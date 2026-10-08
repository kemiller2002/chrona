/// Billing projection, review, attestation and publication (WI-0023):
/// requirements expansion 7, 8, 14, 16, 17 and scenario tests 16-24 and
/// 35-36 of section 43.
module Chrona.Tests.ReviewBillingTests

open System
open Xunit
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Domain.Ledger
open Chrona.Domain.Billing
open Chrona.Domain.Review

let private zone =
    match tryZone "America/New_York" with
    | Ok z -> z
    | Error e -> failwith $"{e}"

let private day = DateOnly(2026, 10, 7)
let private at (h: int) (m: int) = DateTimeOffset(2026, 10, 7, h + 4, m, 0, TimeSpan.Zero)
let private ctx performer = { Performer = performer; At = DateTimeOffset(2026, 10, 7, 22, 0, 0, TimeSpan.Zero); Source = "chrona-web"; Zone = zone; CorrelationId = None }
let private actor = ctx "ACTOR-1"
let private approver = ctx "MANAGER-1"
let private approval = { ApprovalRequired = true }

let private activity id (h, m) minutes =
    let start = at h m

    { ActivityId = id
      OrganizationId = "ORG-1"
      ActorId = "ACTOR-1"
      Occurrence = occurrence zone start
      Timing = Interval(start, start.AddMinutes(float minutes))
      Minutes = minutes
      Classification = { ProjectId = "PRJ-1"; ClientId = Some "CLI-1"; EngagementId = None; ActivityTypeId = "ACT-DEV"; Tags = []; Description = "Work"; BusinessPurpose = "Delivery" }
      EntryMethod = Manual
      Billability = Billable
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
    | Ok value -> value
    | Error problems -> failwith $"{problems}"

let private refused result =
    match result with
    | Ok _ -> failwith "expected a refusal"
    | Error problems -> problems

let private workflowWith (items: Activity list) =
    items |> List.fold (fun ledger a -> execute actor ledger (Record a) |> ok) empty |> start

let private review id (wf: Workflow) = wf.Ledger.Activities[id].Review

// ---------------------------------------------------------------------------
// Billing (7, 8; scenarios 23 and 24).
// ---------------------------------------------------------------------------

let private policies =
    [ legacyDefault "ORG-1"
      { PolicyId = "client-quarter-hour"; Version = 1; Scope = ClientScope "CLI-1"; IncrementMinutes = 15; Rounding = Nearest; EffectiveFrom = DateOnly(2026, 1, 1) }
      { PolicyId = "client-quarter-hour"; Version = 2; Scope = ClientScope "CLI-1"; IncrementMinutes = 15; Rounding = Up; EffectiveFrom = DateOnly(2026, 11, 1) }
      { PolicyId = "project-exact"; Version = 1; Scope = ProjectScope "PRJ-X"; IncrementMinutes = 1; Rounding = Exact; EffectiveFrom = DateOnly.MinValue } ]

[<Fact>]
let ``scenario 24: billing rounds a projection and never the exact time`` () =
    let legacy = legacyDefault "ORG-1"
    Assert.Equal<int list>([ 6; 6; 12; 12; 0 ], [ 1; 6; 7; 12; 0 ] |> List.map (billableMinutes legacy))
    let a = { activity "A1" (9, 0) 37 with Classification = { (activity "A1" (9, 0) 37).Classification with ClientId = None } }
    let projection = project policies a |> ok
    Assert.Equal(37, projection.ExactMinutes)
    Assert.Equal(42, projection.BillableMinutes)
    Assert.Equal(("legacy-six-minute-up", 1), (projection.PolicyId, projection.PolicyVersion))
    Assert.Equal(37, a.Minutes)

[<Fact>]
let ``the most specific policy effective on the business date applies`` () =
    let clientWork = activity "A1" (9, 0) 37
    // Client scope beats organization; version 1 is effective in October.
    let projection = project policies clientWork |> ok
    Assert.Equal(("client-quarter-hour", 1, 30), (projection.PolicyId, projection.PolicyVersion, projection.BillableMinutes))
    let november = { clientWork with Occurrence = { clientWork.Occurrence with LocalDate = DateOnly(2026, 11, 2) } }
    Assert.Equal(45, (project policies november |> ok).BillableMinutes)
    let exact = { clientWork with Classification = { clientWork.Classification with ProjectId = "PRJ-X" } }
    Assert.Equal(("project-exact", 37), ((project policies exact |> ok).PolicyId, (project policies exact |> ok).BillableMinutes))
    Assert.Equal(Error BillingPolicyNotFound, project [] clientWork |> Result.map _.PolicyId)

// ---------------------------------------------------------------------------
// Review (14; scenarios 18-22).
// ---------------------------------------------------------------------------

[<Fact>]
let ``scenarios 18 and 19: submission covers exact revisions and approval records the approver`` () =
    let wf = workflowWith [ activity "A1" (9, 0) 60; activity "A2" (11, 0) 30 ]
    let submitted = submit actor "S1" (day, day) [ "A1"; "A2" ] wf |> ok
    Assert.Equal<Covered>([ "A1", 1; "A2", 1 ], submitted.Submissions["S1"].Covered)
    Assert.Equal(Submitted, review "A1" submitted)
    let approved = approve approval approver "S1" (Some "Looks right") submitted |> ok
    Assert.Equal(Approved, review "A2" approved)
    let record = List.exactlyOne approved.Approvals
    Assert.Equal(("MANAGER-1", Some "Looks right", (day, day)), (record.Approver, record.Note, record.Period))
    Assert.Equal<string list>([ "submit"; "approve" ], approved.Ledger.Audit |> List.skip 2 |> List.map _.Command)
    // Someone else cannot submit the actor's time.
    Assert.Equal<Diagnostic list>([ ActorMismatch ], submit approver "S2" (day, day) [ "A1" ] wf |> refused)

[<Fact>]
let ``scenarios 20 and 21: rejection and reopening are explicit transitions`` () =
    let submitted = workflowWith [ activity "A1" (9, 0) 60 ] |> submit actor "S1" (day, day) [ "A1" ] |> ok
    let rejected = reject approver "S1" "Wrong project" submitted |> ok
    Assert.Equal(Rejected "Wrong project", review "A1" rejected)
    // Rejected time can be corrected and resubmitted.
    Assert.Equal(Submitted, review "A1" (submit actor "S2" (day, day) [ "A1" ] rejected |> ok))
    let approved = approve approval approver "S1" None submitted |> ok
    let reopened = reopen approver "Client dispute" [ "A1" ] approved |> ok
    Assert.Equal(Reopened, review "A1" reopened)
    Assert.Equal<Diagnostic list>([ IllegalTransition("Reopened", "reopen") ], reopen approver "again" [ "A1" ] reopened |> refused)

[<Fact>]
let ``scenario 22: an amendment after approval makes the approval stale, never silently valid`` () =
    let approved = workflowWith [ activity "A1" (9, 0) 60 ] |> submit actor "S1" (day, day) [ "A1" ] |> ok |> approve approval approver "S1" None |> ok
    Assert.Empty(staleApprovals approved)
    let ledger = execute actor approved.Ledger (Amend("A1", 1, { Classification = None; Billability = None; Retime = None; Reason = "forgot a meeting" })) |> ok
    let amended = { approved with Ledger = ledger }
    Assert.Equal(Reopened, review "A1" amended)
    Assert.Equal<(string * string list) list>([ "S1", [ "A1" ] ], staleApprovals amended)

[<Fact>]
let ``approving a changed submission is refused, and approval can be switched off`` () =
    let submitted = workflowWith [ activity "A1" (9, 0) 60 ] |> submit actor "S1" (day, day) [ "A1" ] |> ok
    let changed = { submitted with Ledger = execute actor submitted.Ledger (Void("A1", 1, "duplicate")) |> ok }
    Assert.Equal<Diagnostic list>([ StaleApproval "A1" ], approve approval approver "S1" None changed |> refused)
    Assert.Equal<Diagnostic list>([ ApprovalNotEnabled ], approve { ApprovalRequired = false } approver "S1" None submitted |> refused)

// ---------------------------------------------------------------------------
// Attestation (16; scenarios 16 and 17).
// ---------------------------------------------------------------------------

[<Fact>]
let ``scenarios 16 and 17: attestation snapshots ids and revisions, and later changes show`` () =
    let wf = workflowWith [ activity "A1" (9, 0) 60; activity "A2" (11, 0) 30 ]
    let attested, snapshot = attest actor day "Complete and accurate." wf |> ok
    Assert.Equal<Covered>([ "A1", 1; "A2", 1 ], snapshot.Covered)
    Assert.Empty(attestationChanges attested snapshot)
    let amendedLedger = execute actor attested.Ledger (Amend("A2", 1, { Classification = None; Billability = None; Retime = None; Reason = "fix" })) |> ok
    let addedLedger = execute actor amendedLedger (Record(activity "A3" (13, 0) 15)) |> ok
    Assert.Equal<string list>([ "A2"; "A3" ], attestationChanges { attested with Ledger = addedLedger } snapshot)

// ---------------------------------------------------------------------------
// Summa publication (17; scenarios 23, 35 and 36).
// ---------------------------------------------------------------------------

let private approvedWorkflow items =
    let wf = workflowWith items
    wf |> submit actor "S1" (day, day) (items |> List.map _.ActivityId) |> ok |> approve approval approver "S1" None |> ok

[<Fact>]
let ``scenario 35: approved billable time publishes with its policy identity`` () =
    let wf, record = approvedWorkflow [ activity "A1" (9, 0) 37 ] |> publish approval policies approver "P1" "A1" |> ok
    Assert.Equal(Published, wf.Ledger.Activities["A1"].Publication)
    Assert.Equal(("client-quarter-hour", 1, 30, 1), (record.PolicyId, record.PolicyVersion, record.BillableMinutes, record.Revision))
    Assert.Equal(37, wf.Ledger.Activities["A1"].Minutes)
    Assert.Equal("publish", (List.last wf.Ledger.Audit).Command)

[<Fact>]
let ``scenario 36: retrying a publication is idempotent; republishing is refused`` () =
    let wf, first = approvedWorkflow [ activity "A1" (9, 0) 37 ] |> publish approval policies approver "P1" "A1" |> ok
    let again, retry = publish approval policies approver "P1" "A1" wf |> ok
    Assert.Equal(first, retry)
    Assert.Equal(wf.Ledger.Audit.Length, again.Ledger.Audit.Length)
    Assert.Equal<Diagnostic list>([ AlreadyPublished "A1" ], publish approval policies approver "P2" "A1" wf |> refused)

[<Fact>]
let ``scenario 23: only approved, billable, recorded time passes the publication gates`` () =
    let wf =
        workflowWith
            [ activity "A1" (9, 0) 30
              { activity "A2" (10, 0) 30 with Billability = NonBillable; Publication = NotBillable }
              { activity "A3" (11, 0) 30 with Billability = PendingClassification } ]

    Assert.Equal<Diagnostic list>([ NotApproved "A1" ], candidate approval policies wf "A1" |> refused)
    Assert.Equal<Diagnostic list>([ NotBillableActivity "A2"; NotApproved "A2" ], candidate approval policies wf "A2" |> refused)
    Assert.Equal<Diagnostic list>([ NotBillableActivity "A3"; NotApproved "A3" ], candidate approval policies wf "A3" |> refused)
    // Where approval is not required, recorded billable time is publishable.
    Assert.Equal(30, (candidate { ApprovalRequired = false } policies wf "A1" |> ok).BillableMinutes)
