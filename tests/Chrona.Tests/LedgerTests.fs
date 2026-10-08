/// Revision-safe lifecycle transitions (WI-0021): requirements expansion
/// 12, 13, 21, 24, 25 and scenario tests 10-14, 32, 37 and 38 of section 43.
module Chrona.Tests.LedgerTests

open System
open Xunit
open Chrona.Tests.Support
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Domain.Ledger

let private zone =
    match tryZone "America/New_York" with
    | Ok z -> z
    | Error e -> failwith $"{e}"

let private day = DateOnly(2026, 10, 7)
let private at (h: int) (m: int) = DateTimeOffset(2026, 10, 7, h + 4, m, 0, TimeSpan.Zero) // EDT
let private context = { Performer = "ACTOR-1"; At = DateTimeOffset(2026, 10, 7, 22, 0, 0, TimeSpan.Zero); Source = "chrona-web"; Zone = zone; References = references; CorrelationId = Some "corr-1" }

let private evidence id = { Id = id; Url = $"https://example.test/{id}"; Kind = "commit"; Label = id; CapturedAt = at 8 0; Hash = None }

let private activity id (startH, startM) minutes =
    let start = at startH startM

    { ActivityId = id
      OrganizationId = "ORG-1"
      ActorId = "ACTOR-1"
      Occurrence = occurrence zone start
      Timing = Interval(start, start.AddMinutes(float minutes))
      Minutes = minutes
      Classification =
        { ProjectId = "PRJ-1"; ClientId = None; EngagementId = None; ActivityTypeId = "ACT-DEV"; Tags = []; Description = "Work"; BusinessPurpose = "Delivery" }
      EntryMethod = Manual
      Billability = Billable
      BillingReference = noBillingReference
      Record = Recorded
      Review = Unsubmitted
      Publication = Unpublished
      Revision = 1
      CreatedAt = at 8 0
      LastChangedAt = at 8 0
      Reason = None
      WorkItemRef = None
      ExternalRef = None
      Evidence = []
      Lineage = [] }

let private ok result =
    match result with
    | Ok ledger -> ledger
    | Error problems -> failwith $"{problems}"

let private refused result =
    match result with
    | Ok _ -> failwith "expected a refusal"
    | Error problems -> problems

let private run commands = commands |> List.fold (fun ledger command -> execute context ledger command |> ok) empty
let private get id (ledger: Ledger) = ledger.Activities[id]

[<Fact>]
let ``scenario 10 and 32: a command against a stale revision is a conflict, never last-write-wins`` () =
    let ledger = run [ Record(activity "A1" (9, 0) 60) ]
    let amend description = { Classification = Some { (get "A1" ledger).Classification with Description = description }; Billability = None; BillingReference = None; Retime = None; Reason = "fix" }
    // Two devices both edit revision 1. The first wins; the second is refused.
    let first = execute context ledger (Amend("A1", 1, amend "Device A")) |> ok
    Assert.Equal<Diagnostic list>([ RevisionConflict(1, 2) ], execute context first (Amend("A1", 1, amend "Device B")) |> refused)
    Assert.Equal("Device A", (get "A1" first).Classification.Description)
    Assert.Equal<Diagnostic list>([ UnknownActivity "A9" ], execute context first (Void("A9", 1, "x")) |> refused)

[<Fact>]
let ``scenario 11: void keeps the record and frees the time; restore rechecks overlap`` () =
    let ledger = run [ Record(activity "A1" (9, 0) 60); Void("A1", 1, "entered twice") ]
    Assert.Equal(Voided "entered twice", (get "A1" ledger).Record)
    Assert.Equal(0, activeMinutes ledger)
    // The freed slot can be reused...
    let reused = execute context ledger (Record(activity "A2" (9, 30) 30)) |> ok
    // ...and then restoring A1 would overlap, so it is refused.
    Assert.Equal<Diagnostic list>([ OverlapsActivity "A2" ], execute context reused (Restore("A1", 2)) |> refused)
    let restored = execute context ledger (Restore("A1", 2)) |> ok
    Assert.Equal(Recorded, (get "A1" restored).Record)
    Assert.Equal(3, (get "A1" restored).Revision)
    Assert.Equal<Diagnostic list>([ IllegalTransition("Recorded", "restore") ], execute context restored (Restore("A1", 3)) |> refused)

