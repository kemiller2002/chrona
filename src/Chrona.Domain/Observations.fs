/// Observation processing (requirements expansion 19): a producer's
/// TimeObservation becomes a TimeCandidate that a person (or a trusted
/// source policy) decides, and every observation gets exactly one
/// ProcessingReceipt.
///
/// Pure. The rules here decide; storage (WI-0038) makes each decision
/// durable. The order is part of the contract: a candidate and its decision
/// are written first, and a receipt only after, so a receipt never claims
/// processing that is not durable. A candidate found without its receipt is
/// repaired by issuing the receipt again, never by deciding twice.
module Chrona.Domain.Observations

open System
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity

/// How an observation says when the work happened.
type ObservedTiming =
    | ObservedInterval of start: DateTimeOffset * finish: DateTimeOffset
    /// Minutes worked, on the business date the observation was made.
    | ObservedDuration of minutes: int

/// A structurally valid observation (the receiver-owned contract has already
/// been read; see WI-0037). Domain validation happens here.
type Observation =
    { ObservationId: string
      SourceSystem: string
      OrganizationId: string
      ProjectId: string
      ActorId: string option
      WorkItemId: string option
      ExternalUrl: string option
      Timing: ObservedTiming
      Description: string option
      Evidence: (string * string) list
      ObservedAt: DateTimeOffset }

/// What a source may do without review (19): by default nothing.
type SourcePolicy =
    { SourceSystem: string
      AutoAccept: bool
      /// What an auto-accepted activity is classified as, since producers do
      /// not know Chrona's activity types or business purposes.
      DefaultActivityTypeId: string option
      DefaultBusinessPurpose: string option }

let reviewEverything (sourceSystem: string) =
    { SourceSystem = sourceSystem
      AutoAccept = false
      DefaultActivityTypeId = None
      DefaultBusinessPurpose = None }

type Disposition =
    | Pending
    | Accepted of activityIds: string list
    | AcceptedWithChanges of activityIds: string list
    | Rejected of reason: string
    /// The same work as an earlier candidate, which stays the one to decide.
    | Duplicate of candidateId: string
    /// It could not be accepted as it stands; a person must decide.
    | NeedsAttention of problems: Diagnostic list

type Decision =
    { By: string
      At: DateTimeOffset
      Disposition: Disposition }

type Candidate =
    { CandidateId: string
      Observation: Observation
      ReceivedAt: DateTimeOffset
      Disposition: Disposition
      Revision: int
      /// Every decision, oldest first; nothing is overwritten (25).
      Decisions: Decision list }

type ReceiptOutcome =
    /// A candidate exists and holds the observation's decision.
    | CandidateRecorded of candidateId: string
    /// The payload could not be read as an observation at all. This is not a
    /// rejection: nothing was decided about the work.
    | InvalidObservation of reasons: string list

type Receipt =
    { SourceSystem: string
      ObservationId: string
      Outcome: ReceiptOutcome
      At: DateTimeOffset }

/// The processing state for one organization: candidates by id, receipts by
/// (source, observation id).
[<NoComparison>]
type Inbox =
    { Candidates: Map<string, Candidate>
      Receipts: Map<string * string, Receipt> }

let empty =
    { Candidates = Map.empty
      Receipts = Map.empty }

/// The candidate id an observation gets: stable, so a retried ingestion
/// finds the same candidate.
let candidateId (sourceSystem: string) (observationId: string) = $"CAND-{sourceSystem}-{observationId}"

/// Domain problems with an observation; structurally valid is not enough.
let problems (now: DateTimeOffset) (observation: Observation) =
    let blank = String.IsNullOrWhiteSpace

    [ if blank observation.ObservationId then MissingField "observationId"
      if blank observation.SourceSystem then MissingField "sourceSystem"
      if blank observation.ProjectId then MissingField "project"
      match observation.Timing with
      | ObservedInterval(start, finish) ->
          match minutesBetween start finish with
          | Error d -> d
          | Ok _ -> if finish > now then FutureTime
      | ObservedDuration minutes -> if minutes <= 0 then DurationNotPositive ]

/// Two candidates describe the same work: one source, one person, one project
/// and the same time, whatever ids the producer gave them (28).
let private sameWork (a: Observation) (b: Observation) =
    a.SourceSystem = b.SourceSystem
    && a.ActorId = b.ActorId
    && a.ProjectId = b.ProjectId
    && a.Timing = b.Timing
    && (match a.Timing with
        | ObservedDuration _ -> a.ObservedAt.UtcDateTime.Date = b.ObservedAt.UtcDateTime.Date
        | ObservedInterval _ -> true)

/// The outcome of receiving one observation: the candidate to store first,
/// then the receipt. A repeat of an observation already received changes
/// nothing and returns the receipt it already has (idempotent retry).
type Received =
    | NewCandidate of Candidate * Receipt
    | AlreadyReceived of Receipt
    /// The candidate was stored but its receipt was not (a failure between
    /// the two writes): issue the receipt now, deciding nothing again (29).
    | ReceiptRepair of Receipt

