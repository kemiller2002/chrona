/// Track: the timer, completing a stopped timer, and manual entry.
module Chrona.Engine.App.ProjectTrack

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

/// The person's recent combinations, as the forms offer them (33).
let internal recentRows (model: Model) =
    UpdateRecording.recentCombinations model
    |> List.mapi (fun index c ->
        let parts =
            [ referenceName model Reference.ActivityType c.ActivityTypeId
              referenceName model Reference.Project c.ProjectId
              c.Description ]
            |> List.filter (String.IsNullOrWhiteSpace >> not)

        [ t "id" (string index); t "label" (String.Join(" · ", parts)) ])

let internal timer (model: Model) =
    let active =
        match model.Timer with
        | Timer.Running timer
        | Timer.Paused timer -> Some timer
        | Timer.Idle -> None

    let classification = active |> Option.bind _.Classification
    let span = active |> Option.map (elapsedSpan model) |> Option.defaultValue TimeSpan.Zero

    let state, stateText =
        match model.Timer with
        | Timer.Running _ -> "running", "Timing now"
        | Timer.Paused _ -> "paused", "Paused"
        | Timer.Idle -> "idle", "Not running"

    [ text "timerState" state
      text "timerStateText" stateText
      flag "timerIdle" (state = "idle")
      flag "timerRunning" (state = "running")
      flag "timerPaused" (state = "paused")
      flag "timerActive" active.IsSome
      text "timerTitle" (classification |> Option.map (fun c -> referenceName model Reference.ActivityType c.ActivityTypeId) |> Option.defaultValue "Ready when you are")
      text
          "timerProjectLine"
          (classification
           |> Option.map (fun c ->
               let project = referenceName model Reference.Project c.ProjectId
               if String.IsNullOrWhiteSpace c.Description then project else $"{project} · {c.Description}")
           |> Option.defaultValue "Choose an activity type and a project, then start.")
      text "timerElapsed" (Format.elapsed span)
      text "timerElapsedSpoken" (Format.elapsedSpoken span)
      text
          "timerStarted"
          (active
           |> Option.map (fun t -> $"Started at {localClock model (List.head t.Segments).Start} · exact elapsed time")
           |> Option.defaultValue "Elapsed time comes from the start and stop instants, never a counter.")
      items "timerRecent" (recentRows model)
      items "timerTypeOptions" (options model Reference.ActivityType model.TimerDraft.ActivityTypeId)
      items "timerProjectOptions" (options model Reference.Project model.TimerDraft.ProjectId)
      flag "timerTypeUnset" (model.TimerDraft.ActivityTypeId = "")
      flag "timerProjectUnset" (model.TimerDraft.ProjectId = "")
      text "timerDescription" model.TimerDraft.Description
      flag "hasTimerProblems" (model.Problems.ContainsKey TimerForm)
      items "timerProblems" (problemItems model TimerForm)
      // The compact chip shown everywhere but Track.
      flag "showTimerChip" (active.IsSome && model.Place <> Places.Track)
      text "timerChipTitle" (classification |> Option.map (fun c -> referenceName model Reference.ActivityType c.ActivityTypeId) |> Option.defaultValue "") ]

let internal completion (model: Model) =
    let draft = model.CompletionDraft

    let facts =
        match model.Stopped with
        | Some stopped ->
            let first = stopped.Timer.Segments |> List.head
            let last = stopped.Timer.Segments |> List.last

            [ text "stoppedTotal" (Format.minutes stopped.TotalMinutes)
              text "stoppedStarted" (localClock model first.Start)
              text "stoppedStopped" (last.Finish |> Option.map (localClock model) |> Option.defaultValue "")
              text "stoppedPieces" (if stopped.Pieces.Length > 1 then $"Recorded as {stopped.Pieces.Length} activities: one per run and business day." else "Recorded as one activity.")
              flag "stoppedNeedsReview" stopped.NeedsReview.IsSome
              text "stoppedReviewText" (stopped.NeedsReview |> Option.map (describe model) |> Option.defaultValue "") ]
        | None ->
            [ text "stoppedTotal" ""; text "stoppedStarted" ""; text "stoppedStopped" ""; text "stoppedPieces" ""; flag "stoppedNeedsReview" false; text "stoppedReviewText" "" ]

    [ flag "hasStopped" model.Stopped.IsSome
      yield! facts
      flag "longTimerConfirmed" model.LongTimerConfirmed
      items "completeTypeOptions" (options model Reference.ActivityType draft.ActivityTypeId)
      items "completeProjectOptions" (options model Reference.Project draft.ProjectId)
      items "completeTagOptions" (tagOptions model draft.Tags)
      flag "completeTypeUnset" (draft.ActivityTypeId = "")
      flag "completeProjectUnset" (draft.ProjectId = "")
      flag "hasCompleteTags" (not (Reference.selectable Reference.Tag model.References).IsEmpty)
      text "completeDescription" draft.Description
      text "completePurpose" draft.BusinessPurpose
      flag "hasCompletionProblems" (model.Problems.ContainsKey CompletionForm)
      items "completionProblems" (problemItems model CompletionForm) ]

let internal manual (model: Model) =
    let draft = model.Manual

    [ flag "hasRecent" (not (UpdateRecording.recentCombinations model).IsEmpty)
      items "manualRecent" (recentRows model)
      items
          "manualDurations"
          (let noStart = Format.parseIsoDate draft.StartDate |> Option.isNone || Format.parseTime draft.StartTime |> Option.isNone

           [ for minutes in UpdateRecording.quickDurations model ->
                 [ t "id" (string minutes); t "label" (Format.minutes minutes); f "disabled" noStart ] ])
      items "manualTypeOptions" (options model Reference.ActivityType draft.Classification.ActivityTypeId)
      items "manualProjectOptions" (options model Reference.Project draft.Classification.ProjectId)
      items "manualTagOptions" (tagOptions model draft.Classification.Tags)
      flag "manualTypeUnset" (draft.Classification.ActivityTypeId = "")
      flag "manualProjectUnset" (draft.Classification.ProjectId = "")
      flag "hasManualTags" (not (Reference.selectable Reference.Tag model.References).IsEmpty)
      text "manualStartDate" draft.StartDate
      text "manualStartTime" draft.StartTime
      text "manualEndDate" draft.EndDate
      text "manualEndTime" draft.EndTime
      text "manualDescription" draft.Classification.Description
      text "manualPurpose" draft.Classification.BusinessPurpose
      text "manualReason" draft.Reason
      text "manualMaxDate" (Format.isoDate (Model.today model))
      flag "hasManualProblems" (model.Problems.ContainsKey ManualForm)
      items "manualProblems" (problemItems model ManualForm) ]
