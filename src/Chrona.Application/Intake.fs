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