let private receiptFor (candidate: Candidate) (at: DateTimeOffset) =
    { SourceSystem = candidate.Observation.SourceSystem
      ObservationId = candidate.Observation.ObservationId
      Outcome = CandidateRecorded candidate.CandidateId
      At = at }

/// Receives an observation that is structurally valid. Domain problems make
/// a candidate that needs attention, never a lost observation; work already
/// received under another id is a duplicate of that candidate.
let receive (now: DateTimeOffset) (inbox: Inbox) (observation: Observation) : Received =
    let key = observation.SourceSystem, observation.ObservationId
    let id = candidateId observation.SourceSystem observation.ObservationId

    match inbox.Receipts.TryFind key, inbox.Candidates.TryFind id with
    | Some receipt, _ -> AlreadyReceived receipt
    | None, Some candidate -> ReceiptRepair(receiptFor candidate now)
    | None, None ->
        let earlier =
            inbox.Candidates
            |> Map.toList
            |> List.map snd
            |> List.filter (fun c ->
                sameWork c.Observation observation
                && (match c.Disposition with
                    | Duplicate _
                    | Rejected _ -> false
                    | _ -> true))
            |> List.sortBy _.ReceivedAt
            |> List.tryHead

        let disposition =
            match earlier, problems now observation with
            | Some original, _ -> Duplicate original.CandidateId
            | None, [] -> Pending
            | None, found -> NeedsAttention found

        let candidate =
            { CandidateId = id
              Observation = observation
              ReceivedAt = now
              Disposition = disposition
              Revision = 1
              Decisions = [ { By = "chrona"; At = now; Disposition = disposition } ] }

        NewCandidate(candidate, receiptFor candidate now)

/// The receipt for a payload that is not an observation at all (30). It
/// names the source and id when the payload said them.
let invalid (now: DateTimeOffset) (sourceSystem: string) (observationId: string) (reasons: string list) =
    { SourceSystem = sourceSystem
      ObservationId = observationId
      Outcome = InvalidObservation reasons
      At = now }

/// Stores what `receive` decided, in the order storage must write it:
/// candidate, then receipt.
let record (inbox: Inbox) (received: Received) =
    match received with
    | NewCandidate(candidate, receipt) ->
        { Candidates = inbox.Candidates.Add(candidate.CandidateId, candidate)
          Receipts = inbox.Receipts.Add((receipt.SourceSystem, receipt.ObservationId), receipt) }
    | ReceiptRepair receipt -> { inbox with Receipts = inbox.Receipts.Add((receipt.SourceSystem, receipt.ObservationId), receipt) }
    | AlreadyReceived _ -> inbox

let recordInvalid (inbox: Inbox) (receipt: Receipt) =
    { inbox with Receipts = inbox.Receipts.Add((receipt.SourceSystem, receipt.ObservationId), receipt) }

/// Who decides, when, and against which ledger.
[<NoComparison>]
type DecisionContext =
    { Ledger: Ledger.CommandContext
      Zone: Zone }

let private find (inbox: Inbox) (id: string) (expected: int) =
    match inbox.Candidates.TryFind id with
    | None -> Error [ UnknownActivity id ]
    | Some c when c.Revision <> expected -> Error [ RevisionConflict(expected, c.Revision) ]
    | Some c ->
        match c.Disposition with
        | Pending
        | NeedsAttention _ -> Ok c
        | Accepted _ -> Error [ IllegalTransition("Accepted", "decide") ]
        | AcceptedWithChanges _ -> Error [ IllegalTransition("AcceptedWithChanges", "decide") ]
        | Rejected _ -> Error [ IllegalTransition("Rejected", "decide") ]
        | Duplicate _ -> Error [ IllegalTransition("Duplicate", "decide") ]

let private decide (context: DecisionContext) (candidate: Candidate) (disposition: Disposition) =
    { candidate with
        Disposition = disposition
        Revision = candidate.Revision + 1
        Decisions =
            candidate.Decisions
            @ [ { By = context.Ledger.Performer
                  At = context.Ledger.At
                  Disposition = disposition } ] }

/// What the observation proposes, as a classification. Producers know the
/// project and what was done; the activity type and business purpose come
/// from the reviewer or the source policy.
let proposal (activityTypeId: string) (businessPurpose: string) (observation: Observation) : Classification =
    { ProjectId = observation.ProjectId
      ClientId = None
      EngagementId = None
      ActivityTypeId = activityTypeId
      Tags = []
      Description = defaultArg observation.Description ""
      BusinessPurpose = businessPurpose }

