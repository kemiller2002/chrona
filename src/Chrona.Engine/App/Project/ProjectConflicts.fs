/// Changes not stored, and records edited outside Chrona (21, 34, 41).
module Chrona.Engine.App.ProjectConflicts

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Engine.View
open Chrona.Engine.App.Model
open Chrona.Engine.App.ProjectWords
open Chrona.Engine.App.ProjectShell
open Chrona.Engine.App.ProjectToday
open Chrona.Engine.App.ProjectTrack
open Chrona.Engine.App.ProjectMore

let internal activityLabel (activity: Activity) =
    let times =
        match interval activity with
        | Some(start, finish) -> $", {Format.clock start} – {Format.clock finish}"
        | None -> ""

    $"{quoted activity.Classification.Description} on {Format.longDate activity.Occurrence.LocalDate}{times}"

/// What the change was, in a few words.
let internal conflictTitle (case: ConflictCase) =
    match case.Request.Activities, case.Request.References, case.Request.Members, case.Request.RemovedMembers with
    | _ when case.Request.Periods.IsSome -> "Your change to the period settings was not saved"
    | _ when not case.Request.Reviews.IsEmpty -> "Your period review was not saved"
    | [ activity ], [], [], [] when activity.Revision = 1 -> $"Your new entry {quoted activity.Classification.Description} was not saved"
    | [ activity ], [], [], [] -> $"Your change to {quoted activity.Classification.Description} was not saved"
    | [], (_ :: _), [], [] -> "Your change to the lists was not saved"
    | [], [], _, _ -> "Your change to People was not saved"
    | activities, _, _, _ -> "Your change to " + plural activities.Length "activity" "activities" + " was not saved"

/// What diverged, for the person.
let internal divergenceText (model: Model) (divergence: Reconcile.Divergence) =
    match divergence with
    | Reconcile.ActivityChanged(mine, Some stored) when mine.Classification <> stored.Classification ->
        $"{quoted stored.Classification.Description} was changed elsewhere first. Stored now: {activityLabel stored}. Yours: {activityLabel mine}."
    | Reconcile.ActivityChanged(_, Some stored) -> $"{activityLabel stored} was changed elsewhere first."
    | Reconcile.ActivityChanged(mine, None) -> $"{activityLabel mine} is no longer stored as Chrona's: it was moved, removed or changed outside Chrona."
    | Reconcile.ReferenceChanged(mine, _) -> $"{quoted mine.Name} was changed elsewhere first."
    | Reconcile.MembershipChanged(mine, _) -> $"{mine.Principal.DisplayName}'s access was changed elsewhere first."
    | Reconcile.MemberGone principalId ->
        let name = model.Roster.Members.TryFind principalId |> Option.map _.Principal.DisplayName |> Option.defaultValue principalId
        $"{name} was already removed elsewhere."
    | Reconcile.OverlapsStored(mine, stored) -> $"{activityLabel mine} overlaps {activityLabel stored}, stored since."
    | Reconcile.CandidateChanged(mine, _) -> $"The observation {quoted mine.Observation.ObservationId} from {mine.Observation.SourceSystem} was decided elsewhere first."
    | Reconcile.PeriodsChanged _ -> "The organization's period settings were changed elsewhere first. Review them under More and save again."
    | Reconcile.KeptChanging -> "The records kept changing elsewhere while it was being saved. Nothing was found wrong with it."

/// The divergence the person can redo through a form, if any.
let internal redoable (model: Model) (case: ConflictCase) =
    case.Divergences
    |> List.tryPick (function
        | Reconcile.ActivityChanged(mine, Some _) ->
            match model.Ledger.Activities.TryFind mine.ActivityId with
            | Some current when current.Record = Recorded && mine.Record = Recorded && current.Classification <> mine.Classification ->
                Some "Redo yours on the current version"
            | _ -> None
        | Reconcile.OverlapsStored(mine, _) when mine.Revision = 1 && model.Zone.IsSome -> Some "Change your entry's time"
        | _ -> None)

let internal conflictView (model: Model) =
    [ flag "hasConflicts" (not model.Store.Conflicts.IsEmpty)
      items
          "conflicts"
          [ for case in model.Store.Conflicts do
                let redo = redoable model case

                [ t "id" case.Id
                  t "title" (conflictTitle case)
                  t "detail" (case.Divergences |> List.map (divergenceText model) |> String.concat " ")
                  t "code" (case.Divergences |> List.map (Reconcile.diagnostic >> code) |> List.distinct |> commaList)
                  f "noRetry" (case.Divergences <> [ Reconcile.KeptChanging ])
                  f "noRedo" redo.IsNone
                  t "redoLabel" (defaultArg redo "") ] ]
      flag "hasConflictProblems" (model.Problems.ContainsKey ConflictForm)
      items "conflictProblems" (problemItems model ConflictForm)
      flag "hasOutsideEdits" (not model.Store.Held.IsEmpty)
      items
          "outsideEdits"
          [ for held in model.Store.Held |> List.sortBy (fun a -> a.Occurrence.LocalDate, a.Occurrence.LocalTime, a.ActivityId) do
                [ t "id" held.ActivityId
                  t "title" (activityLabel held)
                  t "detail" $"{Format.minutes held.Minutes}, {referenceName model Reference.Project held.Classification.ProjectId}. Purpose: {held.Classification.BusinessPurpose}" ] ]
      flag "hasOutsideEditProblems" (model.Problems.ContainsKey OutsideEditForm)
      items "outsideEditProblems" (problemItems model OutsideEditForm) ]
