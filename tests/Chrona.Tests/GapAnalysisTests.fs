/// The requirement gap analysis stays complete and consistent: every section
/// and subsection of the requirements expansion has exactly one status row,
/// and both summaries are the counts of their columns.
module Chrona.Tests.GapAnalysisTests

open System.Text.RegularExpressions
open Xunit
open Chrona.Tests.Support

let private source = readRepoFile "docs/requirements/CHRONA-REQUIREMENTS-EXPANSION.txt"
let private analysis = readRepoFile "docs/requirements/implementation-gap-analysis.md"

/// `## 10. Timer additions` -> CHX-100 and `### 10.3 Cross-midnight` ->
/// CHX-103. A section with subsections is represented by its subsections.
let private expectedIds =
    let headings =
        Regex.Matches(source, @"(?m)^(##|###) (\d+)(?:\.(\d+))?\.? ")
        |> Seq.map (fun m -> int m.Groups[2].Value, (if m.Groups[3].Success then Some(int m.Groups[3].Value) else None))
        |> Seq.toList

    let withSubsections = headings |> List.choose (fun (s, sub) -> sub |> Option.map (fun _ -> s)) |> Set.ofList

    headings
    |> List.choose (fun (section, sub) ->
        match sub with
        | Some m -> Some(sprintf "CHX-%02d%d" section m)
        | None when withSubsections.Contains section -> None
        | None -> Some(sprintf "CHX-%02d0" section))
    |> Set.ofList

let private rows =
    Regex.Matches(analysis, @"(?m)^\| (CHX-\d{3}) \| (tested|partial|missing) \| (tested|partial|missing) \|")
    |> Seq.map (fun m -> m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)
    |> Seq.toList

[<Fact>]
let ``every requirement section has exactly one status row`` () =
    let ids = rows |> List.map (fun (id, _, _) -> id)
    Assert.Equal(63, expectedIds.Count)
    Assert.Equal<string list>(List.distinct ids, ids)
    Assert.Equal<Set<string>>(expectedIds, Set.ofList ids)

let private summary (heading: string) =
    let section = analysis.Substring(analysis.IndexOf heading)
    let m = Regex.Match(section, @"(?m)^\| CHRONA-REQUIREMENTS-EXPANSION \| (\d+) \| (\d+) \| (\d+) \| (\d+) \|")
    Assert.True(m.Success, heading)
    [ for i in 1..4 -> int m.Groups[i].Value ]

let private counts (column: string * string * string -> string) =
    let count status = rows |> List.filter (fun row -> column row = status) |> List.length
    [ rows.Length; count "tested"; count "partial"; count "missing" ]

[<Fact>]
let ``both summaries are the counts of their columns`` () =
    Assert.Equal<int list>(counts (fun (_, baseline, _) -> baseline), summary "## Summary")
    Assert.Equal<int list>(counts (fun (_, _, current) -> current), summary "## Coverage after this programme")

/// Every section that is not yet `tested` is planned: an open work item in
/// the Praxis queue (captured, ready, active or blocked) names it, either
/// directly (`CHX-240`) or inside a range (`CHX-024..027`). A gap that no
/// open work item names would be silently dropped from the backlog.
let private openWorkText =
    use queue = System.Text.Json.JsonDocument.Parse(readRepoFile ".ros/work/queue.json")

    queue.RootElement.GetProperty("items").EnumerateArray()
    |> Seq.filter (fun item ->
        match item.GetProperty("status").GetString() with
        | "complete"
        | "abandoned" -> false
        | _ -> true)
    |> Seq.map (fun item ->
        let text (name: string) =
            match item.TryGetProperty name with
            | true, value when value.ValueKind = System.Text.Json.JsonValueKind.String -> value.GetString()
            | _ -> ""

        text "title" + "\n" + text "description")
    |> String.concat "\n"

let private plannedIds =
    let known = rows |> List.map (fun (id, _, _) -> id)
    let number (id: string) = int (id.Substring 4)

    let direct =
        Regex.Matches(openWorkText, @"CHX-\d{3}") |> Seq.map _.Value

    let ranges =
        Regex.Matches(openWorkText, @"CHX-(\d{3})\.\.(?:CHX-)?(\d{3})")
        |> Seq.collect (fun m ->
            let low, high = int m.Groups[1].Value, int m.Groups[2].Value
            known |> List.filter (fun id -> number id >= low && number id <= high))

    Seq.append direct ranges |> Set.ofSeq

[<Fact>]
let ``every section that is not yet tested is named by an open work item`` () =
    let unplanned =
        rows
        |> List.filter (fun (id, _, current) -> current <> "tested" && not (plannedIds.Contains id))
        |> List.map (fun (id, _, _) -> id)

    Assert.Empty unplanned