/// The activity an accepted observation becomes. Every ordinary rule still
/// applies when it is recorded (overlap, classification, reference data).
let private activityFrom (context: DecisionContext) (candidate: Candidate) (classification: Classification) (activityId: string) : Result<Activity, Diagnostic list> =
    let o = candidate.Observation

    let timing =
        match o.Timing with
        | ObservedInterval(start, finish) ->
            minutesBetween start finish |> Result.map (fun minutes -> Interval(start, finish), minutes, occurrence context.Zone start)
        | ObservedDuration minutes ->
            let date = (occurrence context.Zone o.ObservedAt).LocalDate
            let dayStart, _ = dayBounds context.Zone date
            Ok(DurationOnDate minutes, minutes, { occurrence context.Zone dayStart with LocalDate = date })

    timing
    |> Result.mapError List.singleton
    |> Result.map (fun (timing, minutes, occurs) ->
        { ActivityId = activityId
          OrganizationId = o.OrganizationId
          ActorId = defaultArg o.ActorId context.Ledger.Performer
          Occurrence = occurs
          Timing = timing
          Minutes = minutes
          Classification = classification
          EntryMethod = Imported o.SourceSystem
          // Whether imported time is billable is a person's decision.
          Billability = PendingClassification
          BillingReference = noBillingReference
          Record = Recorded
          Review = Unsubmitted
          Publication = Unpublished
          Revision = 1
          CreatedAt = context.Ledger.At
          LastChangedAt = context.Ledger.At
          Reason = None
          WorkItemRef = o.WorkItemId
          ExternalRef = o.ExternalUrl
          Evidence =
            o.Evidence
            |> List.mapi (fun index (kind, reference) ->
                { Id = $"{activityId}-EV{index + 1}"
                  Url = (if reference.StartsWith "http" then reference else "")
                  Kind = kind
                  Label = reference
                  CapturedAt = o.ObservedAt
                  Hash = None })
          Lineage = []
          Source =
            Some
                { SourceSystem = o.SourceSystem
                  ObservationId = o.ObservationId
                  ExternalUrl = o.ExternalUrl
                  IngestedAt = candidate.ReceivedAt } })

/// Accepts a candidate as one activity, classified as the reviewer says.
/// A classification that changes what the observation proposed (project or
/// description) is AcceptedWithChanges (26). The ledger records the activity
/// under its ordinary rules; a refusal leaves the candidate as it was.
let accept (context: DecisionContext) (activityId: string) (candidateId: string) (expectedRevision: int) (classification: Classification) (inbox: Inbox) (ledger: Ledger.Ledger) =
    find inbox candidateId expectedRevision
    |> Result.bind (fun candidate ->
        activityFrom context candidate classification activityId
        |> Result.bind (fun activity ->
            Ledger.execute { context.Ledger with Source = $"observation:{candidate.Observation.SourceSystem}" } ledger (Ledger.Record activity)
            |> Result.map (fun next ->
                let proposed = proposal classification.ActivityTypeId classification.BusinessPurpose candidate.Observation

                let disposition =
                    if classification.ProjectId = proposed.ProjectId && classification.Description = proposed.Description then
                        Accepted [ activityId ]
                    else
                        AcceptedWithChanges [ activityId ]

                { inbox with Candidates = inbox.Candidates.Add(candidateId, decide context candidate disposition) }, next)))

/// Rejects a candidate; it stays, with its reason (27).
let reject (context: DecisionContext) (candidateId: string) (expectedRevision: int) (reason: string) (inbox: Inbox) =
    if String.IsNullOrWhiteSpace reason then
        Error [ MissingField "reason" ]
    else
        find inbox candidateId expectedRevision
        |> Result.map (fun candidate ->
            { inbox with Candidates = inbox.Candidates.Add(candidateId, decide context candidate (Rejected(reason.Trim()))) })

/// Receives, then applies the source policy: a trusted source's observation
/// is accepted at once with the policy's classification; if it cannot be
/// recorded it needs attention instead, and nothing is lost (19).
let ingest (context: DecisionContext) (policy: SourcePolicy) (activityId: string) (inbox: Inbox) (ledger: Ledger.Ledger) (observation: Observation) =
    let received = receive context.Ledger.At inbox observation
    let stored = record inbox received

    match received, policy with
    | NewCandidate(candidate, receipt), { AutoAccept = true; DefaultActivityTypeId = Some activityType; DefaultBusinessPurpose = Some purpose } when
        candidate.Disposition = Pending && policy.SourceSystem = observation.SourceSystem
        ->
        let classification = proposal activityType purpose observation

        match accept context activityId candidate.CandidateId candidate.Revision classification stored ledger with
        | Ok(inbox, ledger) -> inbox, ledger, receipt
        | Error found ->
            let attention = decide context candidate (NeedsAttention found)
            { stored with Candidates = stored.Candidates.Add(candidate.CandidateId, attention) }, ledger, receipt
    | NewCandidate(_, receipt), _
    | AlreadyReceived receipt, _
    | ReceiptRepair receipt, _ -> stored, ledger, receipt

/// Candidates awaiting a person, oldest first: an obligation (34).
let awaitingReview (inbox: Inbox) =
    inbox.Candidates
    |> Map.toList
    |> List.map snd
    |> List.filter (fun c ->
        match c.Disposition with
        | Pending
        | NeedsAttention _ -> true
        | _ -> false)
    |> List.sortBy _.ReceivedAt
