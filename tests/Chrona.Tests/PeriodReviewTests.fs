/// Periods under review on Arca (WI-0036): requirements expansion 14, 15 and
/// 26 (the submitted-period restriction).
module Chrona.Tests.PeriodReviewTests

open System
open Xunit
open Chrona.Tests.Support
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Domain.Ledger
open Chrona.Domain.Periods
open Chrona.Domain.PeriodReview

let private zone =
    match tryZone "America/New_York" with
    | Ok z -> z
    | Error e -> failwith $"{e}"

let private at (d: int) (h: int) = DateTimeOffset(2026, 10, d, h + 4, 0, 0, TimeSpan.Zero)
let private context performer d = { Performer = performer; At = at d 18; Source = "chrona-web"; Zone = zone; References = references; CorrelationId = None }

let private config approval =
    { defaultConfig "America/New_York" with
        SubmissionExpected = true
        ApprovalRequired = approval }

let private activity id (d: int) =
    let start = at d 9

    { ActivityId = id
      OrganizationId = "ORG-1"
      ActorId = "ACTOR-1"
      Occurrence = occurrence zone start
      Timing = Interval(start, start.AddMinutes 60.0)
      Minutes = 60
      Classification = { ProjectId = "PRJ-1"; ClientId = Some "CLI-1"; EngagementId = None; ActivityTypeId = "ACT-DEV"; Tags = []; Description = "Work"; BusinessPurpose = "Delivery" }
      EntryMethod = Manual
      Billability = Billable
      BillingReference = noBillingReference
      Record = Recorded
      Review = Unsubmitted
      Publication = Unpublished
      Revision = 1
      CreatedAt = at d 8
      LastChangedAt = at d 8
      Reason = None
      WorkItemRef = None
      ExternalRef = None
      Evidence = []
      Lineage = []
      Source = None }

let private ledger (activities: Activity list) : Ledger =
    { Activities = activities |> List.map (fun a -> a.ActivityId, a) |> Map.ofList
      Audit = [] }

// Monday 5 to Sunday 11 October 2026.
let private week = containing (config true) (DateOnly(2026, 10, 7))
let private ok = function Ok value -> value | Error error -> failwith $"%A{error}"

[<Fact>]
let ``a submitted period waits for approval, is closed once approved, and is open again when returned or reopened`` () =
    let start = ledger [ activity "A1" 5; activity "A2" 7 ]
    let c = config true

    let submitted, submission = submit (context "ACTOR-1" 12) c [] "SUB-1" week start |> ok
    Assert.Equal(Submission, submission.Kind)
    Assert.Equal<Review.Covered>([ "A1", 1; "A2", 1 ], submission.Covered)
    Assert.All(submitted.Activities |> Map.toList, fun (_, a) -> Assert.Equal(Submitted, a.Review))
    Assert.Equal(AwaitingApproval submission, stateOf true [ submission ] "ACTOR-1" week)
    Assert.Equal<PeriodReview list>([ submission ], awaitingApproval true [ submission ])

    // Submitting again while it waits is the submitted-period restriction.
    match submit (context "ACTOR-1" 12) c [ submission ] "SUB-2" week submitted with
    | Error [ SubmittedPeriodRestriction s ] -> Assert.Equal(week.Start, s)
    | other -> failwith $"%A{other}"

    let approved, approval = approve (context "MANAGER-1" 13) c [ submission ] "SUB-1" (Some "Thanks") submitted |> ok
    Assert.Equal("MANAGER-1", approval.By)
    Assert.Equal("ACTOR-1", approval.ActorId)
    Assert.Equal(Some "Thanks", approval.Note)
    Assert.All(approved.Activities |> Map.toList, fun (_, a) -> Assert.Equal(Approved, a.Review))
    Assert.Equal(Closed approval, stateOf true [ submission; approval ] "ACTOR-1" week)
    Assert.Empty(awaitingApproval true [ submission; approval ])

    let reopened, reopening = reopen (context "MANAGER-1" 14) c [ submission; approval ] "ACTOR-1" week "A late correction" approved |> ok
    Assert.Equal(Reopening, reopening.Kind)
    Assert.All(reopened.Activities |> Map.toList, fun (_, a) -> Assert.Equal(Reopened, a.Review))
    Assert.Equal(Open, stateOf true [ submission; approval; reopening ] "ACTOR-1" week)

    // Returned for correction: open, with the reason.
    let _, rejection = reject (context "MANAGER-1" 13) c [ submission ] "SUB-1" "Missing a day" submitted |> ok
    Assert.Equal(Returned rejection, stateOf true [ submission; rejection ] "ACTOR-1" week)
    Assert.Equal(Some "Missing a day", rejection.Note)

