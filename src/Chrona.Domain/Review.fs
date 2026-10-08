/// Submission and approval (14), attestation (16) and Summa publication
/// (17), over the ledger.
///
/// Review state changes do not change an activity's content revision; they
/// record the exact (activity id, revision) pairs they cover. Any later
/// content change therefore makes an approval or attestation detectably
/// stale, and `Ledger` also reopens reviewed time it changes. Publication is
/// gated by policy and idempotent per publication id.
module Chrona.Domain.Review

open System
open Chrona.Domain.Diagnostics
open Chrona.Domain.Activity
open Chrona.Domain.Ledger

/// What Summa reports when it has invoiced published time (6.3): which
/// publication, of which activity revision, under which of its invoices.
/// Identifiers only; money stays with Summa.
type InvoiceReport =
    { PublicationId: string
      ActivityId: string
      Revision: int
      InvoiceReference: string
      At: DateTimeOffset }

type ReviewConfig =
    { /// Approval is organization-configurable (6.2, 14).
      ApprovalRequired: bool }

/// The exact activity revisions a review covers.
type Covered = (string * int) list

type Submission =
    { SubmissionId: string
      Period: DateOnly * DateOnly
      Covered: Covered
      SubmittedBy: string
      At: DateTimeOffset }

type Approval =
    { SubmissionId: string
      Approver: string
      At: DateTimeOffset
      Period: DateOnly * DateOnly
      Covered: Covered
      Note: string option }

type Attestation =
    { ActorId: string
      LocalDate: DateOnly
      /// What the actor affirmed, in their own words (R6).
      Statement: string
      Covered: Covered
      At: DateTimeOffset }

type PublicationRecord =
    { PublicationId: string
      ActivityId: string
      Revision: int
      BillableMinutes: int
      PolicyId: string
      PolicyVersion: int
      At: DateTimeOffset }

[<NoComparison>]
type Workflow =
    { Ledger: Ledger
      Submissions: Map<string, Submission>
      Approvals: Approval list
      Attestations: Attestation list
      /// The latest publication of each activity.
      Publications: Map<string, PublicationRecord>
      /// Invoices Summa reported for published time, by activity id: Summa's
      /// identifiers only, never amounts (6.3).
      Invoices: Map<string, InvoiceReport> }

let start (ledger: Ledger) =
    { Ledger = ledger
      Submissions = Map.empty
      Approvals = []
      Attestations = []
      Publications = Map.empty
      Invoices = Map.empty }

let private activities (workflow: Workflow) = workflow.Ledger.Activities |> Map.toList |> List.map snd

/// Sets fields of the named activities without changing their content
/// revision, and appends one audit entry for the transition.
let private transition (context: CommandContext) (command: string) (reason: string option) (changes: Activity list) (workflow: Workflow) =
    let revisions = changes |> List.map (fun a -> a.ActivityId, a.Revision) |> List.sortBy fst

    { workflow with
        Ledger =
            { Activities = changes |> List.fold (fun map a -> Map.add a.ActivityId a map) workflow.Ledger.Activities
              Audit =
                workflow.Ledger.Audit
                @ [ { Performer = context.Performer
                      At = context.At
                      Source = context.Source
                      Command = command
                      ActivityIds = revisions |> List.map fst
                      PriorRevisions = revisions
                      ResultingRevisions = revisions
                      Reason = reason
                      CorrelationId = context.CorrelationId } ] } }

let private lookup (workflow: Workflow) (id: string) =
    match workflow.Ledger.Activities.TryFind id with
    | Some a -> Ok a
    | None -> Error [ UnknownActivity id ]