[<Fact>]
let ``scenarios 12 and 14: split preserves total time, lineage and assigns evidence once`` () =
    let source = { activity "A1" (9, 0) 90 with Evidence = [ evidence "E1"; evidence "E2" ] }
    let ledger = run [ Record source ]
    let parts =
        [ { ActivityId = "A1a"; Minutes = 60; Classification = None; EvidenceIds = [ "E1" ] }
          { ActivityId = "A1b"; Minutes = 30; Classification = Some { source.Classification with ProjectId = "PRJ-2" }; EvidenceIds = [ "E2" ] } ]
    let split = execute context ledger (Split("A1", 1, parts)) |> ok
    Assert.Equal(Superseded [ "A1a"; "A1b" ], (get "A1" split).Record)
    Assert.Equal(90, activeMinutes split)
    Assert.Equal(Interval(at 9 0, at 10 0), (get "A1a" split).Timing)
    Assert.Equal(Interval(at 10 0, at 10 30), (get "A1b" split).Timing)
    Assert.Equal<string list>([ "A1" ], (get "A1b" split).Lineage)
    Assert.Equal("PRJ-2", (get "A1b" split).Classification.ProjectId)
    Assert.Equal<string list>([ "E2" ], (get "A1b" split).Evidence |> List.map _.Id)
    // Durations must add up, and evidence may not be duplicated.
    let bad = [ { parts[0] with Minutes = 50 }; parts[1] ]
    Assert.Equal<Diagnostic list>([ SplitDurationMismatch(90, 80) ], execute context ledger (Split("A1", 1, bad)) |> refused)
    let duplicated = [ parts[0]; { parts[1] with EvidenceIds = [ "E1" ] } ]
    Assert.Equal<Diagnostic list>([ EvidenceAssignmentInvalid ], execute context ledger (Split("A1", 1, duplicated)) |> refused)

[<Fact>]
let ``scenarios 13 and 14: merge keeps every source id and supersedes, never deletes`` () =
    let ledger =
        run
            [ Record { activity "A1" (9, 0) 30 with Evidence = [ evidence "E1" ] }
              Record { activity "A2" (9, 30) 45 with Evidence = [ evidence "E2" ] } ]

    let merged = execute context ledger (Merge([ "A2", 1; "A1", 1 ], "M1", None)) |> ok
    let m = get "M1" merged
    Assert.Equal<string list>([ "A1"; "A2" ], m.Lineage)
    Assert.Equal(75, m.Minutes)
    Assert.Equal(Interval(at 9 0, at 10 15), m.Timing) // contiguous sources
    Assert.Equal<string list>([ "E1"; "E2" ], m.Evidence |> List.map _.Id)
    Assert.Equal(Superseded [ "M1" ], (get "A1" merged).Record)
    Assert.Equal(3, merged.Activities.Count)
    Assert.Equal(75, activeMinutes merged)

[<Fact>]
let ``merge refuses incompatible sources explicitly`` () =
    // Each candidate is contiguous with A1, so each refusal has one reason.
    let other = { activity "B1" (9, 30) 30 with ActorId = "ACTOR-2" }
    let ledger = run [ Record(activity "A1" (9, 0) 30); Record other; Record { activity "A3" (9, 30) 30 with Publication = ReadyForPublication } ]
    Assert.Equal<Diagnostic list>([ ActorMismatch ], execute context ledger (Merge([ "A1", 1; "B1", 1 ], "M1", None)) |> refused)
    Assert.Equal<Diagnostic list>([ PublicationStateConflict "sources have different publication states" ], execute context ledger (Merge([ "A1", 1; "A3", 1 ], "M1", None)) |> refused)
    Assert.Equal<Diagnostic list>([ RevisionConflict(2, 1) ], execute context ledger (Merge([ "A1", 2; "A3", 1 ], "M1", None)) |> refused)

