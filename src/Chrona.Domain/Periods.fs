/// Timesheet periods (requirements expansion 15): the operational periods
/// time is reviewed in, and what each period shows.
///
/// Pure. A period is a range of business dates, so its boundaries follow the
/// organization's time zone through the activities' own business dates;
/// nothing here reads a clock (today arrives as an argument).
module Chrona.Domain.Periods

open System
open Chrona.Domain.Activity

type Cadence =
    | Daily
    | Weekly
    /// Every two weeks, counted from a date that starts a period.
    | Biweekly of anchor: DateOnly
    /// The 1st to the 15th, and the 16th to the month's end.
    | SemiMonthly
    | Monthly

/// An organization's period configuration.
type PeriodConfig =
    { Cadence: Cadence
      /// The first day of a weekly period.
      WeekStart: DayOfWeek
      /// The business time zone periods are reckoned in (IANA id).
      ZoneId: string
      /// Whether time is expected to be submitted at the end of each period.
      SubmissionExpected: bool
      /// Whether submitted time must also be approved.
      ApprovalRequired: bool }

/// A legacy-like default: weekly from Monday, no submission or approval.
let defaultConfig (zoneId: string) =
    { Cadence = Weekly
      WeekStart = DayOfWeek.Monday
      ZoneId = zoneId
      SubmissionExpected = false
      ApprovalRequired = false }

/// Business dates from `Start` to `Finish`, both included.
type Period = { Start: DateOnly; Finish: DateOnly }

let contains (period: Period) (date: DateOnly) = date >= period.Start && date <= period.Finish

let private floorDiv (a: int) (b: int) = int (Math.Floor(float a / float b))

/// The period of a configuration that contains a business date.
let containing (config: PeriodConfig) (date: DateOnly) : Period =
    match config.Cadence with
    | Daily -> { Start = date; Finish = date }
    | Weekly ->
        let back = (int date.DayOfWeek - int config.WeekStart + 7) % 7
        let start = date.AddDays -back
        { Start = start; Finish = start.AddDays 6 }
    | Biweekly anchor ->
        let offset = date.DayNumber - anchor.DayNumber
        let start = anchor.AddDays(floorDiv offset 14 * 14)
        { Start = start; Finish = start.AddDays 13 }
    | SemiMonthly ->
        let first = DateOnly(date.Year, date.Month, 1)

        if date.Day <= 15 then
            { Start = first; Finish = first.AddDays 14 }
        else
            { Start = first.AddDays 15; Finish = first.AddMonths(1).AddDays -1 }
    | Monthly ->
        let first = DateOnly(date.Year, date.Month, 1)
        { Start = first; Finish = first.AddMonths(1).AddDays -1 }

let next (config: PeriodConfig) (period: Period) = containing config (period.Finish.AddDays 1)

let previous (config: PeriodConfig) (period: Period) = containing config (period.Start.AddDays -1)

type SubmissionState =
    | NothingToSubmit
    | NotSubmitted
    | PartlySubmitted
    | FullySubmitted

type ApprovalState =
    | ApprovalNotRequired
    | NothingApproved
    | PartlyApproved
    | FullyApproved

/// Work a period leaves unresolved (34).
type PeriodObligation =
    /// Time not yet classified as billable or not.
    | UnclassifiedTime of minutes: int
    /// An ended period whose time is expected to be submitted.
    | AwaitingSubmission of minutes: int
    | AwaitingApproval of minutes: int
    | RejectedTime of minutes: int

type PeriodSummary =
    { Period: Period
      ExactMinutes: int
      /// Billable time as billed, under each activity's billing policy.
      BillableMinutes: int
      NonBillableMinutes: int
      /// Time still `PendingClassification`.
      UnclassifiedMinutes: int
      Submission: SubmissionState
      Approval: ApprovalState
      Obligations: PeriodObligation list }

/// What a period shows for one person's activities (15). Only records that
/// consume time count. Billable time is projected through the billing
/// policies; an activity whose policy cannot be resolved counts at its exact
/// minutes, never more.
let summarize (config: PeriodConfig) (policies: Billing.BillingPolicy list) (today: DateOnly) (activities: Activity list) (period: Period) =
    let counted = activities |> List.filter (fun a -> consumesTime a && contains period a.Occurrence.LocalDate)
    let minutes (pick: Activity -> bool) = counted |> List.filter pick |> List.sumBy _.Minutes

    let billed (a: Activity) =
        Billing.project policies a |> Result.map _.BillableMinutes |> Result.defaultValue a.Minutes

    let submitted (a: Activity) =
        match a.Review with
        | Submitted
        | Approved -> true
        | _ -> false

    let submission =
        match counted, counted |> List.filter submitted with
        | [], _ -> NothingToSubmit
        | _, [] -> NotSubmitted
        | all, some when some.Length = all.Length -> FullySubmitted
        | _ -> PartlySubmitted

    let approval =
        let approved = counted |> List.filter (fun a -> a.Review = Approved)

        match config.ApprovalRequired, counted, approved with
        | false, _, _ -> ApprovalNotRequired
        | true, [], _ -> ApprovalNotRequired
        | true, _, [] -> NothingApproved
        | true, all, some when some.Length = all.Length -> FullyApproved
        | true, _, _ -> PartlyApproved

    let ended = period.Finish < today
    let unclassified = minutes (fun a -> a.Billability = PendingClassification)
    let unsubmitted = minutes (submitted >> not)
    let awaitingApproval = minutes (fun a -> a.Review = Submitted)

    let rejected =
        minutes (fun a ->
            match a.Review with
            | Rejected _ -> true
            | _ -> false)

    { Period = period
      ExactMinutes = minutes (fun _ -> true)
      BillableMinutes = counted |> List.filter (fun a -> a.Billability = Billable) |> List.sumBy billed
      NonBillableMinutes = minutes (fun a -> a.Billability = NonBillable)
      UnclassifiedMinutes = unclassified
      Submission = submission
      Approval = approval
      Obligations =
        [ if unclassified > 0 then UnclassifiedTime unclassified
          if config.SubmissionExpected && ended && unsubmitted > 0 then AwaitingSubmission unsubmitted
          if config.ApprovalRequired && awaitingApproval > 0 then AwaitingApproval awaitingApproval
          if rejected > 0 then RejectedTime rejected ] }