let private all (results: Result<'a, Diagnostic list> list) =
    match results |> List.collect (function Error e -> e | Ok _ -> []) with
    | [] -> Ok(results |> List.choose (function Ok a -> Some a | Error _ -> None))
    | problems -> Error problems

/// Submits the named activities, recording their exact revisions.
let submit (context: CommandContext) (submissionId: string) (period: DateOnly * DateOnly) (ids: string list) (workflow: Workflow) =
    ids
    |> List.map (fun id ->
        lookup workflow id
        |> Result.bind (fun a ->
            match a.Record, a.Review with
            | Recorded, (Unsubmitted | Reopened | Rejected _) when a.ActorId = context.Performer -> Ok a
            | Recorded, (Unsubmitted | Reopened | Rejected _) -> Error [ ActorMismatch ]
            | Recorded, review -> Error [ IllegalTransition(string review, "submit") ]
            | record, _ -> Error [ IllegalTransition(recordStateName record, "submit") ]))
    |> all
    |> Result.map (fun items ->
        let submission =
            { SubmissionId = submissionId
              Period = period
              Covered = items |> List.map (fun a -> a.ActivityId, a.Revision) |> List.sortBy fst
              SubmittedBy = context.Performer
              At = context.At }

        { (transition context "submit" None (items |> List.map (fun a -> { a with Review = Submitted })) workflow) with
            Submissions = workflow.Submissions.Add(submissionId, submission) })

/// Covered activities whose content or review changed since `covered`.
let private changedSince (workflow: Workflow) (expectedReview: ReviewState) (covered: Covered) =
    covered
    |> List.filter (fun (id, revision) ->
        match workflow.Ledger.Activities.TryFind id with
        | Some a -> a.Revision <> revision || a.Record <> Recorded || a.Review <> expectedReview
        | None -> true)
    |> List.map fst

let approve (config: ReviewConfig) (context: CommandContext) (submissionId: string) (note: string option) (workflow: Workflow) =
    match config.ApprovalRequired, workflow.Submissions.TryFind submissionId with
    | false, _ -> Error [ ApprovalNotEnabled ]
    | _, None -> Error [ UnknownActivity submissionId ]
    | true, Some submission ->
        match changedSince workflow Submitted submission.Covered with
        | (_ :: _) as stale -> Error(stale |> List.map StaleApproval)
        | [] ->
            let items = submission.Covered |> List.map (fun (id, _) -> { workflow.Ledger.Activities[id] with Review = Approved })

            let approval =
                { SubmissionId = submissionId
                  Approver = context.Performer
                  At = context.At
                  Period = submission.Period
                  Covered = submission.Covered
                  Note = note }

            Ok { (transition context "approve" note items workflow) with Approvals = workflow.Approvals @ [ approval ] }

let reject (context: CommandContext) (submissionId: string) (reason: string) (workflow: Workflow) =
    match workflow.Submissions.TryFind submissionId with
    | None -> Error [ UnknownActivity submissionId ]
    | Some submission ->
        match changedSince workflow Submitted submission.Covered with
        | (_ :: _) as stale -> Error(stale |> List.map StaleApproval)
        | [] ->
            let items = submission.Covered |> List.map (fun (id, _) -> { workflow.Ledger.Activities[id] with Review = Rejected reason })
            Ok(transition context "reject" (Some reason) items workflow)

/// Reopening is an explicit legal transition (14).
let reopen (context: CommandContext) (reason: string) (ids: string list) (workflow: Workflow) =
    ids
    |> List.map (fun id ->
        lookup workflow id
        |> Result.bind (fun a ->
            match a.Review with
            | Submitted
            | Approved
            | Rejected _ -> Ok { a with Review = Reopened }
            | review -> Error [ IllegalTransition(string review, "reopen") ]))
    |> all
    |> Result.map (fun items -> transition context "reopen" (Some reason) items workflow)

/// Approvals no longer valid for some of what they covered (14, 16).
let staleApprovals (workflow: Workflow) =
    workflow.Approvals
    |> List.choose (fun approval ->
        match changedSince workflow Approved approval.Covered with
        | [] -> None
        | ids -> Some(approval.SubmissionId, ids))

/// Daily attestation: the actor's statement and a snapshot of their recorded
/// activities for the date, by id and revision, not just a total (16).
/// Attesting again adds a newer attestation; the earlier one stays on record.
let attest (context: CommandContext) (date: DateOnly) (statement: string) (workflow: Workflow) =
    let covered =
        activities workflow
        |> List.filter (fun a -> a.ActorId = context.Performer && a.Occurrence.LocalDate = date && consumesTime a)
        |> List.map (fun a -> a.ActivityId, a.Revision)
        |> List.sortBy fst

    if String.IsNullOrWhiteSpace statement then
        Error [ MissingField "statement" ]
    else
        let attestation =
            { ActorId = context.Performer
              LocalDate = date
              Statement = statement.Trim()
              Covered = covered
              At = context.At }

        Ok({ workflow with Attestations = workflow.Attestations @ [ attestation ] }, attestation)

/// What changed for an attested day since the attestation: changed or
/// removed activities and activities added afterwards (16, scenario 17).
let attestationChanges (workflow: Workflow) (attestation: Attestation) =
    let now =
        activities workflow
        |> List.filter (fun a -> a.ActorId = attestation.ActorId && a.Occurrence.LocalDate = attestation.LocalDate && consumesTime a)
        |> List.map (fun a -> a.ActivityId, a.Revision)
        |> Set.ofList

    let before = Set.ofList attestation.Covered
    Set.union (Set.difference before now) (Set.difference now before) |> Set.map fst |> Set.toList

/// What would be published for one activity, if every gate passes (17).
let candidate (config: ReviewConfig) (policies: Billing.BillingPolicy list) (workflow: Workflow) (id: string) =
    lookup workflow id
    |> Result.bind (fun a ->
        let problems =
            [ if a.Record <> Recorded then IllegalTransition(recordStateName a.Record, "publish")
              if a.Billability <> Billable then NotBillableActivity id
              if config.ApprovalRequired && a.Review <> Approved then NotApproved id
              if String.IsNullOrWhiteSpace a.Classification.ProjectId then MissingField "project"
              match a.Publication with
              | Published
              | InvoicedExternally -> AlreadyPublished id
              | _ -> () ]

        if not problems.IsEmpty then
            Error problems
        else
            Billing.project policies a |> Result.mapError List.singleton)

/// Publishes one activity. Retrying the same publication id for the same
/// revision is a no-op success (idempotent); publishing time that was
/// already published under another id is refused.
let publish (config: ReviewConfig) (policies: Billing.BillingPolicy list) (context: CommandContext) (publicationId: string) (id: string) (workflow: Workflow) =
    match workflow.Publications.TryFind id, workflow.Ledger.Activities.TryFind id with
    | Some existing, Some a when existing.PublicationId = publicationId && existing.Revision = a.Revision && a.Publication = Published ->
        Ok(workflow, existing)
    | _ ->
        candidate config policies workflow id
        |> Result.map (fun projection ->
            let record =
                { PublicationId = publicationId
                  ActivityId = id
                  Revision = projection.Revision
                  BillableMinutes = projection.BillableMinutes
                  PolicyId = projection.PolicyId
                  PolicyVersion = projection.PolicyVersion
                  At = context.At }

            let activity = { workflow.Ledger.Activities[id] with Publication = Published }

            { (transition context "publish" None [ activity ] workflow) with
                Publications = workflow.Publications.Add(id, record) },
            record)

/// Stages approved billable time for publication (6.3): Unpublished becomes
/// ReadyForPublication when every publication gate passes. Staged time that
/// changes falls back to Unpublished (Ledger).
let stage (config: ReviewConfig) (policies: Billing.BillingPolicy list) (context: CommandContext) (ids: string list) (workflow: Workflow) =
    ids
    |> List.map (fun id ->
        candidate config policies workflow id
        |> Result.bind (fun _ ->
            let a = workflow.Ledger.Activities[id]

            match a.Publication with
            | Unpublished
            | AdjustmentRequired -> Ok { a with Publication = ReadyForPublication }
            | other -> Error [ PublicationStateConflict $"{id} is {other}, not ready to stage" ]))
    |> all
    |> Result.map (fun staged -> transition context "stage" None staged workflow)

/// Records Summa's report that published time was invoiced (6.3): the
/// publication must be the one recorded for that activity, at that revision.
/// The same report again is a no-op; a report for another publication or
/// revision is refused, never applied.
let recordInvoiced (context: CommandContext) (report: InvoiceReport) (workflow: Workflow) =
    match workflow.Publications.TryFind report.ActivityId, workflow.Ledger.Activities.TryFind report.ActivityId with
    | _, None -> Error [ UnknownActivity report.ActivityId ]
    | Some publication, Some a when publication.PublicationId = report.PublicationId && publication.Revision = report.Revision ->
        match a.Publication with
        | InvoicedExternally when workflow.Invoices.TryFind report.ActivityId = Some report -> Ok workflow
        | Published ->
            Ok
                { (transition context "invoiced-externally" None [ { a with Publication = InvoicedExternally } ] workflow) with
                    Invoices = workflow.Invoices.Add(report.ActivityId, report) }
        | other -> Error [ PublicationStateConflict $"{report.ActivityId} is {other}, not published" ]
    | _ -> Error [ PublicationStateConflict $"{report.PublicationId} is not the publication of {report.ActivityId} at revision {report.Revision}" ]