[<Fact>]
let ``without approval a submitted period is closed at once, and approving is refused`` () =
    let c = config false
    let submitted, submission = submit (context "ACTOR-1" 12) c [] "SUB-1" week (ledger [ activity "A1" 5 ]) |> ok
    Assert.Equal(Closed submission, stateOf false [ submission ] "ACTOR-1" week)

    match approve (context "MANAGER-1" 13) c [ submission ] "SUB-1" None submitted with
    | Error [ ApprovalNotEnabled ] -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``time in a held period is restricted with a stable code, wherever it moves`` () =
    let c = config true
    let _, submission = submit (context "ACTOR-1" 12) c [] "SUB-1" week (ledger [ activity "A1" 5 ]) |> ok
    let reviews = [ submission ]

    match restrictions c reviews [ "ACTOR-1", DateOnly(2026, 10, 9) ] with
    | [ problem ] -> Assert.Equal("CHRONA.REVIEW.SUBMITTED_PERIOD", code problem)
    | other -> failwith $"%A{other}"

    // Another person's time, and the next week, are not held.
    Assert.Empty(restrictions c reviews [ "ACTOR-2", DateOnly(2026, 10, 9); "ACTOR-1", DateOnly(2026, 10, 12) ])

    // Moving time out of a held week touches that week too.
    let before = Map.ofList [ "A1", activity "A1" 5 ]
    let moved = { activity "A1" 12 with Revision = 2 }
    Assert.NotEmpty(restrictions c reviews (touchedBy before [ moved ]))

[<Fact>]
let ``nothing to submit, a missing reason and an unknown submission are refused with stable codes`` () =
    let c = config true

    match submit (context "ACTOR-1" 12) c [] "SUB-1" week (ledger []) with
    | Error [ problem ] -> Assert.Equal("CHRONA.REVIEW.NOTHING_TO_SUBMIT", code problem)
    | other -> failwith $"%A{other}"

    let submitted, submission = submit (context "ACTOR-1" 12) c [] "SUB-1" week (ledger [ activity "A1" 5 ]) |> ok

    match reject (context "MANAGER-1" 13) c [ submission ] "SUB-1" " " submitted with
    | Error [ MissingField "reason" ] -> ()
    | other -> failwith $"%A{other}"

    match approve (context "MANAGER-1" 13) c [ submission ] "SUB-9" None submitted with
    | Error [ problem ] -> Assert.Equal("CHRONA.REVIEW.UNKNOWN_SUBMISSION", code problem)
    | other -> failwith $"%A{other}"

    match reopen (context "ACTOR-1" 13) c [ submission ] "ACTOR-1" week "" submitted with
    | Error [ MissingField "reason" ] -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a submission whose time changed since is stale and cannot be approved`` () =
    let c = config true
    let submitted, submission = submit (context "ACTOR-1" 12) c [] "SUB-1" week (ledger [ activity "A1" 5 ]) |> ok
    let changed = ledger [ { submitted.Activities["A1"] with Revision = 2; Review = Reopened } ]

    match approve (context "MANAGER-1" 13) c [ submission ] "SUB-1" None changed with
    | Error [ StaleApproval "A1" ] -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``review steps and the period configuration are stored as records and read back exactly`` () =
    let c = config true
    let _, submission = submit (context "ACTOR-1" 12) c [] "SUB-1" week (ledger [ activity "A1" 5 ]) |> ok
    let rejection = { submission with Kind = Rejection; By = "MANAGER-1"; At = at 13 10; Note = Some "Missing a day" }

    for review in [ submission; rejection ] do
        let text = ReviewRecord.encode review |> ok
        let record = Arca.Record.decode Arca.Record.DefaultMaxBytes text |> ok
        Assert.Equal(review, ReviewRecord.ofBody record.Body |> ok)
        Assert.Equal("records/chrona.review/ACTOR-1/2026/10/" + ReviewRecord.idOf review + ".json", ReviewRecord.path review |> ok |> Arca.RelativePath.render)

    // A rejection must say why.
    let text = ReviewRecord.encode { rejection with Note = None } |> ok
    let record = Arca.Record.decode Arca.Record.DefaultMaxBytes text |> ok
    Assert.True(ReviewRecord.ofBody record.Body |> Result.isError)

    for cadence in [ Daily; Weekly; Biweekly(DateOnly(2026, 9, 21)); SemiMonthly; Monthly ] do
        let stored: PeriodConfigRecord.StoredPeriods =
            { Config = { c with Cadence = cadence; WeekStart = DayOfWeek.Sunday }
              Revision = 3
              ChangedBy = "MANAGER-1"
              ChangedAt = at 13 9 }

        let record = Arca.Record.decode Arca.Record.DefaultMaxBytes (PeriodConfigRecord.encode stored |> ok) |> ok
        Assert.Equal(stored, PeriodConfigRecord.ofBody "America/New_York" record.Body |> ok)

    Assert.Equal("records/chrona.configuration/periods.json", PeriodConfigRecord.path () |> ok |> Arca.RelativePath.render)

[<Fact>]
let ``a review step fits what is stored when only the review state differs and the step is legal from it`` () =
    let stored = Stored.load []
    let a = activity "A1" 5
    let snapshot = { stored with Activities = { stored.Activities with Activities = Map.ofList [ "A1", { Activity = a; Path = ActivityRecord.path a |> ok; Revision = Arca.Revision "r1"; ContentHash = "" } ] } }

    match Reconcile.decide snapshot { Stored.nothing with Activities = [ { a with Review = Submitted } ] } with
    | Ok changed -> Assert.Equal(1, changed.Activities.Length)
    | Error divergences -> failwith $"%A{divergences}"

    // Approving time that is not submitted (it was reopened meanwhile) diverges.
    match Reconcile.decide snapshot { Stored.nothing with Activities = [ { a with Review = Approved } ] } with
    | Error [ Reconcile.ActivityChanged _ ] -> ()
    | other -> failwith $"%A{other}"
