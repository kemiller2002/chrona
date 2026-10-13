/// The measurement baseline index names only what exists (WI-0078).
module Chrona.Tests.MeasurementIndexTests

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open Xunit
open Chrona.Tests.Support

let private index = "docs/measurement/baseline-sources.md"

/// Every backticked span of the index.
let private spans () =
    Regex.Matches(readRepoFile index, "`([^`]+)`") |> Seq.map _.Groups[1].Value |> Seq.distinct |> List.ofSeq

/// The names at the repository's root.
let private roots =
    Directory.EnumerateFileSystemEntries repositoryRoot |> Seq.map Path.GetFileName |> Set.ofSeq

/// A span is a repository path when it has no spaces and its first segment
/// names something at the repository root.
let private isPath (span: string) =
    not (span.Contains ' ') && roots.Contains(span.Split('/').[0])

/// Whether a path exists, segment by segment. A segment holding
/// `<placeholder>` or `*` is a pattern that something there must match; a
/// path ending in `/` must be a folder.
let private exists (path: string) =
    let folder = path.EndsWith '/'
    let segments = path.TrimEnd('/').Split '/' |> List.ofArray

    let matches (segment: string) (name: string) =
        Regex.IsMatch(name, "^" + Regex.Replace(Regex.Escape segment, @"\\\*|<[^>]*>", ".+") + "$")

    let rec walk (at: string) (rest: string list) =
        match rest with
        | [] -> if folder then Directory.Exists at else File.Exists at || Directory.Exists at
        | segment :: rest when Directory.Exists at ->
            Directory.EnumerateFileSystemEntries at
            |> Seq.filter (fun entry -> matches segment (Path.GetFileName entry))
            |> Seq.exists (fun entry -> walk entry rest)
        | _ -> false

    walk repositoryRoot segments

/// Whether git knows this commit in the repository's history.
let private isCommit (hash: string) =
    let start =
        ProcessStartInfo("git", $"cat-file -e {hash}^{{commit}}", WorkingDirectory = repositoryRoot, RedirectStandardError = true, RedirectStandardOutput = true)

    use git = Process.Start start
    git.WaitForExit()
    git.ExitCode = 0

[<Fact>]
let ``every repository path the measurement index names exists`` () =
    let paths = spans () |> List.filter isPath
    // The index names real paths, so the rule above must find them.
    Assert.Contains(".ros/work/queue.json", paths)
    Assert.Contains(".github/workflows/pages.yml", paths)
    Assert.Contains(".ros/executions/<EXE-id>/", paths)
    Assert.Contains("research/decisions/*.md", paths)
    let missing = paths |> List.filter (exists >> not)
    Assert.True(missing.IsEmpty, $"The index names paths that do not exist: %A{missing}")

[<Fact>]
let ``every commit the measurement index names is in the repository's history`` () =
    let hashes = spans () |> List.filter (fun span -> Regex.IsMatch(span, "^[0-9a-f]{7,40}$"))
    Assert.True(hashes.Length >= 20, $"Only {hashes.Length} commits found in the index")
    let unknown = hashes |> List.filter (isCommit >> not)
    Assert.True(unknown.IsEmpty, $"The index names commits that are not in the history: %A{unknown}")

[<Fact>]
let ``the path rule catches a wrong path`` () =
    Assert.True(isPath ".ros/work/nowhere.json")
    Assert.False(exists ".ros/work/nowhere.json")
    Assert.False(exists ".ros/nowhere/<EXE-id>.json")
    Assert.False(isPath "kemiller2002/chrona")
    Assert.False(isCommit "0000000")
