/// Search, reporting and export (WI-0049): requirements expansion 27-29.
module Chrona.Tests.ReportTests

open System
open System.Text.Json
open Xunit
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Domain.Reports

let private zone =
    match tryZone "America/New_York" with
    | Ok z -> z
    | Error e -> failwith $"{e}"

let private at (day: int) (h: int) (m: int) = DateTimeOffset(2026, 10, day, h, m, 0, TimeSpan.FromHours -4.0)

let private activity id day (h, m) minutes =
    let start = at day h m

    { ActivityId = id
      OrganizationId = "ORG-1"
      ActorId = "ACTOR-1"
      Occurrence = occurrence zone start
      Timing = Interval(start, start.AddMinutes(float minutes))
      Minutes = minutes
      Classification = { ProjectId = "PRJ-1"; ClientId = Some "CLI-1"; EngagementId = None; ActivityTypeId = "DEV"; Tags = [ "backend" ]; Description = "Implement export"; BusinessPurpose = "Client deliverable" }
      EntryMethod = Manual
      Billability = Billable
      BillingReference = noBillingReference
      Record = Recorded
      Review = Unsubmitted
      Publication = Unpublished
      Revision = 1
      CreatedAt = start
      LastChangedAt = start
      Reason = None
      WorkItemRef = Some "GH-42"
      ExternalRef = None
      Evidence = []
      Lineage = [] }

let private policies = [ Chrona.Domain.Billing.legacyDefault "ORG-1" ]

let private activities =
    [ activity "A1" 5 (9, 0) 37
      { activity "A2" 6 (9, 0) 30 with EntryMethod = Timer; Billability = NonBillable; Classification = { (activity "x" 6 (9, 0) 30).Classification with ProjectId = "PRJ-2"; Tags = [ "backend"; "ops" ]; Description = "Rotate keys, \"urgent\"" } }
      { activity "A3" 7 (9, 0) 20 with Billability = PendingClassification; Review = Reopened; Classification = { (activity "x" 7 (9, 0) 20).Classification with ActivityTypeId = "MTG"; Tags = []; ClientId = None } }
      { activity "A4" 7 (11, 0) 60 with Record = Voided "duplicate" }
      { activity "A5" 8 (9, 0) 45 with Record = Superseded [ "A6" ] }
      { activity "A6" 8 (9, 0) 45 with Review = Approved; Publication = AdjustmentRequired }
      { activity "B1" 8 (13, 0) 15 with ActorId = "ACTOR-2" }
      activity "OUT" 12 (9, 0) 99 ]

let private october = between (DateOnly(2026, 10, 5)) (DateOnly(2026, 10, 8))
let private ids (filter: Filter) = select filter activities |> List.map _.ActivityId

[<Fact>]
let ``a report covers its dates; superseded records never appear and voided ones only on request`` () =
    Assert.Equal<string list>([ "A1"; "A2"; "A3"; "A6"; "B1" ], ids october)
    Assert.Equal<string list>([ "A1"; "A2"; "A3"; "A4"; "A6"; "B1" ], ids { october with IncludeRemoved = true })
    // Shown, never totalled.
    Assert.Equal((totals policies (select october activities)).ExactMinutes, (totals policies (select { october with IncludeRemoved = true } activities)).ExactMinutes)

[<Fact>]
let ``search filters by every dimension, and words match anywhere, ignoring case`` () =
    Assert.Equal<string list>([ "B1" ], ids { october with ActorIds = [ "ACTOR-2" ] })
    Assert.Equal<string list>([ "A2" ], ids { october with ProjectIds = [ "PRJ-2" ] })
    Assert.Equal<string list>([ "A3" ], ids { october with ActivityTypeIds = [ "MTG" ] })
    Assert.Equal<string list>([ "A1"; "A2"; "A6"; "B1" ], ids { october with ClientIds = [ "CLI-1" ] })
    Assert.Equal<string list>([ "A2" ], ids { october with Tags = [ "ops" ] })
    Assert.Equal<string list>([ "A2" ], ids { october with Billability = [ NonBillable ] })
    Assert.Equal<string list>([ "A2" ], ids { october with EntryKinds = [ TimerEntries ] })
    Assert.Equal<string list>([ "A6" ], ids { october with ReviewStates = [ "approved" ] })
    Assert.Equal<string list>([ "A6" ], ids { october with PublicationStates = [ "adjustment-required" ] })
    Assert.Equal<string list>([ "A2" ], ids { october with Text = "ROTATE urgent" })
    Assert.Equal<string list>([ "A1"; "A2"; "A3"; "A6"; "B1" ], ids { october with Text = "gh-42 deliverable" })
    Assert.Empty(ids { october with Text = "rotate nothing" })

