/// Chrona's side of the Chrona-to-Summa billing contract (requirements
/// expansion 17 and 45; WI-0037).
///
/// The contract is Summa's: Summa is the application that accepts the data
/// (DF-SUMMA-2026-0001), and `EchelonFoundry.Summa.Contracts` defines its
/// types, canonical JSON and rules. Chrona decides authoritative time and
/// what is billable; Summa owns the financial consequence. This module maps
/// one way and the other, and nothing else:
///
/// - Outbound: time staged for publication (ReadyForPublication) becomes
///   `BillableTimePublished` when Chrona publishes it (`Review.publish`),
///   carrying the exact activity revision, the billing policy, the covering
///   approval and the provenance; a published activity that was corrected so
///   that it is no longer billable as published becomes
///   `PublicationWithdrawn`.
/// - Inbound: `InvoicedExternally` is Chrona's `InvoiceReport` and
///   `AdjustmentNeeded` its `AdjustmentReport`, each applied only when it
///   names the publication and revision Chrona recorded.
///
/// Pure: no transport, clock or I/O. The wire text is the contract's own
/// codec (`Codec.tryEncodePublication`, `Codec.decodeFeedback`).
module Chrona.Integration.SummaBilling

open System
open Summa.Contracts
open Summa.Contracts.ChronaBilling.V1
open Chrona.Domain
open Chrona.Domain.Diagnostics

module A = Chrona.Domain.Activity

/// The publication id of an activity revision: the contract's idempotency
/// key. The same revision always has the same id, so publishing it again is
/// a retry; a corrected revision is a new publication that supersedes it.
let publicationId (organizationId: string) (activityId: string) (revision: int) =
    $"pub-{organizationId}-{activityId}-r{revision}"

let private source (activity: A.Activity) : SourceReference =
    { OrganizationId = activity.OrganizationId
      ActivityId = activity.ActivityId
      Revision = activity.Revision }

let private service (activity: A.Activity) : ServicePeriod =
    { BusinessDate = activity.Occurrence.LocalDate
      Zone = activity.Occurrence.Zone
      Interval = A.interval activity }

let private classification (c: A.Classification) : Classification =
    { ProjectId = c.ProjectId
      ClientId = c.ClientId
      EngagementId = c.EngagementId
      ActivityTypeId = c.ActivityTypeId
      Description = c.Description
      BusinessPurpose = c.BusinessPurpose
      Tags = c.Tags }

let private billingReference (b: A.BillingReference) : BillingReference =
    { RateReference = b.RateReference
      BillingClass = b.BillingClass
      ContractReference = b.ContractReference }

/// Where the time came from, as Chrona recorded it: always known in Chrona.
/// Chrona keeps no execution id for an entry, so none is sent.
let private origin (activity: A.Activity) : Origin =
    let method =
        match activity.EntryMethod with
        | A.Manual -> Manual
        | A.Timer -> Timer
        | A.Imported system -> Imported system

    let observation =
        activity.Source
        |> Option.map (fun found ->
            { SourceSystem = found.SourceSystem
              ObservationId = found.ObservationId
              ExternalUrl = found.ExternalUrl })

    Known(method, observation, None)

/// The approval that covers this exact revision, or none required.
let private approval (config: Review.ReviewConfig) (workflow: Review.Workflow) (activity: A.Activity) =
    if not config.ApprovalRequired then
        Ok ApprovalNotRequired
    else
        workflow.Approvals
        |> List.filter (fun found -> List.contains (activity.ActivityId, activity.Revision) found.Covered)
        |> List.sortByDescending _.At
        |> List.tryHead
        |> function
            | Some found -> Ok(ApprovedBy(found.Approver, found.At))
            | None -> Error [ NotApproved activity.ActivityId ]

/// The message for a publication Chrona recorded (`Review.publish`), at the
/// revision it names. `previous` is the activity's publication before this
/// one, which this one supersedes.
let billableTime
    (config: Review.ReviewConfig)
    (workflow: Review.Workflow)
    (previous: Review.PublicationRecord option)
    (record: Review.PublicationRecord)
    : Result<Publication, Diagnostic list> =
    match workflow.Ledger.Activities.TryFind record.ActivityId with
    | None -> Error [ UnknownActivity record.ActivityId ]
    | Some activity when activity.Revision <> record.Revision ->
        Error [ PublicationStateConflict $"{record.ActivityId} is at revision {activity.Revision}, not the published {record.Revision}" ]
    | Some activity ->
        approval config workflow activity
        |> Result.map (fun approval ->
            BillableTimePublished
                { PublicationId = record.PublicationId
                  Source = source activity
                  PerformerId = activity.ActorId
                  Service = service activity
                  Classification = classification activity.Classification
                  ExactMinutes = activity.Minutes
                  BillableMinutes = record.BillableMinutes
                  Policy = { PolicyId = record.PolicyId; Version = record.PolicyVersion }
                  BillingReference = billingReference activity.BillingReference
                  Approval = approval
                  Origin = origin activity
                  Lineage = activity.Lineage
                  WorkItemReference = activity.WorkItemRef
                  Supersedes = previous |> Option.map _.PublicationId |> Option.filter ((<>) record.PublicationId)
                  PublishedAt = record.At })

