/// Timesheet periods (WI-0048): requirements expansion 15.
module Chrona.Tests.PeriodTests

open System
open Xunit
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Domain.Periods

let private zone =
    match tryZone "America/New_York" with
    | Ok z -> z
    | Error e -> failwith $"{e}"

let private date (y, m, d) = DateOnly(y, m, d)
let private config cadence = { defaultConfig "America/New_York" with Cadence = cadence }

[<Fact>]
let ``each cadence finds the period around a date`` () =
    // Thursday 2026-10-08.
    let thursday = date (2026, 10, 8)
    Assert.Equal({ Start = thursday; Finish = thursday }, containing (config Daily) thursday)
    Assert.Equal({ Start = date (2026, 10, 5); Finish = date (2026, 10, 11) }, containing (config Weekly) thursday)
    Assert.Equal({ Start = date (2026, 10, 4); Finish = date (2026, 10, 10) }, containing { config Weekly with WeekStart = DayOfWeek.Sunday } thursday)
    Assert.Equal({ Start = date (2026, 10, 5); Finish = date (2026, 10, 18) }, containing (config (Biweekly(date (2026, 9, 21)))) thursday)
    // Dates before the anchor still fall into whole fortnights.
    Assert.Equal({ Start = date (2026, 9, 7); Finish = date (2026, 9, 20) }, containing (config (Biweekly(date (2026, 9, 21)))) (date (2026, 9, 7)))
    Assert.Equal({ Start = date (2026, 10, 1); Finish = date (2026, 10, 15) }, containing (config SemiMonthly) thursday)
    Assert.Equal({ Start = date (2026, 2, 16); Finish = date (2026, 2, 28) }, containing (config SemiMonthly) (date (2026, 2, 20)))
    Assert.Equal({ Start = date (2026, 10, 1); Finish = date (2026, 10, 31) }, containing (config Monthly) thursday)

[<Fact>]
let ``periods tile the calendar with no gap or overlap`` () =
    for cadence in [ Daily; Weekly; Biweekly(date (2026, 1, 5)); SemiMonthly; Monthly ] do
        let c = config cadence
        let periods = List.scan (fun p _ -> next c p) (containing c (date (2026, 1, 1))) [ 1..40 ]

        for a, b in List.pairwise periods do
            Assert.Equal(a.Finish.AddDays 1, b.Start)
            Assert.Equal(a, previous c b)

        for p in periods do
            Assert.Equal(p, containing c p.Start)
            Assert.Equal(p, containing c p.Finish)

let private activity id (d: DateOnly) minutes billability review =
    let start = DateTimeOffset(d.Year, d.Month, d.Day, 9, 0, 0, TimeSpan.FromHours -4.0)

    { ActivityId = id
      OrganizationId = "ORG-1"
      ActorId = "ACTOR-1"
      Occurrence = occurrence zone start
      Timing = Interval(start, start.AddMinutes(float minutes))
      Minutes = minutes
      Classification = { ProjectId = "PRJ-1"; ClientId = None; EngagementId = None; ActivityTypeId = "ACT-DEV"; Tags = []; Description = "Work"; BusinessPurpose = "Delivery" }
      EntryMethod = Manual
      Billability = billability
      BillingReference = noBillingReference
      Record = Recorded
      Review = review
      Publication = Unpublished
      Revision = 1
      CreatedAt = start
      LastChangedAt = start
      Reason = None
      WorkItemRef = None
      ExternalRef = None
      Evidence = []
      Lineage = []
      Source = None }

let private policies = [ Chrona.Domain.Billing.legacyDefault "ORG-1" ]

[<Fact>]
let ``a period shows exact, billable, non-billable and unclassified time apart`` () =
    let week = containing (config Weekly) (date (2026, 10, 8))

    let activities =
        [ activity "A1" (date (2026, 10, 5)) 37 Billable Unsubmitted
          activity "A2" (date (2026, 10, 6)) 30 NonBillable Unsubmitted
          activity "A3" (date (2026, 10, 7)) 20 PendingClassification Unsubmitted
          { activity "A4" (date (2026, 10, 8)) 60 Billable Unsubmitted with Record = Voided "duplicate" }
          activity "A5" (date (2026, 10, 12)) 60 Billable Unsubmitted ]

    let summary = summarize (config Weekly) policies (date (2026, 10, 8)) activities week
    Assert.Equal(87, summary.ExactMinutes)
    // Billable time is billed per activity (six-minute up): 37 -> 42.
    Assert.Equal(42, summary.BillableMinutes)
    Assert.Equal(30, summary.NonBillableMinutes)
    Assert.Equal(20, summary.UnclassifiedMinutes)
    Assert.Equal(NotSubmitted, summary.Submission)
    Assert.Equal(ApprovalNotRequired, summary.Approval)
    Assert.Equal<PeriodObligation list>([ UnclassifiedTime 20 ], summary.Obligations)

[<Fact>]
let ``an ended period expecting submission and approval shows what is outstanding`` () =
    let strict = { config Weekly with SubmissionExpected = true; ApprovalRequired = true }
    let week = containing strict (date (2026, 10, 1))

    let activities =
        [ activity "A1" (date (2026, 9, 28)) 60 Billable Approved
          activity "A2" (date (2026, 9, 29)) 30 Billable Submitted
          activity "A3" (date (2026, 9, 30)) 15 Billable (Rejected "wrong project")
          activity "A4" (date (2026, 10, 1)) 45 Billable Unsubmitted ]

    let summary = summarize strict policies (date (2026, 10, 8)) activities week
    Assert.Equal(PartlySubmitted, summary.Submission)
    Assert.Equal(PartlyApproved, summary.Approval)
    Assert.Equal<PeriodObligation list>([ AwaitingSubmission 60; AwaitingApproval 30; RejectedTime 15 ], summary.Obligations)

    // A period still running is not yet awaiting submission.
    let running = summarize strict policies (date (2026, 10, 2)) activities week
    Assert.Equal<PeriodObligation list>([ AwaitingApproval 30; RejectedTime 15 ], running.Obligations)

    let all = activities |> List.map (fun a -> { a with Review = Approved })
    let done' = summarize strict policies (date (2026, 10, 8)) all week
    Assert.Equal((FullySubmitted, FullyApproved), (done'.Submission, done'.Approval))
    Assert.Empty(done'.Obligations)
    Assert.Equal(NothingToSubmit, (summarize strict policies (date (2026, 10, 8)) [] week).Submission)
