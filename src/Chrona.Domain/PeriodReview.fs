/// A person's timesheet period under review (requirements expansion 14, 15,
/// 26; WI-0036): the submissions, approvals, rejections and reopenings
/// made of it, kept as immutable records, and what they make of the period.
///
/// A period is open until its time is submitted. Submitted, it waits for
/// approval where the organization requires it, and is closed otherwise;
/// approved, it is closed. Rejected or reopened, it is open again, for
/// correction. While a period waits for approval or is closed, its time is
/// not changed, not added to and not attested: it is reopened first
/// (`CHRONA.REVIEW.SUBMITTED_PERIOD`).
///
/// Pure.
module Chrona.Domain.PeriodReview

open System
open Chrona.Domain.Diagnostics
open Chrona.Domain.Activity
open Chrona.Domain.Periods

/// What was done to a person's period.
type ReviewKind =
    | Submission
    | Approval
    | Rejection
    | Reopening

/// One step of a period's review, as recorded.
type PeriodReview =
    { Kind: ReviewKind
      /// Whose time.
      ActorId: string
      Period: Period
      /// The submission this step belongs to: the submission's own id, or
      /// the one approved, rejected or reopened.
      SubmissionId: string
      /// Who acted: the person submitting, the approver, the reviewer who
      /// rejected, or whoever reopened.
      By: string
      At: DateTimeOffset
      /// The exact activity revisions covered (14).
      Covered: Review.Covered
      /// The approval's optional note, the rejection's reason, or the
      /// reopening's reason.
      Note: string option }

/// What a person's period is now.
type PeriodState =
    /// Nothing submitted, or rejected or reopened since: time can change.
    | Open
    /// Submitted, waiting for approval.
    | AwaitingApproval of submission: PeriodReview
    /// Submitted where no approval is required, or approved.
    | Closed of last: PeriodReview
    /// Rejected: open for correction, with the reason.
    | Returned of rejection: PeriodReview

let kindName =
    function
    | Submission -> "submission"
    | Approval -> "approval"
    | Rejection -> "rejection"
    | Reopening -> "reopening"

let kindOf =
    function
    | "submission" -> Some Submission
    | "approval" -> Some Approval
    | "rejection" -> Some Rejection
    | "reopening" -> Some Reopening
    | _ -> None

/// Where a step comes in a period's review: a submission, then its
/// decision, then a reopening. Steps made at the same instant follow it.
let private rank =
    function
    | Submission -> 0
    | Approval
    | Rejection -> 1
    | Reopening -> 2

/// The reviews of one person's period, oldest first.
let ofPeriod (reviews: PeriodReview list) (actorId: string) (period: Period) =
    reviews
    |> List.filter (fun review -> review.ActorId = actorId && review.Period = period)
    |> List.sortBy (fun review -> review.At, rank review.Kind)

/// What the reviews make of a person's period.
let stateOf (approvalRequired: bool) (reviews: PeriodReview list) (actorId: string) (period: Period) =
    match ofPeriod reviews actorId period |> List.tryLast with
    | None -> Open
    | Some({ Kind = Submission } as last) when approvalRequired -> AwaitingApproval last
    | Some({ Kind = Submission } as last) -> Closed last
    | Some({ Kind = Approval } as last) -> Closed last
    | Some({ Kind = Rejection } as last) -> Returned last
    | Some { Kind = Reopening } -> Open

/// Whether time in the period is held as it is: submitted or closed.
let isHeld =
    function
    | AwaitingApproval _
    | Closed _ -> true
    | Open
    | Returned _ -> false

/// Why changing time on these (person, business date) pairs is refused: one
/// diagnostic per period held as it is.
let restrictions (config: PeriodConfig) (reviews: PeriodReview list) (touched: (string * DateOnly) list) =
    touched
    |> List.map (fun (actorId, date) -> actorId, containing config date)
    |> List.distinct
    |> List.filter (fun (actorId, period) -> isHeld (stateOf config.ApprovalRequired reviews actorId period))
    |> List.map (fun (_, period) -> SubmittedPeriodRestriction period.Start)

/// Where an activity is, and where it was, for `restrictions`: moving time
/// out of a held period changes that period too.
let touchedBy (before: Map<string, Activity>) (changed: Activity list) =
    changed
    |> List.collect (fun activity ->
        [ activity.ActorId, activity.Occurrence.LocalDate
          match before.TryFind activity.ActivityId with
          | Some earlier -> earlier.ActorId, earlier.Occurrence.LocalDate
          | None -> () ])
    |> List.distinct

/// Submissions as the review workflow knows them, by id.
let submissions (reviews: PeriodReview list) : Map<string, Review.Submission> =
    reviews
    |> List.filter (fun review -> review.Kind = Submission)
    |> List.map (fun review ->
        review.SubmissionId,
        ({ SubmissionId = review.SubmissionId
           Period = review.Period.Start, review.Period.Finish
           Covered = review.Covered
           SubmittedBy = review.ActorId
           At = review.At }: Review.Submission))
    |> Map.ofList

