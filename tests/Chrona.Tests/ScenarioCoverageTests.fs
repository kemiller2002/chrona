/// Every required scenario (43) is tested by name (WI-0040).
module Chrona.Tests.ScenarioCoverageTests

open System.IO
open System.Text.RegularExpressions
open Xunit
open Chrona.Tests.Support

/// The scenarios section 43 requires, by number.
let private required () =
    let text = readRepoFile "docs/requirements/CHRONA-REQUIREMENTS-EXPANSION.txt"
    let start = text.IndexOf "## 43. Required scenario tests"
    let finish = text.IndexOf("## 44.", start)

    Regex.Matches(text.Substring(start, finish - start), @"(?m)^(\d+)\. (.+)$")
    |> Seq.map (fun m -> int m.Groups[1].Value, m.Groups[2].Value.Trim())
    |> List.ofSeq

/// The scenario numbers a test's name claims: "scenario 9", "scenarios 12
/// and 14", "scenarios 23, 35 and 36", "scenarios 18-22".
let private claimed (name: string) =
    match Regex.Match(name, @"^scenarios? ([0-9][0-9, and\-]*?)(:|$)") with
    | m when m.Success ->
        Regex.Matches(m.Groups[1].Value, @"(\d+)(?:-(\d+))?")
        |> Seq.collect (fun part ->
            let first = int part.Groups[1].Value

            if part.Groups[2].Success then
                seq { first .. int part.Groups[2].Value }
            else
                Seq.singleton first)
        |> List.ofSeq
    | _ -> []

/// Every test name in the .NET and browser suites.
let private testNames () =
    let dotnet =
        Directory.GetFiles(repoFile "tests/Chrona.Tests", "*.fs")
        |> Seq.collect (fun file -> Regex.Matches(File.ReadAllText file, @"(?m)^let ``(.+?)``") |> Seq.map _.Groups[1].Value)

    let browser =
        Directory.GetFiles(repoFile "tests/browser", "*.spec.js")
        |> Seq.collect (fun file ->
            Regex.Matches(File.ReadAllText file, "(?:test|test\\.describe|describe)\\(\"([^\"]+)\"") |> Seq.map _.Groups[1].Value)

    Seq.append dotnet browser |> List.ofSeq

[<Fact>]
let ``section 43 requires 44 scenarios`` () =
    Assert.Equal<int list>([ 1..44 ], required () |> List.map fst)

[<Fact>]
let ``every required scenario has a test that names it`` () =
    let covered = testNames () |> List.collect claimed |> Set.ofList
    let missing = required () |> List.filter (fun (number, _) -> not (covered.Contains number))
    Assert.True(missing.IsEmpty, $"Scenarios with no test that names them: %A{missing}")

[<Fact>]
let ``a test claims only scenarios that exist`` () =
    let known = required () |> List.map fst |> Set.ofList
    let stray = testNames () |> List.collect claimed |> List.filter (known.Contains >> not)
    Assert.Empty(stray)

[<Fact>]
let ``scenario claims are read as written`` () =
    Assert.Equal<int list>([ 9 ], claimed "scenario 9: overlapping timers")
    Assert.Equal<int list>([ 12; 14 ], claimed "scenarios 12 and 14: split")
    Assert.Equal<int list>([ 23; 35; 36 ], claimed "scenarios 23, 35 and 36: gates")
    Assert.Equal<int list>([ 18; 19; 20; 21; 22 ], claimed "scenarios 18-22: review")
    Assert.Equal<int list>([ 42 ], claimed "scenario 42")
    Assert.Empty(claimed "a test about scenario 3 in passing")
