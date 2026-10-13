/// What becomes of one file found in a producer's inbox (requirement 18,
/// expansion 19; WI-0038): read through the receiver-owned contract, checked
/// against the organization whose inbox it is in, then received as an
/// observation or refused with every reason.
///
/// Pure. The store (Store.fs) makes each outcome durable in the order
/// `Observations` requires: a new candidate first, then its receipt with the
/// inbox file removed; a payload that is not an observation gets a receipt
/// naming why, and its file stays where the producer can see it.
module Chrona.Application.Intake

open System
open Chrona.Domain
open Chrona.Integration

/// The domain's observation from version 1 of the contract.
let observationOf (o: TimeObservationV1.TimeObservation) : Observations.Observation =
    { ObservationId = o.ObservationId
      SourceSystem = o.SourceSystem
      OrganizationId = o.OrganizationId
      ProjectId = o.ProjectId
      ActorId = o.ActorId
      WorkItemId = o.WorkItemId
      ExternalUrl = o.ExternalUrl
      Timing =
        match o.Timing with
        | TimeObservationV1.Interval(start, finish) -> Observations.ObservedInterval(start, finish)
        | TimeObservationV1.Duration minutes -> Observations.ObservedDuration minutes
      Description = o.Description
      Evidence = o.Evidence |> List.map (fun evidence -> evidence.Kind, evidence.Reference)
      ObservedAt = o.ObservedAt }

/// What to do with one inbox file.
type Outcome =
    /// An observation: what receiving it decided.
    | Received of Observations.Received
    /// Not an observation for this organization: the receipt saying why.
    | Refused of Observations.Receipt

/// Reads the file `inbox/<source>/<id>.json` of `organizationId`'s folder,
/// and receives it against what the organization already holds.
let decide (now: DateTimeOffset) (organizationId: string) (inbox: Observations.Inbox) (sourceSystem: string) (observationId: string) (text: string) =
    match Inbox.read sourceSystem observationId text with
    | Inbox.Invalid reasons -> Refused(Observations.invalid now sourceSystem observationId reasons)
    | Inbox.V1 observation when observation.OrganizationId <> organizationId ->
        Refused(
            Observations.invalid now sourceSystem observationId [ $"the observation is for organization '{observation.OrganizationId}', not '{organizationId}'" ]
        )
    | Inbox.V1 observation -> Received(Observations.receive now inbox (observationOf observation))

/// What a receipt says went wrong, when it names a payload that was not an
/// observation.
let reasonsOf (receipt: Observations.Receipt) =
    match receipt.Outcome with
    | Observations.InvalidObservation reasons -> Some reasons
    | Observations.CandidateRecorded _ -> None

/// What a source's policy made of a new candidate (expansion 19, WI-0074).
type Policed =
    /// Left for a person to review: the source is not trusted, or the
    /// observation names no member to record the time for.
    | ForReview
    /// Accepted as the named person's time: the decided candidate, the
    /// activities recorded and the ledger's audit entries.
    | AutoAccepted of candidate: Observations.Candidate * activities: Activity.Activity list * entries: Ledger.AuditEntry list
    /// The policy would accept it, but the time cannot be recorded as it is
    /// (an overlap, an archived reference, a held period): a person decides.
    | Attention of Observations.Candidate

/// Applies the organization's policy for the candidate's source. Only a
/// trusted source, with the classification its policy names, and only for
/// an observation that names one of the organization's members: that
/// person's ledger (`ledger`, the months the time falls in) decides, under
/// every ordinary rule, and time in a submitted or closed period waits for
/// a person (`periods`, `reviews`).
let police
    (policy: Observations.SourcePolicy)
    (context: Observations.DecisionContext)
    (members: Set<string>)
    (periods: Periods.PeriodConfig)
    (reviews: PeriodReview.PeriodReview list)
    (activityId: string)
    (candidate: Observations.Candidate)
    (ledger: Ledger.Ledger)
    =
    let observation = candidate.Observation

    match policy, observation.ActorId with
    | { AutoAccept = true; DefaultActivityTypeId = Some activityType; DefaultBusinessPurpose = Some purpose }, Some actor when
        policy.SourceSystem = observation.SourceSystem && members.Contains actor && candidate.Disposition = Observations.Pending
        ->
        let single: Observations.Inbox =
            { Observations.empty with Candidates = Map.ofList [ candidate.CandidateId, candidate ] }

        let classification = Observations.proposal activityType purpose observation

        let accepted =
            Observations.accept context activityId candidate.CandidateId candidate.Revision classification single ledger
            |> Result.bind (fun (inbox, next) ->
                let recorded =
                    next.Activities |> Map.toList |> List.filter (fun (id, a) -> ledger.Activities.TryFind id <> Some a) |> List.map snd

                match PeriodReview.restrictions periods reviews (PeriodReview.touchedBy ledger.Activities recorded) with
                | [] -> Ok(inbox.Candidates[candidate.CandidateId], recorded, next.Audit |> List.skip ledger.Audit.Length)
                | restricted -> Error restricted)

        match accepted with
        | Ok(decided, recorded, entries) -> AutoAccepted(decided, recorded, entries)
        | Error problems -> Attention(Observations.needsAttention context candidate (List.distinct problems))
    | _ -> ForReview
