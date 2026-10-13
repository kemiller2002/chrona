/// Obligations (34): what the person must still do, each with where it is
/// resolved.
module Chrona.Engine.App.ProjectObligations

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Engine.View
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Engine.App.ProjectWords
open Chrona.Engine.App.ProjectShell
open Chrona.Engine.App.ProjectToday
open Chrona.Engine.App.ProjectTrack
open Chrona.Engine.App.ProjectMore
open Chrona.Engine.App.ProjectConflicts
open Chrona.Engine.App.ProjectCandidates

/// The latest attestation of each day, and what changed since (16).
let internal staleAttestations (model: Model) =
    let workflow = { Review.start model.Ledger with Attestations = model.Attestations }

    model.Attestations
    |> List.groupBy _.LocalDate
    |> List.map (fun (date, all) -> date, List.last all)
    |> List.choose (fun (date, latest) ->
        match Review.attestationChanges workflow latest with
        | [] -> None
        | changed -> Some(date, changed))

let internal actionLabel =
    function
    | "goTrack" -> "Complete it"
    | "openReview" -> "Review the day"
    | "goToday" -> "See the period"
    | "approve" -> "Review it"
    | "candidates" -> "Decide them"
    | _ -> "See details"

/// Unresolved work, as one projection rather than scattered warnings.
let internal obligations (model: Model) =
    [ match model.Stopped with
      | Some stopped ->
          yield
              "stopped-timer",
              "A stopped timer is waiting to be completed",
              $"{Format.minutes stopped.TotalMinutes} is held until you add what it was for.",
              "goTrack"
      | None -> ()
      for date, changed in staleAttestations model do
          yield
              $"attestation-{Format.isoDate date}",
              $"{Format.longDate date} changed after you attested it",
              $"{activities changed.Length} changed since. Review the day again.",
              "openReview"
      let period = currentPeriod model
      let label = periodLabel period.Period

      for obligation in period.Obligations do
          match obligation with
          | Periods.UnclassifiedTime minutes ->
              yield "period-unclassified", $"{Format.minutes minutes} is not yet classified as billable or not", $"In the period {label}.", "goToday"
          | Periods.AwaitingSubmission minutes ->
              yield "period-submission", $"{Format.minutes minutes} awaits submission", $"The period {label} has ended.", "goToday"
          | Periods.AwaitingApproval minutes ->
              yield "period-approval", $"{Format.minutes minutes} awaits approval", $"In the period {label}.", "goToday"
          | Periods.RejectedTime minutes ->
              yield "period-rejected", $"{Format.minutes minutes} was rejected and needs correcting", $"In the period {label}.", "goToday"

      // Other people's submissions waiting for this person's approval (34).
      if permits model Access.ApproveTime then
          for submission in awaitingMine model do
              yield
                  $"approval-{submission.SubmissionId}",
                  $"{personName model submission.ActorId}'s time waits for your approval",
                  $"The period {periodLabel submission.Period}, submitted {localStamp model submission.At}.",
                  "approve"

      // Observations waiting for this person's decision (34; WI-0038).
      match visibleCandidates model |> List.filter decidable with
      | [] -> ()
      | waiting ->
          let sources = waiting |> List.map _.Observation.SourceSystem |> List.distinct |> String.concat ", "

          yield
              "candidates-awaiting",
              plural waiting.Length "observation awaits" "observations await" + " your decision",
              $"From {sources}. Accept each as your time, or reject it with a reason.",
              "candidates"

      match model.Inbox.Invalid with
      | [] -> ()
      | invalid ->
          yield
              "inbox-invalid",
              plural invalid.Length "file in the inbox is" "files in the inbox are" + " not an observation",
              "Its producer can read why beside it. See them under More.",
              "goMore"

      match model.Inbox.Orphaned with
      | [] -> ()
      | orphaned ->
          yield
              "inbox-orphaned",
              plural orphaned.Length "receipt names a candidate" "receipts name candidates" + " that is not stored",
              "Written outside Chrona, or the candidate was removed. Its observation was not decided here. See them under More.",
              "goMore"

      for case in model.Store.Conflicts do
          yield $"conflict-{case.Id}", conflictTitle case, "It changed elsewhere first. Keep what is stored, or redo yours on it.", "goMore"

      if model.Store.Sync.Offline && not model.Store.Pending.IsEmpty then
          yield
              "unsent",
              plural model.Store.Pending.Length "change has" "changes have" + " not reached GitHub yet",
              unsentDetail model.Store.Sync,
              "goMore"

      // A running or paused timer that overlaps recorded time (10.5).
      match model.Timer with
      | Timer.Running active
      | Timer.Paused active ->
          for problem in Timer.overlapsRecorded model.Now active (model.Ledger.Activities |> Map.toList |> List.map snd) do
              match problem with
              | ConcurrentTimer activityId ->
                  let label =
                      model.Ledger.Activities.TryFind activityId
                      |> Option.map (fun a -> quoted a.Classification.Description)
                      |> Option.defaultValue activityId

                  yield
                      $"timer-overlap-{activityId}",
                      $"Your timer overlaps {label}, recorded since it started",
                      $"Perhaps it was recorded on another device. Stop the timer and keep only the time not already recorded ({code problem}).",
                      "goTrack"
              | _ -> ()
      | Timer.Idle -> ()

      match model.Store.Held with
      | [] -> ()
      | held ->
          yield
              "outside-edits",
              plural held.Length "record was" "records were" + " changed outside Chrona",
              "Held until you review them.",
              "goMore"

      match model.Store.Problem with
      | Some(Conflict _) -> ()
      | Some(Failed detail) -> yield "store", "A change was not saved", detail, "goMore"
      | Some(OutcomeUnknown detail) -> yield "store", "Chrona cannot tell whether a change was saved", detail, "goMore"
      | _ -> () ]
    |> List.map (fun (id, title, detail, action) -> [ t "id" id; t "title" title; t "detail" detail; t "action" (actionLabel action) ])