[<Fact>]
let ``scenarios 37 and 38: changing published or invoiced time creates a correction obligation`` () =
    let ledger = run [ Record { activity "A1" (9, 0) 60 with Publication = Published }; Record { activity "A2" (11, 0) 60 with Publication = InvoicedExternally } ]
    let amendment = { Classification = None; Billability = None; BillingReference = None; Retime = None; Reason = "client asked" }
    Assert.Equal(AdjustmentRequired, (get "A1" (execute context ledger (Amend("A1", 1, amendment)) |> ok)).Publication)
    Assert.Equal(AdjustmentRequired, (get "A1" (execute context ledger (Void("A1", 1, "wrong")) |> ok)).Publication)
    // Chrona may correct its own truth even after Summa invoiced the time,
    // but never silently: the record now requires downstream reconciliation.
    Assert.Equal(AdjustmentRequired, (get "A2" (execute context ledger (Amend("A2", 1, amendment)) |> ok)).Publication)
    let parts = [ { ActivityId = "x"; Minutes = 30; Classification = None; EvidenceIds = [] }; { ActivityId = "y"; Minutes = 30; Classification = None; EvidenceIds = [] } ]
    let split = execute context ledger (Split("A1", 1, parts)) |> ok
    Assert.Equal(AdjustmentRequired, (get "A1" split).Publication)
    Assert.Equal(Unpublished, (get "x" split).Publication)

[<Fact>]
let ``scenario 22: changing reviewed time reopens its review`` () =
    let ledger = run [ Record { activity "A1" (9, 0) 60 with Review = Approved }; Record { activity "A2" (11, 0) 60 with Review = Submitted } ]
    let amendment = { Classification = None; Billability = None; BillingReference = None; Retime = None; Reason = "late correction" }
    Assert.Equal(Reopened, (get "A1" (execute context ledger (Amend("A1", 1, amendment)) |> ok)).Review)
    Assert.Equal(Reopened, (get "A2" (execute context ledger (Void("A2", 1, "wrong")) |> ok)).Review)

[<Fact>]
let ``amending billability moves the publication state with it, and retiming rechecks overlap`` () =
    let ledger = run [ Record(activity "A1" (9, 0) 60); Record(activity "A2" (11, 0) 60) ]
    let nonBillable = execute context ledger (Amend("A1", 1, { Classification = None; Billability = Some NonBillable; BillingReference = None; Retime = None; Reason = "internal" })) |> ok
    Assert.Equal(NotBillable, (get "A1" nonBillable).Publication)
    let retime = Some(occurrence zone (at 10 30), Interval(at 10 30, at 11 30), 60)
    Assert.Equal<Diagnostic list>([ OverlapsActivity "A2" ], execute context ledger (Amend("A1", 1, { Classification = None; Billability = None; BillingReference = None; Retime = retime; Reason = "moved" })) |> refused)

[<Fact>]
let ``every transition is audited with revisions, reason and correlation; refusals are not`` () =
    let ledger =
        run
            [ Record(activity "A1" (9, 0) 60)
              LinkEvidence("A1", 1, evidence "E1")
              UnlinkEvidence("A1", 2, "E1")
              Void("A1", 3, "duplicate")
              Restore("A1", 4) ]

    Assert.Equal<string list>([ "create"; "evidence-link"; "evidence-unlink"; "void"; "restore" ], ledger.Audit |> List.map _.Command)
    let voided = ledger.Audit[3]
    Assert.Equal<(string * int) list>([ "A1", 3 ], voided.PriorRevisions)
    Assert.Equal<(string * int) list>([ "A1", 4 ], voided.ResultingRevisions)
    Assert.Equal(Some "duplicate", voided.Reason)
    Assert.Equal(Some "corr-1", voided.CorrelationId)
    Assert.Equal("ACTOR-1", voided.Performer)
    Assert.Equal("chrona-web", voided.Source)
    Assert.Empty(ledger.Audit[0].PriorRevisions)
    // A refused command leaves the ledger and its audit exactly as they were.
    Assert.True(Result.isError (execute context ledger (Void("A1", 1, "stale"))))
    Assert.Equal(5, ledger.Audit.Length)
