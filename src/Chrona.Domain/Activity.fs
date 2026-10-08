/// The authoritative activity record (requirements expansion 5) and its
/// separate state dimensions (6): record lifecycle, review lifecycle and
/// publication/billing lifecycle are three types, never one status enum.
module Chrona.Domain.Activity

open System
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time

/// 6.1 Record lifecycle.
type RecordState =
    | Recorded
    | Voided of reason: string
    /// Replaced by these activities (split children or a merge result).
    | Superseded of by: string list

/// 6.2 Review lifecycle.
type ReviewState =
    | Unsubmitted
    | Submitted
    | Approved
    | Rejected of reason: string
    | Reopened

/// 6.3 Publication/billing lifecycle.
type PublicationState =
    | NotBillable
    | Unpublished
    | ReadyForPublication
    | Published
    | InvoicedExternally
    | AdjustmentRequired

/// 8 Billability.
type Billability =
    | Billable
    | NonBillable
    | PendingClassification

type EntryMethod =
    | Manual
    | Timer
    | Imported of sourceSystem: string

/// 24 An evidence or artifact reference: a pointer, never the content.
type Evidence =
    { Id: string
      Url: string
      Kind: string
      Label: string
      CapturedAt: DateTimeOffset
      Hash: string option }

/// Every problem with an evidence reference (24): it needs a label saying
/// what it is; a URL, when given, must be an absolute http or https address
/// (a pointer to the artifact, never the artifact).
let evidenceProblems (evidence: Evidence) =
    let blank = String.IsNullOrWhiteSpace

    let webAddress =
        match Uri.TryCreate(evidence.Url.Trim(), UriKind.Absolute) with
        | true, uri ->
            uri
            |> Option.ofObj
            |> Option.exists (fun u -> u.Scheme = Uri.UriSchemeHttps || u.Scheme = Uri.UriSchemeHttp)
        | _ -> false

    [ if blank evidence.Label then MissingField "evidenceLabel"
      if blank evidence.Kind then MissingField "evidenceKind"
      if not (blank evidence.Url) && not webAddress then InvalidEvidenceUrl evidence.Url ]

/// What the time was spent on (4 reference data by id).
type Classification =
    { ProjectId: string
      ClientId: string option
      EngagementId: string option
      ActivityTypeId: string
      Tags: string list
      Description: string
      BusinessPurpose: string }

/// 8 The billing references Chrona may retain beside billability. They
/// are identifiers only: rates, amounts and invoices belong to Summa.
type BillingReference =
    { RateReference: string option
      BillingClass: string option
      ContractReference: string option }

let noBillingReference =
    { RateReference = None
      BillingClass = None
      ContractReference = None }

/// Where an imported activity came from (19): the producing system, its
/// observation id, a link back where it has one, and when Chrona ingested it.
type ObservationSource =
    { SourceSystem: string
      ObservationId: string
      ExternalUrl: string option
      IngestedAt: DateTimeOffset }

/// When: an exact interval, or a duration on a business date when the
/// entry has no clock times.
type Timing =
    | Interval of start: DateTimeOffset * finish: DateTimeOffset
    | DurationOnDate of minutes: int

type Activity =
    { ActivityId: string
      OrganizationId: string
      ActorId: string
      /// Business date and local context, preserved as recorded.
      Occurrence: Occurrence
      Timing: Timing
      /// Exact duration in whole minutes; billing never changes it.
      Minutes: int
      Classification: Classification
      EntryMethod: EntryMethod
      Billability: Billability
      BillingReference: BillingReference
      Record: RecordState
      Review: ReviewState
      Publication: PublicationState
      /// Optimistic-concurrency revision, starting at 1.
      Revision: int
      CreatedAt: DateTimeOffset
      LastChangedAt: DateTimeOffset
      /// Explanation required for historical/reconstructed entries.
      Reason: string option
      WorkItemRef: string option
      ExternalRef: string option
      Evidence: Evidence list
      /// Source activity ids this one was split from or merged from.
      Lineage: string list
      /// The observation an imported activity was accepted from.
      Source: ObservationSource option }

/// Whether the activity consumes the actor's time (12): voided and
/// superseded records do not; submitted and approved records still do.
let consumesTime (activity: Activity) =
    match activity.Record with
    | Recorded -> true
    | Voided _
    | Superseded _ -> false

let interval (activity: Activity) =
    match activity.Timing with
    | Interval(start, finish) -> Some(start, finish)
    | DurationOnDate _ -> None

let recordStateName =
    function
    | Recorded -> "Recorded"
    | Voided _ -> "Voided"
    | Superseded _ -> "Superseded"

/// Every classification problem, in a stable order: what the work was, why
/// it mattered to the business, and where it belongs are all required
/// (legacy DOMAIN-REQUIREMENTS "Creating an activity requires", kept by
/// expansion 9; DF-CHRONA-2026-0002 R1). Applied on creation, on every
/// amendment and to timer results alike.
let classificationProblems (c: Classification) =
    let blank = String.IsNullOrWhiteSpace

    [ if blank c.ProjectId then MissingField "project"
      if blank c.ActivityTypeId then MissingField "activityType"
      if blank c.Description then MissingField "description"
      if blank c.BusinessPurpose then MissingField "businessPurpose" ]
