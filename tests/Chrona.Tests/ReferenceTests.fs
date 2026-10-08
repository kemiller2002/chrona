/// Reference data (WI-0045): requirements expansion 4 and 8, and scenario
/// test 15 (archived references) of section 43.
module Chrona.Tests.ReferenceTests

open System
open Xunit
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Domain.Reference
open Chrona.Tests.Support

module Ledger = Chrona.Domain.Ledger
module Entry = Chrona.Domain.ManualEntry

let private zone =
    match tryZone "America/New_York" with
    | Ok z -> z
    | Error e -> failwith $"{e}"

let private at h m = DateTimeOffset(2026, 10, 7, h, m, 0, TimeSpan.FromHours -4.0)

let private ok result =
    match result with
    | Ok value -> value
    | Error problems -> failwith $"{problems}"

let private refused result =
    match result with
    | Ok _ -> failwith "expected a refusal"
    | Error problems -> problems

let private run commands =
    commands |> List.fold (fun catalogue command -> execute catalogue command |> ok) (empty "ORG-1")

let private catalogue =
    run
        [ Add(Project, "PRJ-1", "HelixNote")
          Add(Project, "PRJ-OLD", "Retired project")
          Add(ActivityType, "ACT-DEV", "Software development")
          Add(Tag, "backend", "Backend")
          Add(Tag, "legacy", "Legacy")
          Archive(Project, "PRJ-OLD", 1)
          Archive(Tag, "legacy", 1) ]

let private work =
    { ProjectId = "PRJ-1"; ClientId = None; EngagementId = None; ActivityTypeId = "ACT-DEV"; Tags = [ "backend" ]; Description = "Work"; BusinessPurpose = "Delivery" }

[<Fact>]
let ``items have stable ids, a revision and an active or archived state`` () =
    let c = run [ Add(Project, "PRJ-1", "  HelixNote ") ]
    Assert.Equal(Some "HelixNote", tryName Project "PRJ-1" c)
    let renamed = execute c (Rename(Project, "PRJ-1", 1, "HelixNote app")) |> ok
    Assert.Equal({ Kind = Project; Id = "PRJ-1"; Name = "HelixNote app"; Status = Active; Ownership = Owned; Revision = 2 }, renamed.Items[(Project, "PRJ-1")])
    Assert.Equal<Diagnostic list>([ RevisionConflict(1, 2) ], execute renamed (Archive(Project, "PRJ-1", 1)) |> refused)
    let archived = execute renamed (Archive(Project, "PRJ-1", 2)) |> ok
    Assert.Equal<Diagnostic list>([ IllegalTransition("Archived", "archive") ], execute archived (Archive(Project, "PRJ-1", 3)) |> refused)
    Assert.Equal(Active, (execute archived (Reactivate(Project, "PRJ-1", 3)) |> ok).Items[(Project, "PRJ-1")].Status)

[<Fact>]
let ``adding refuses blanks and duplicate ids; the same id may name different kinds`` () =
    let c = run [ Add(Project, "X", "X") ]
    Assert.Equal<Diagnostic list>([ DuplicateReference("project", "X") ], execute c (Add(Project, "X", "Other")) |> refused)
    Assert.Equal<Diagnostic list>([ MissingField "id"; MissingField "name" ], execute c (Add(Tag, " ", "")) |> refused)
    Assert.True(Result.isOk (execute c (Add(Tag, "X", "X"))))

[<Fact>]
let ``only active items are offered for new work, by name`` () =
    let c = run [ Add(Project, "b", "beta"); Add(Project, "a", "Alpha"); Add(Project, "z", "Zulu"); Archive(Project, "z", 1); Add(Tag, "t", "Tag") ]
    Assert.Equal<string list>([ "a"; "b" ], selectable Project c |> List.map _.Id)
    Assert.Equal<string list>([ "a"; "b"; "z" ], all Project c |> List.map _.Id)

[<Fact>]
let ``a concept another system owns is mirrored by its stable id and changed only by its owner`` () =
    let c = execute (empty "ORG-1") (Mirror("summa", Client, "CLI-7", "Northline Studio", Active)) |> ok
    Assert.Equal(MirroredFrom "summa", c.Items[(Client, "CLI-7")].Ownership)
    Assert.Equal<Diagnostic list>([ ReferenceOwnedElsewhere("client", "CLI-7", "summa") ], execute c (Rename(Client, "CLI-7", 1, "Mine now")) |> refused)
    Assert.Equal<Diagnostic list>([ ReferenceOwnedElsewhere("client", "CLI-7", "summa") ], execute c (Archive(Client, "CLI-7", 1)) |> refused)
    let updated = execute c (Mirror("summa", Client, "CLI-7", "Northline Studio LLC", Archived)) |> ok
    Assert.Equal(("Northline Studio LLC", Archived, 2), (updated.Items[(Client, "CLI-7")].Name, updated.Items[(Client, "CLI-7")].Status, updated.Items[(Client, "CLI-7")].Revision))
    // Chrona's own items are never overwritten by a mirror.
    Assert.Equal<Diagnostic list>([ ReferenceOwnedElsewhere("project", "PRJ-1", "Chrona") ], execute catalogue (Mirror("summa", Project, "PRJ-1", "x", Active)) |> refused)

[<Fact>]
let ``new work must name existing, active references`` () =
    Assert.Empty(assignmentProblems catalogue [] work)

    Assert.Equal<Diagnostic list>(
        [ ArchivedReference("project", "PRJ-OLD"); UnknownReference("activityType", "ACT-NONE"); UnknownReference("tag", "nope") ],
        assignmentProblems catalogue [] { work with ProjectId = "PRJ-OLD"; ActivityTypeId = "ACT-NONE"; Tags = [ "nope" ] }
    )