/// Publishes time staged for publication (ReadyForPublication), as Chrona's
/// rules allow (`Review.publish`), and gives the message to send.
let publish
    (config: Review.ReviewConfig)
    (policies: Billing.BillingPolicy list)
    (context: Ledger.CommandContext)
    (activityId: string)
    (workflow: Review.Workflow)
    : Result<Review.Workflow * Publication, Diagnostic list> =
    match workflow.Ledger.Activities.TryFind activityId with
    | None -> Error [ UnknownActivity activityId ]
    | Some activity when activity.Publication <> A.ReadyForPublication ->
        Error [ PublicationStateConflict $"{activityId} is {activity.Publication}, not ready for publication" ]
    | Some activity ->
        let previous = workflow.Publications.TryFind activityId
        let id = publicationId activity.OrganizationId activityId activity.Revision

        Review.publish config policies context id activityId workflow
        |> Result.bind (fun (published, record) ->
            billableTime config published previous record |> Result.map (fun message -> published, message))

/// The withdrawal of a publication whose activity was corrected so that it
/// is no longer billable as published (AdjustmentRequired and now voided,
/// split or merged away, or not billable), or None when it still is.
let withdrawal (workflow: Review.Workflow) (at: DateTimeOffset) (activityId: string) : Publication option =
    match workflow.Ledger.Activities.TryFind activityId, workflow.Publications.TryFind activityId with
    | Some activity, Some record when activity.Publication = A.AdjustmentRequired ->
        let reason =
            match activity.Record, activity.Billability with
            | A.Voided _, _ -> Some Voided
            | A.Superseded by, _ -> Some(Replaced by)
            | A.Recorded, A.Billable -> None
            | A.Recorded, _ -> Some NoLongerBillable

        reason
        |> Option.map (fun reason ->
            PublicationWithdrawn
                { PublicationId = record.PublicationId
                  Source = source activity
                  Reason = reason
                  WithdrawnAt = at })
    | _ -> None

/// The wire text of a message for Summa, checked against the contract's
/// rules first.
let encode (message: Publication) : Result<string, Problem list> = Codec.tryEncodePublication message

/// Why feedback from Summa was not applied.
type Refusal =
    /// The text is not a message of the contract.
    | NotAMessage of Problem list
    /// It is about another organization.
    | OtherOrganization of organizationId: string
    /// Chrona's rules refused it.
    | Refused of Diagnostic list

/// Applies feedback from Summa (its wire text) to the organization's
/// workflow: an invoice report or an adjustment request, each only for the
/// publication and revision Chrona recorded.
let receive (context: Ledger.CommandContext) (organizationId: string) (text: string) (workflow: Review.Workflow) : Result<Review.Workflow, Refusal> =
    match Codec.decodeFeedback text with
    | Error problems -> Error(NotAMessage problems)
    | Ok(InvoicedExternally invoiced) when invoiced.OrganizationId <> organizationId -> Error(OtherOrganization invoiced.OrganizationId)
    | Ok(AdjustmentNeeded adjustment) when adjustment.OrganizationId <> organizationId -> Error(OtherOrganization adjustment.OrganizationId)
    | Ok(InvoicedExternally invoiced) ->
        Review.recordInvoiced
            context
            { PublicationId = invoiced.PublicationId
              ActivityId = invoiced.ActivityId
              Revision = invoiced.Revision
              InvoiceReference = invoiced.InvoiceReference
              At = invoiced.At }
            workflow
        |> Result.mapError Refused
    | Ok(AdjustmentNeeded adjustment) ->
        Review.recordAdjustmentRequired
            context
            { PublicationId = adjustment.PublicationId
              ActivityId = adjustment.ActivityId
              Revision = adjustment.Revision
              Reason = adjustment.Reason
              At = adjustment.At }
            workflow
        |> Result.mapError Refused