[<Fact>]
let ``totals keep exact time apart from billable time and count review and publication`` () =
    let t = totals policies (select october activities)
    Assert.Equal(5, t.Count)
    Assert.Equal(37 + 30 + 20 + 45 + 15, t.ExactMinutes)
    // Six-minute up per billable activity: 37 -> 42, 45 -> 48, 15 -> 18.
    Assert.Equal(42 + 48 + 18, t.BillableMinutes)
    Assert.Equal(30, t.NonBillableMinutes)
    Assert.Equal(20, t.UnclassifiedMinutes)
    Assert.Equal((30, 117), (t.TimerMinutes, t.ManualMinutes))
    Assert.Equal((45, 102), (t.ApprovedMinutes, t.UnapprovedMinutes))
    Assert.Equal((0, 147), (t.PublishedMinutes, t.UnpublishedMinutes))
    Assert.Equal((1, 1), (t.AmendedAfterReview, t.CorrectedAfterPublication))

[<Fact>]
let ``groups total each key; an activity counts under each of its tags`` () =
    let rows = select october activities
    Assert.Equal<(string * int) list>([ "2026-10-05", 37; "2026-10-06", 30; "2026-10-07", 20; "2026-10-08", 60 ], groupBy policies ByDay rows |> List.map (fun (k, t) -> k, t.ExactMinutes))
    Assert.Equal<(string * int) list>([ "PRJ-1", 117; "PRJ-2", 30 ], groupBy policies ByProject rows |> List.map (fun (k, t) -> k, t.ExactMinutes))
    Assert.Equal<(string * int) list>([ "", 20; "backend", 127; "ops", 30 ], groupBy policies ByTag rows |> List.map (fun (k, t) -> k, t.ExactMinutes))
    Assert.Equal<(string * int) list>([ "ACTOR-1", 132; "ACTOR-2", 15 ], groupBy policies ByActor rows |> List.map (fun (k, t) -> k, t.ExactMinutes))

let private header = { OrganizationId = "ORG-1"; GeneratedAt = DateTimeOffset(2026, 10, 9, 8, 30, 0, TimeSpan.FromHours -4.0); Filter = october }

[<Fact>]
let ``CSV export is deterministic, identifies itself and quotes what it must`` () =
    let text = csv policies header activities
    // The same records in any order export byte for byte the same.
    Assert.Equal(text, csv policies header (List.rev activities))
    let lines = text.Split('\n')
    Assert.Equal("# schema: chrona.time-report/1", lines[0])
    Assert.Equal("# organization: ORG-1", lines[1])
    Assert.Equal("# generatedAt: 2026-10-09T12:30:00Z", lines[2])
    Assert.Contains("# filter.from: 2026-10-05", lines)
    Assert.Contains("# filter.to: 2026-10-08", lines)
    Assert.Contains("# semantics.exactMinutes: Recorded time in whole minutes. It is authoritative and is never rounded.", lines)
    let columnRow = lines |> Array.find (fun l -> not (l.StartsWith "#"))
    Assert.Equal(String.Join(",", columnNames), columnRow)
    let a2 = lines |> Array.find (fun l -> l.StartsWith "A2,")
    Assert.Contains(",\"Rotate keys, \"\"urgent\"\"\",", a2)
    Assert.Contains(",backend;ops,", a2)
    let a1 = lines |> Array.find (fun l -> l.StartsWith "A1,")
    Assert.Contains(",37,42,legacy-six-minute-up@1,billable,", a1)
    Assert.EndsWith("\n", text)
    Assert.DoesNotContain("\r", text)

[<Fact>]
let ``every activity exports the same named columns`` () =
    for a in activities do
        Assert.Equal<string list>(columnNames, row policies a |> List.map fst)

[<Fact>]
let ``JSON export carries the same report with typed numbers and stable keys`` () =
    let text = json policies header activities
    Assert.Equal(text, json policies header (List.rev activities))
    use document = JsonDocument.Parse text
    let root = document.RootElement
    Assert.Equal<string list>([ "schema"; "organization"; "generatedAt"; "filter"; "semantics"; "totals"; "activities" ], root.EnumerateObject() |> Seq.map _.Name |> Seq.toList)
    Assert.Equal("chrona.time-report/1", root.GetProperty("schema").GetString())
    Assert.Equal(147, root.GetProperty("totals").GetProperty("exactMinutes").GetInt32())
    Assert.Equal(108, root.GetProperty("totals").GetProperty("billableMinutes").GetInt32())
    let first = root.GetProperty("activities")[0]
    Assert.Equal("A1", first.GetProperty("activityId").GetString())
    Assert.Equal(37, first.GetProperty("exactMinutes").GetInt32())
    Assert.Equal("2026-10-05T13:00:00Z", first.GetProperty("startUtc").GetString())
