/// Today: the day's activities, with their totals.
module Chrona.Engine.App.ProjectToday

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Engine.View
open Chrona.Engine.App.Model
open Chrona.Engine.App.ProjectWords
open Chrona.Engine.App.ProjectShell

let internal billing (model: Model) = Billing.legacyDefault model.Session.OrganizationId

let internal interval (activity: Activity) =
    match activity.Timing, tryZone activity.Occurrence.Zone with
    | Interval(start, finish), Ok zone -> Some((occurrence zone start).LocalTime, (occurrence zone finish).LocalTime)
    | _ -> None

let internal dayActivities (model: Model) (date: DateOnly) =
    model.Ledger.Activities
    |> Map.toList
    |> List.map snd
    |> List.filter (fun a ->
        a.ActorId = model.Session.ActorId
        && a.Occurrence.LocalDate = date
        && (match a.Record with
            | Superseded _ -> false
            | _ -> true))
    |> List.sortBy (fun a -> a.Occurrence.LocalTime, a.ActivityId)

let internal methodLabel =
    function
    | Manual -> "Manual entry"
    | EntryMethod.Timer -> "Timer"
    | Imported source -> $"Imported from {source}"

let internal record (model: Model) (activity: Activity) =
    let times =
        match interval activity with
        | Some(start, finish) -> Format.clock start, Format.clock finish
        | None -> "—", ""

    let voided =
        match activity.Record with
        | Voided _ -> true
        | _ -> false

    [ t "id" activity.ActivityId
      // The activity's own address, with its day so only that month is read.
      t "href" (hrefOf model (Places.Entry(activity.ActivityId, Some activity.Occurrence.LocalDate)))
      t "start" (fst times)
      t "finish" (snd times)
      t "title" activity.Classification.Description
      t "classification" $"{referenceName model Reference.ActivityType activity.Classification.ActivityTypeId} · {referenceName model Reference.Project activity.Classification.ProjectId}"
      t "purpose" activity.Classification.BusinessPurpose
      t "duration" (Format.minutes activity.Minutes)
      t "billed" $"{Format.minutes (Billing.billableMinutes (billing model) activity.Minutes)} billed"
      t "method" (methodLabel activity.EntryMethod)
      t "state" (if voided then "Removed from totals" else "Recorded")
      t "stateTone" (if voided then "unknown" else "ok")
      f "mergeSelected" (List.contains activity.ActivityId model.MergeSelection)
      f "voided" voided ]

let internal percent (part: int) (whole: int) =
    if whole = 0 then "—" else $"{int (Math.Round(100.0 * float part / float whole))}%%"

/// The project the day's ledger is filtered to, from the address.
let internal dayProject (model: Model) =
    match model.Place with
    | Places.Day(_, project) -> project
    | _ -> None

let internal today (model: Model) =
    let date = selectedDate model
    let project = dayProject model

    let shown =
        dayActivities model date
        |> List.filter (fun a -> project |> Option.forall ((=) a.Classification.ProjectId))
    let counted = shown |> List.filter consumesTime
    let total = counted |> List.sumBy _.Minutes
    let billed = counted |> List.sumBy (fun a -> Billing.billableMinutes (billing model) a.Minutes)
    let isToday = date = Model.today model

    [ text "dayEyebrow" (if isToday then "Today" else "Day")
      // The ledger's project filter (CHX-460): every project, archived ones too.
      items
          "dayProjectOptions"
          ([ t "id" ""; t "name" "All projects"; f "selected" project.IsNone ]
           :: (Reference.all Reference.Project model.References
               |> List.map (fun item -> [ t "id" item.Id; t "name" item.Name; f "selected" (Some item.Id = project) ])))
      flag "dayFiltered" project.IsSome
      // The week this day is in, with the same filter (CHX-460).
      text "weekHref" (hrefOf model (Places.Week((weekOf model date).Start, project)))
      text "dayFilterName" (project |> Option.map (referenceName model Reference.Project) |> Option.defaultValue "")
      text "dayTitle" (Format.longDate date)
      text "dayIso" (Format.isoDate date)
      text "dayTotal" (Format.minutes total)
      text "dayBilled" $"{Format.minutes billed} billed"
      text "dayDecimal" (Format.decimalHours total)
      text "dayEntryCount" (plural counted.Length "entry" "entries")
      flag "dayEmpty" (shown.IsEmpty && project.IsNone)
      flag "dayFilteredEmpty" (shown.IsEmpty && project.IsSome)
      flag "dayHasRecords" (not shown.IsEmpty)
      items "dayRecords" (shown |> List.map (record model))
      text "dayTimerTotal" (counted |> List.filter (fun a -> a.EntryMethod = EntryMethod.Timer) |> List.sumBy _.Minutes |> Format.minutes)
      text "dayManualTotal" (counted |> List.filter (fun a -> a.EntryMethod = Manual) |> List.sumBy _.Minutes |> Format.minutes)
      text "dayCorrections" (counted |> List.filter (fun a -> a.Revision > 1) |> List.length |> string)
      text "dayVoided" (shown |> List.filter (consumesTime >> not) |> List.length |> string)
      text "dayEvidence" (percent (counted |> List.filter (fun a -> not a.Evidence.IsEmpty) |> List.length) counted.Length)
      flag "canMerge" (model.MergeSelection.Length >= 2)
      text "mergeCount" (plural model.MergeSelection.Length "activity selected" "activities selected")
      items "mergeTypeOptions" (options model Reference.ActivityType model.MergeDraft.ActivityTypeId)
      items "mergeProjectOptions" (options model Reference.Project model.MergeDraft.ProjectId)
      flag "mergeTypeUnset" (model.MergeDraft.ActivityTypeId = "")
      flag "mergeProjectUnset" (model.MergeDraft.ProjectId = "")
      text "mergeDescription" model.MergeDraft.Description
      text "mergePurpose" model.MergeDraft.BusinessPurpose
      flag "hasMergeProblems" (model.Problems.ContainsKey MergeForm)
      items "mergeProblems" (problemItems model MergeForm) ]