[<Fact>]
let ``scenario 15: archived references stay valid on the records that carry them`` () =
    let old = { work with ProjectId = "PRJ-OLD"; Tags = [ "legacy" ] }
    // Unchanged references are never re-litigated ...
    Assert.Empty(assignmentProblems catalogue [ old ] { old with Description = "Reworded" })
    // ... but newly assigning an archived one is refused, even beside one kept.
    Assert.Equal<Diagnostic list>([ ArchivedReference("project", "PRJ-OLD") ], assignmentProblems catalogue [ work ] { work with ProjectId = "PRJ-OLD" })
    Assert.Equal<Diagnostic list>([ ArchivedReference("tag", "legacy") ], assignmentProblems catalogue [ work ] { work with Tags = [ "backend"; "legacy" ] })

let private context: Ledger.CommandContext = { Performer = "ACTOR-1"; At = at 18 0; Source = "chrona-web"; Zone = zone; References = catalogue; CorrelationId = None }

let private activity id classification =
    { ActivityId = id
      OrganizationId = "ORG-1"
      ActorId = "ACTOR-1"
      Occurrence = occurrence zone (at 9 0)
      Timing = Interval(at 9 0, at 10 0)
      Minutes = 60
      Classification = classification
      EntryMethod = Manual
      Billability = Billable
      BillingReference = { RateReference = Some "RATE-STD"; BillingClass = Some "consulting"; ContractReference = Some "CTR-2026-01" }
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

[<Fact>]
let ``the ledger enforces the assignment rules on record, amend, restore and split`` () =
    Assert.Equal<Diagnostic list>(
        [ ArchivedReference("project", "PRJ-OLD") ],
        Ledger.execute context Ledger.empty (Ledger.Record(activity "A1" { work with ProjectId = "PRJ-OLD" })) |> refused
    )

    // Recorded while PRJ-OLD was active (an earlier catalogue), then archived.
    let earlier = { context with References = run [ Add(Project, "PRJ-OLD", "Retired project"); Add(ActivityType, "ACT-DEV", "Dev"); Add(Tag, "backend", "Backend") ] }
    let ledger = Ledger.execute earlier Ledger.empty (Ledger.Record(activity "A1" { work with ProjectId = "PRJ-OLD" })) |> ok
    let amend classification = Ledger.Amend("A1", 1, { Classification = Some classification; Billability = None; BillingReference = None; Retime = None; Reason = "fix" })
    Assert.True(Result.isOk (Ledger.execute context ledger (amend { work with ProjectId = "PRJ-OLD"; Description = "Reworded" })))
    Assert.True(Result.isOk (Ledger.execute context ledger (amend work)))
    let voided = Ledger.execute context ledger (Ledger.Void("A1", 1, "duplicate")) |> ok
    Assert.True(Result.isOk (Ledger.execute context voided (Ledger.Restore("A1", 2))))

    let parts: Ledger.SplitPart list =
        [ { ActivityId = "A1a"; Minutes = 30; Classification = None; EvidenceIds = [] }
          { ActivityId = "A1b"; Minutes = 30; Classification = Some { work with ActivityTypeId = "ACT-NONE" }; EvidenceIds = [] } ]

    Assert.Equal<Diagnostic list>([ UnknownReference("activityType", "ACT-NONE") ], Ledger.execute context ledger (Ledger.Split("A1", 1, parts)) |> refused)

[<Fact>]
let ``manual entry reports reference problems with every other problem`` () =
    let entry: Entry.ManualEntry =
        { ActivityId = "A1"
          OrganizationId = "ORG-1"
          ActorId = "ACTOR-1"
          Zone = zone
          Timing = Entry.StartAndEnd(DateOnly(2026, 10, 7), TimeOnly(9, 0), TimeOnly(10, 0), None, None)
          Classification = { work with ProjectId = "PRJ-OLD"; BusinessPurpose = "" }
          Billability = Billable
          BillingReference = noBillingReference
          Reason = None
          WorkItemRef = None
          Evidence = [] }

    Assert.Equal<Diagnostic list>(
        [ MissingField "businessPurpose"; ArchivedReference("project", "PRJ-OLD") ],
        Entry.create { Now = at 18 0; HistoricalAfterDays = 7; References = catalogue } [] entry |> refused
    )

[<Fact>]
let ``billing references are retained and amendable, and never change exact time`` () =
    let ledger = Ledger.execute context Ledger.empty (Ledger.Record(activity "A1" work)) |> ok
    let a = ledger.Activities["A1"]
    Assert.Equal(Some "CTR-2026-01", a.BillingReference.ContractReference)
    let changed = { a.BillingReference with BillingClass = Some "training" }
    let amended = Ledger.execute context ledger (Ledger.Amend("A1", 1, { Classification = None; Billability = None; BillingReference = Some changed; Retime = None; Reason = "reclass" })) |> ok
    Assert.Equal(Some "training", amended.Activities["A1"].BillingReference.BillingClass)
    Assert.Equal(60, amended.Activities["A1"].Minutes)
    Assert.Equal(changed, (Entry.copyAsDraft "A2" zone (DateOnly(2026, 10, 8)) 30 amended.Activities["A1"]).BillingReference)

[<Fact>]
let ``reference diagnostics have stable codes`` () =
    Assert.Equal<string list>(
        [ "CHRONA.REFERENCE.UNKNOWN"; "CHRONA.REFERENCE.ARCHIVED"; "CHRONA.REFERENCE.DUPLICATE"; "CHRONA.REFERENCE.OWNED_ELSEWHERE" ],
        [ UnknownReference("a", "b"); ArchivedReference("a", "b"); DuplicateReference("a", "b"); ReferenceOwnedElsewhere("a", "b", "c") ] |> List.map code
    )