/// The submissions waiting for approval, oldest first: each person's period
/// whose last step is its submission.
let awaitingApproval (approvalRequired: bool) (reviews: PeriodReview list) =
    reviews
    |> List.map (fun review -> review.ActorId, review.Period)
    |> List.distinct
    |> List.choose (fun (actorId, period) ->
        match stateOf approvalRequired reviews actorId period with
        | AwaitingApproval submission -> Some submission
        | _ -> None)
    |> List.sortBy _.At

/// The time a person can submit for a period: what they recorded in it that
/// is not voided or superseded.
let submittable (actorId: string) (period: Period) (activities: Activity list) =
    activities
    |> List.filter (fun a -> a.ActorId = actorId && consumesTime a && contains period a.Occurrence.LocalDate)

/// The review workflow over the ledger, with the submissions recorded.
let workflowOf (ledger: Ledger.Ledger) (reviews: PeriodReview list) =
    { Review.start ledger with Submissions = submissions reviews }

/// Submits a person's period (14): every activity of theirs in it, at its
/// exact revision. Refused while the period is held, or when there is
/// nothing in it to submit.
let submit
    (context: Ledger.CommandContext)
    (config: PeriodConfig)
    (reviews: PeriodReview list)
    (submissionId: string)
    (period: Period)
    (ledger: Ledger.Ledger)
    =
    let actorId = context.Performer
    let mine = submittable actorId period (ledger.Activities |> Map.toList |> List.map snd)

    match stateOf config.ApprovalRequired reviews actorId period, mine with
    | state, _ when isHeld state -> Error [ SubmittedPeriodRestriction period.Start ]
    | _, [] -> Error [ NoTimeToSubmit period.Start ]
    | _, items ->
        Review.submit context submissionId (period.Start, period.Finish) (items |> List.map _.ActivityId) (workflowOf ledger reviews)
        |> Result.map (fun workflow ->
            let submission = workflow.Submissions[submissionId]

            workflow.Ledger,
            { Kind = Submission
              ActorId = actorId
              Period = period
              SubmissionId = submissionId
              By = actorId
              At = context.At
              Covered = submission.Covered
              Note = None })

let private awaiting (config: PeriodConfig) (reviews: PeriodReview list) (submissionId: string) =
    awaitingApproval config.ApprovalRequired reviews
    |> List.tryFind (fun review -> review.SubmissionId = submissionId)

/// Approves a submission waiting for approval (14): the approver, the time,
/// the period, the exact revisions and an optional note. Refused when any
/// covered activity changed since it was submitted (stale).
let approve
    (context: Ledger.CommandContext)
    (config: PeriodConfig)
    (reviews: PeriodReview list)
    (submissionId: string)
    (note: string option)
    (ledger: Ledger.Ledger)
    =
    match awaiting config reviews submissionId with
    | None when not config.ApprovalRequired -> Error [ ApprovalNotEnabled ]
    | None -> Error [ UnknownSubmission submissionId ]
    | Some submission ->
        Review.approve { ApprovalRequired = config.ApprovalRequired } context submissionId note (workflowOf ledger reviews)
        |> Result.map (fun workflow ->
            workflow.Ledger,
            { submission with
                Kind = Approval
                By = context.Performer
                At = context.At
                Note = note })

/// Rejects a submission waiting for approval (14), with the reason; the
/// period is open again for correction.
let reject
    (context: Ledger.CommandContext)
    (config: PeriodConfig)
    (reviews: PeriodReview list)
    (submissionId: string)
    (reason: string)
    (ledger: Ledger.Ledger)
    =
    match awaiting config reviews submissionId with
    | None -> Error [ UnknownSubmission submissionId ]
    | Some _ when String.IsNullOrWhiteSpace reason -> Error [ MissingField "reason" ]
    | Some submission ->
        Review.reject context submissionId (reason.Trim()) (workflowOf ledger reviews)
        |> Result.map (fun workflow ->
            workflow.Ledger,
            { submission with
                Kind = Rejection
                By = context.Performer
                At = context.At
                Note = Some(reason.Trim()) })

/// Reopens a person's held period (14: an explicit legal transition), with
/// the reason: its submitted or approved time becomes open again.
let reopen
    (context: Ledger.CommandContext)
    (config: PeriodConfig)
    (reviews: PeriodReview list)
    (actorId: string)
    (period: Period)
    (reason: string)
    (ledger: Ledger.Ledger)
    =
    let last =
        match stateOf config.ApprovalRequired reviews actorId period with
        | AwaitingApproval last
        | Closed last -> Some last
        | Open
        | Returned _ -> None

    match last with
    | None -> Error [ IllegalTransition("Open", "reopen") ]
    | Some _ when String.IsNullOrWhiteSpace reason -> Error [ MissingField "reason" ]
    | Some last ->
        let ids =
            last.Covered
            |> List.map fst
            |> List.filter (fun id ->
                match ledger.Activities.TryFind id with
                | Some a ->
                    match a.Review with
                    | Submitted
                    | Approved -> true
                    | _ -> false
                | None -> false)

        Review.reopen context (reason.Trim()) ids (Review.start ledger)
        |> Result.map (fun workflow ->
            workflow.Ledger,
            { last with
                Kind = Reopening
                By = context.Performer
                At = context.At
                Note = Some(reason.Trim()) })
