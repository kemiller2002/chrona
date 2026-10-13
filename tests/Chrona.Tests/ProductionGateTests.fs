/// The production completion gate names real evidence (44, WI-0040).
module Chrona.Tests.ProductionGateTests

open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Xunit
open Chrona.Tests.Support

let private gate = "docs/requirements/production-completion-gate.md"

/// The items section 44 requires.
let private required () =
    let text = readRepoFile "docs/requirements/CHRONA-REQUIREMENTS-EXPANSION.txt"
    let start = text.IndexOf "## 44. Production completion gate"
    let finish = text.IndexOf("## 45.", start)

    Regex.Matches(text.Substring(start, finish - start), @"(?m)^- (.+)$") |> Seq.map _.Groups[1].Value.Trim() |> List.ofSeq

/// The gate's rows: item, status and evidence.
let private rows () =
    Regex.Matches(readRepoFile gate, @"(?m)^\| ([^|]+) \| ([^|]+) \| (.+) \|$")
    |> Seq.map (fun m -> m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim(), m.Groups[3].Value)
    |> Seq.filter (fun (item, _, _) -> item <> "Item" && not (item.StartsWith "---"))
    |> List.ofSeq

/// Whether a test of this name is in this file: an F# test in the .NET
/// suite, or a test or group in the browser suite.
let private hasTest (file: string) (name: string) =
    if file.EndsWith ".fs" then
        let path = repoFile $"tests/Chrona.Tests/{file}"
        File.Exists path && File.ReadAllText(path).Contains($"let ``{name}``")
    else
        let path = repoFile $"tests/browser/{file}"
        File.Exists path && File.ReadAllText(path).Contains($"(\"{name}\"")

/// Every piece of evidence a row names that is missing.
let private missing (evidence: string) =
    Regex.Matches(evidence, "`([^`]+)`")
    |> Seq.map _.Groups[1].Value
    |> Seq.choose (fun span ->
        match Regex.Match(span, @"^([A-Za-z.\-]+\.(?:fs|spec\.js)): (.+)$") with
        | m when m.Success -> if hasTest m.Groups[1].Value m.Groups[2].Value then None else Some span
        | _ when span.Contains '/' && not (span.Contains ' ') ->
            if File.Exists(repoFile span) || Directory.Exists(repoFile span) then None else Some span
        | _ -> None)
    |> List.ofSeq

/// The work items still open.
let private openItems () =
    use queue = JsonDocument.Parse(readRepoFile ".ros/work/queue.json")

    queue.RootElement.GetProperty("items").EnumerateArray()
    |> Seq.filter (fun item -> item.GetProperty("status").GetString() <> "complete")
    |> Seq.map (fun item -> item.GetProperty("id").GetString())
    |> Set.ofSeq

[<Fact>]
let ``every item of section 44 has its row, in order`` () =
    Assert.Equal<string list>(required (), rows () |> List.map (fun (item, _, _) -> item))

[<Fact>]
let ``every test, file and decision the gate names exists`` () =
    let absent = rows () |> List.collect (fun (item, _, evidence) -> missing evidence |> List.map (fun span -> item, span))
    Assert.True(absent.IsEmpty, $"The gate names evidence that does not exist: %A{absent}")

[<Fact>]
let ``every item is demonstrated, or names the open work item it waits on`` () =
    let opened = openItems ()

    for item, status, _ in rows () do
        match status with
        | "demonstrated" -> ()
        | waiting ->
            let m = Regex.Match(waiting, @"^not yet: (WI-\d{4})$")
            Assert.True(m.Success, $"{item}: '{waiting}' is neither demonstrated nor waiting on a work item")
            Assert.True(opened.Contains m.Groups[1].Value, $"{item} waits on {m.Groups[1].Value}, which is not open")

[<Fact>]
let ``the evidence rule finds the evidence, and catches a test that is not there`` () =
    Assert.Empty(missing "`TimerTests.fs: scenario 2: a timer records exact minutes as an interval`")
    Assert.Equal<string list>([ "TimerTests.fs: no such test" ], missing "`TimerTests.fs: no such test`")
    Assert.Equal<string list>([ "docs/nowhere.md" ], missing "`docs/nowhere.md`")
