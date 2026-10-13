/// Observations' candidates (WI-0038): the list, and one candidate to
/// decide.
module Chrona.Engine.App.ProjectCandidates

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

let internal dispositionText (model: Model) (disposition: Observations.Disposition) =
    match disposition with
    | Observations.Pending -> "Waiting for your decision"
    | Observations.NeedsAttention problems -> "Needs attention: " + (problems |> List.map (describe model) |> String.concat " ")
    | Observations.Accepted _ -> "Accepted"
    | Observations.AcceptedWithChanges _ -> "Accepted with changes"
    | Observations.Rejected reason -> $"Rejected: {reason}"
    | Observations.Duplicate original -> $"The same work as {original}, which is the one to decide"

let internal decidable (candidate: Observations.Candidate) = CandidateRecord.isOpen candidate

/// When the observed work happened, in the organization's zone.
let internal timingText (model: Model) (observation: Observations.Observation) =
    match observation.Timing with
    | Observations.ObservedInterval(start, finish) ->
        let minutes = int (finish - start).TotalMinutes
        $"{localStamp model start} to {localClock model finish} ({Format.minutes minutes})"
    | Observations.ObservedDuration minutes -> $"{Format.minutes minutes} on {Format.longDate (observedDate model observation)}"

let internal candidateTitle (model: Model) (observation: Observations.Observation) =
    match observation.Description with
    | Some description when description.Trim() <> "" -> description
    | _ -> $"Work on {referenceName model Reference.Project observation.ProjectId}"

let internal statusLabels = [ "awaiting", "Awaiting a decision"; "decided", "Decided"; "all", "All" ]

let internal candidatesView (model: Model) =
    let query =
        match model.Place with
        | Places.Candidates query -> query
        | _ -> Places.awaitingCandidates

    let visible = visibleCandidates model

    let shown =
        visible
        |> List.filter (fun candidate ->
            match query.Status with
            | "awaiting" -> decidable candidate
            | "decided" -> not (decidable candidate)
            | _ -> true)
        |> List.filter (fun candidate -> query.Source |> Option.forall ((=) candidate.Observation.SourceSystem))
        |> List.sortBy (fun candidate -> candidate.ReceivedAt, candidate.CandidateId)

    let sources = visible |> List.map _.Observation.SourceSystem |> List.distinct |> List.sort

    [ flag "screenCandidates" (shows model (function Places.Candidates _ -> true | _ -> false))
      flag "candidatesEmpty" shown.IsEmpty
      text
          "candidatesEmptyText"
          (match query.Status with
           | "awaiting" -> "Nothing observed waits for your decision."
           | "decided" -> "No observations were decided in the months read."
           | _ -> "No observations yet.")
      items
          "candidateStatuses"
          [ for status, label in statusLabels ->
                [ t "id" status
                  t "label" label
                  t "href" (hrefOf model (Places.Candidates { query with Status = status }))
                  t "current" (if status = query.Status then "page" else "false") ] ]
      flag "hasCandidateSources" (sources.Length > 1)
      items
          "candidateSources"
          [ for source in None :: (sources |> List.map Some) ->
                [ t "id" (defaultArg source "")
                  t "label" (defaultArg source "Every source")
                  t "href" (hrefOf model (Places.Candidates { query with Source = source }))
                  t "current" (if source = query.Source then "page" else "false") ] ]
      items
          "candidateList"
          [ for candidate in shown ->
                [ t "id" candidate.CandidateId
                  t "title" (candidateTitle model candidate.Observation)
                  t "detail" $"{candidate.Observation.SourceSystem} · {timingText model candidate.Observation}"
                  t "state" (dispositionText model candidate.Disposition)
                  t "href" (hrefOf model (Places.Candidate candidate.CandidateId)) ] ] ]

let internal candidateView (model: Model) =
    let candidate =
        match model.Place with
        | Places.Candidate id -> visibleCandidates model |> List.tryFind (fun candidate -> candidate.CandidateId = id)
        | _ -> None

    let draft = model.CandidateDraft
    let observation = candidate |> Option.map _.Observation
    let field (read: Observations.Observation -> string) = observation |> Option.map read |> Option.defaultValue ""

    [ flag "screenCandidate" (shows model (function Places.Candidate _ -> true | _ -> false))
      flag "candidateFound" candidate.IsSome
      text "candidatesHref" (hrefOf model (Places.Candidates Places.awaitingCandidates))
      text "candidateTitle" (field (candidateTitle model))
      text "candidateSource" (field _.SourceSystem)
      text "candidateObservation" (field _.ObservationId)
      text "candidateState" (candidate |> Option.map (fun c -> dispositionText model c.Disposition) |> Option.defaultValue "")
      text "candidateTiming" (field (timingText model))
      text "candidateProposedProject" (field (fun o -> referenceName model Reference.Project o.ProjectId))
      text "candidateWho" (field (fun o -> o.ActorId |> Option.map (personName model) |> Option.defaultValue "No one named: yours once you accept it"))
      text "candidateWorkItem" (field (fun o -> defaultArg o.WorkItemId "None"))
      flag "candidateHasLink" (observation |> Option.exists _.ExternalUrl.IsSome)
      text "candidateLink" (field (fun o -> defaultArg o.ExternalUrl ""))
      text "candidateReceived" (candidate |> Option.map (fun c -> localStamp model c.ReceivedAt) |> Option.defaultValue "")
      flag "candidateHasEvidence" (observation |> Option.exists (fun o -> not o.Evidence.IsEmpty))
      items
          "candidateEvidence"
          [ for o in Option.toList observation do
                for index, (kind, reference) in List.indexed o.Evidence ->
                    [ t "id" (string index); t "kind" kind; t "reference" reference ] ]
      items
          "candidateDecisions"
          [ for c in Option.toList candidate do
                for index, decision in List.indexed c.Decisions ->
                    [ t "id" (string index)
                      t "who" (if decision.By = "chrona" then "Chrona" else personName model decision.By)
                      t "when" (localStamp model decision.At)
                      t "what" (dispositionText model decision.Disposition) ] ]
      flag "candidateDecidable" (candidate |> Option.exists decidable && permits model Access.RecordOwnTime)
      items "candidateTypeOptions" (options model Reference.ActivityType draft.ActivityTypeId)
      flag "candidateTypeUnset" (draft.ActivityTypeId = "")
      items "candidateProjectOptions" (options model Reference.Project draft.ProjectId)
      flag
          "candidateProjectUnset"
          (Reference.selectable Reference.Project model.References |> List.forall (fun item -> item.Id <> draft.ProjectId))
      text "candidateDescription" draft.Description
      text "candidatePurpose" draft.BusinessPurpose
      text "candidateReason" model.CandidateReason
      flag "hasCandidateProblems" (model.Problems.ContainsKey CandidateForm)
      items "candidateProblems" (problemItems model CandidateForm) ]
