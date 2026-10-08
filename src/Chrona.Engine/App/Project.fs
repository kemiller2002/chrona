/// The Chrona application's projection: authoritative state to Limen's flat
/// view state. Pure: no DOM, no clock (the model's `Now` is the last instant
/// the engine was told). Every word a person reads is decided here.
module Chrona.Engine.App.Project

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Engine.View
open Chrona.Engine.App.Model

let private text key (value: string) = key, Value(Text value)
let private flag key (value: bool) = key, Value(Flag value)
let private items key (rows: (string * Scalar) list list) = key, Items rows
let private t key (value: string) = key, Text value
let private f key (value: bool) = key, Flag value

let private plural (n: int) (one: string) (many: string) = if n = 1 then $"1 {one}" else $"{n} {many}"

// ---- names and words ----------------------------------------------------------

let private referenceName (model: Model) (kind: Reference.Kind) (id: string) =
    Reference.tryName kind id model.References |> Option.defaultValue id

let private fieldName =
    function
    | "activityType" -> "an activity type"
    | "project" -> "a project"
    | "description" -> "what you did"
    | "businessPurpose" -> "the business purpose: why this work mattered to the business"
    | "start" -> "a start date and time"
    | "end" -> "an end date and time"
    | "name" -> "a name"
    | other -> other

/// A diagnostic in words, with what to do next. The stable code stays on the
/// item for support and tests.
let describe (model: Model) (diagnostic: Diagnostic) =
    match diagnostic with
    | MissingField field -> $"Add {fieldName field}."
    | DurationNotPositive
    | EndBeforeStart -> "The end must be after the start."
    | DurationNotWholeMinutes -> "Times are recorded in whole minutes."
    | CrossesBusinessDay -> "An activity cannot cross midnight. Record each day separately."
    | FutureTime -> "That time has not happened yet. Only actual time can be recorded."
    | ReasonRequiredForHistoricalEntry -> "Say why you are entering this after the fact: it is for an earlier day."
    | AmbiguousLocalTime -> "That time happens twice on this date (the clocks go back). Choose a time outside the repeated hour."
    | InvalidLocalTime -> "That time does not exist on this date (the clocks go forward). Choose another time."
    | InvalidTimeZone _ -> "Chrona does not know your time zone yet. Try again in a moment."
    | OverlapsActivity id ->
        match model.Ledger.Activities.TryFind id with
        | Some other -> $"This overlaps \"{other.Classification.Description}\". Adjust the times so they do not overlap."
        | None -> "This overlaps another activity. Adjust the times so they do not overlap."
    | DailyCapacityExceeded(date, minutes) -> $"That would record {Format.minutes minutes} on {date}, more than the day holds."
    | TimerAlreadyActive -> "A timer is already running. Stop it before starting another."
    | NoActiveTimer -> "No timer is running."
    | TimerNotPaused -> "The timer is not paused."
    | TimerPaused -> "The timer is already paused."
    | LongRunningTimerNeedsReview minutes -> $"This timer ran {Format.minutes minutes}. Confirm all of it was worked before saving."
    | UnknownReference(kind, id) -> $"Choose {fieldName kind}: \"{id}\" is not one Chrona knows."
    | ArchivedReference(kind, id) ->
        let name =
            [ Reference.Project; Reference.ActivityType; Reference.Tag; Reference.Client; Reference.Engagement ]
            |> List.tryFind (fun k -> Reference.kindName k = kind)
            |> Option.bind (fun k -> Reference.tryName k id model.References)
            |> Option.defaultValue id

        $"\"{name}\" is archived. Choose an active one, or reactivate it under More."
    | DuplicateReference _ -> "That already exists."
    | ReferenceOwnedElsewhere(_, _, owner) -> $"This is managed in {owner}."
    | RevisionConflict _ -> "This changed since you opened it. Review the current version and try again."
    | other -> $"Chrona could not do that ({code other})."

let private problemItems (model: Model) (form: Form) =
    model.Problems.TryFind form
    |> Option.defaultValue []
    |> List.mapi (fun index d -> [ t "id" $"{index}"; t "code" (code d); t "text" (describe model d) ])

// ---- shell -------------------------------------------------------------------------

let private navigation (model: Model) =
    [ "today", "Today", "TD"; "track", "Track", "TR"; "more", "More", "MR" ]
    |> List.map (fun (id, label, mark) ->
        let current = Routes.ofScreenName id = Some model.Route.Screen
        [ t "id" id; t "label" label; t "mark" mark; t "current" (if current then "page" else "false") ])

let private storeLines (model: Model) =
    let store = model.Store

    match store.Problem, store.Kind with
    | Some(Conflict detail), _ -> "attention", "Not saved: changed elsewhere", detail
    | Some(Failed detail), _ -> "attention", "Not saved", detail
    | Some(OutcomeUnknown detail), _ -> "attention", "Save outcome unknown", detail
    | _, _ when not store.Pending.IsEmpty -> "progress", "Saving…", plural store.Pending.Length "change" "changes"
    | _, InMemory ->
        "memory",
        "Kept in this tab only",
        "Records last until this tab closes. Saving to GitHub arrives with storage."
    | _, Durable name -> "ok", "All changes saved", $"Saved to {name}."

let private elapsedSpan (model: Model) (timer: Timer.ActiveTimer) =
    timer.Segments
    |> List.sumBy (fun s -> max 0.0 ((defaultArg s.Finish model.Now) - s.Start).TotalSeconds)
    |> TimeSpan.FromSeconds
    |> fun span -> TimeSpan.FromSeconds(Math.Floor span.TotalSeconds)

let private localClock (model: Model) (instant: DateTimeOffset) =
    match model.Zone with
    | Some zone -> Format.clock (occurrence zone instant).LocalTime
    | None -> Format.clock (TimeOnly.FromDateTime instant.UtcDateTime)

// ---- options ---------------------------------------------------------------------

let private options (model: Model) (kind: Reference.Kind) (selected: string) =
    Reference.selectable kind model.References
    |> List.map (fun item -> [ t "id" item.Id; t "name" item.Name; f "selected" (item.Id = selected) ])

let private tagOptions (model: Model) (chosen: string list) =
    Reference.selectable Reference.Tag model.References
    |> List.map (fun item -> [ t "id" item.Id; t "name" item.Name; f "checked" (List.contains item.Id chosen) ])

// ---- today ----------------------------------------------------------------------

let private billing (model: Model) = Billing.legacyDefault model.Session.OrganizationId

let private interval (activity: Activity) =
    match activity.Timing, tryZone activity.Occurrence.Zone with
    | Interval(start, finish), Ok zone -> Some((occurrence zone start).LocalTime, (occurrence zone finish).LocalTime)
    | _ -> None

let private dayActivities (model: Model) (date: DateOnly) =
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

let private methodLabel =
    function
    | Manual -> "Manual entry"
    | EntryMethod.Timer -> "Timer"
    | Imported source -> $"Imported from {source}"

let private record (model: Model) (activity: Activity) =
    let times =
        match interval activity with
        | Some(start, finish) -> Format.clock start, Format.clock finish
        | None -> "—", ""

    let voided =
        match activity.Record with
        | Voided _ -> true
        | _ -> false

    [ t "id" activity.ActivityId
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
      f "voided" voided ]

let private percent (part: int) (whole: int) =
    if whole = 0 then "—" else $"{int (Math.Round(100.0 * float part / float whole))}%%"

let private today (model: Model) =
    let date = selectedDate model
    let shown = dayActivities model date
    let counted = shown |> List.filter consumesTime
    let total = counted |> List.sumBy _.Minutes
    let billed = counted |> List.sumBy (fun a -> Billing.billableMinutes (billing model) a.Minutes)
    let isToday = date = Model.today model

    [ text "dayEyebrow" (if isToday then "Today" else "Day")
      text "dayTitle" (Format.longDate date)
      text "dayIso" (Format.isoDate date)
      text "dayTotal" (Format.minutes total)
      text "dayBilled" $"{Format.minutes billed} billed"
      text "dayDecimal" (Format.decimalHours total)
      text "dayEntryCount" (plural counted.Length "entry" "entries")
      flag "dayEmpty" shown.IsEmpty
      flag "dayHasRecords" (not shown.IsEmpty)
      items "dayRecords" (shown |> List.map (record model))
      text "dayTimerTotal" (counted |> List.filter (fun a -> a.EntryMethod = EntryMethod.Timer) |> List.sumBy _.Minutes |> Format.minutes)
      text "dayManualTotal" (counted |> List.filter (fun a -> a.EntryMethod = Manual) |> List.sumBy _.Minutes |> Format.minutes)
      text "dayCorrections" (counted |> List.filter (fun a -> a.Revision > 1) |> List.length |> string)
      text "dayVoided" (shown |> List.filter (consumesTime >> not) |> List.length |> string)
      text "dayEvidence" (percent (counted |> List.filter (fun a -> not a.Evidence.IsEmpty) |> List.length) counted.Length) ]

// ---- track -------------------------------------------------------------------------

let private timer (model: Model) =
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
      items "timerTypeOptions" (options model Reference.ActivityType model.TimerDraft.ActivityTypeId)
      items "timerProjectOptions" (options model Reference.Project model.TimerDraft.ProjectId)
      flag "timerTypeUnset" (model.TimerDraft.ActivityTypeId = "")
      flag "timerProjectUnset" (model.TimerDraft.ProjectId = "")
      text "timerDescription" model.TimerDraft.Description
      flag "hasTimerProblems" (model.Problems.ContainsKey TimerForm)
      items "timerProblems" (problemItems model TimerForm)
      // The compact chip shown everywhere but Track.
      flag "showTimerChip" (active.IsSome && model.Route.Screen <> Track)
      text "timerChipTitle" (classification |> Option.map (fun c -> referenceName model Reference.ActivityType c.ActivityTypeId) |> Option.defaultValue "") ]

let private completion (model: Model) =
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

let private manual (model: Model) =
    let draft = model.Manual

    [ items "manualTypeOptions" (options model Reference.ActivityType draft.Classification.ActivityTypeId)
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

// ---- more ----------------------------------------------------------------------------

let private referenceRows (model: Model) (kind: Reference.Kind) =
    Reference.all kind model.References
    |> List.map (fun item ->
        let active = item.Status = Reference.Active

        [ t "key" $"{Reference.kindName kind}:{item.Id}"
          t "name" item.Name
          f "active" active
          t "state" (if active then "Offered for new work" else "Archived: kept on past records") ])

let private more (model: Model) =
    [ text "zoneId" (model.Zone |> Option.map _.Id |> Option.defaultValue "Not yet known")
      items "projects" (referenceRows model Reference.Project)
      items "activityTypes" (referenceRows model Reference.ActivityType)
      items "tags" (referenceRows model Reference.Tag)
      text "newProjectName" (model.NewNames.TryFind Reference.Project |> Option.defaultValue "")
      text "newActivityTypeName" (model.NewNames.TryFind Reference.ActivityType |> Option.defaultValue "")
      text "newTagName" (model.NewNames.TryFind Reference.Tag |> Option.defaultValue "")
      flag "hasReferenceProblems" (model.Problems.ContainsKey ReferenceForm)
      items "referenceProblems" (problemItems model ReferenceForm) ]

// ---- the whole view ---------------------------------------------------------------

let project (model: Model) : View =
    let tone, headline, detail = storeLines model

    let needsReferences =
        (Reference.selectable Reference.Project model.References).IsEmpty
        || (Reference.selectable Reference.ActivityType model.References).IsEmpty

    [ flag "screenToday" (model.Route.Screen = Today)
      flag "screenTrack" (model.Route.Screen = Track)
      flag "screenMore" (model.Route.Screen = More)
      items "navigation" (navigation model)
      text "announcement" model.Announcement
      text "sessionName" model.Session.DisplayName
      text "sessionInitials" (Format.initials model.Session.DisplayName)
      text "storeTone" tone
      text "storeHeadline" headline
      text "storeDetail" detail
      text "todayLong" (Format.longDate (Model.today model))
      flag "needsReferences" needsReferences
      yield! today model
      yield! timer model
      yield! completion model
      yield! manual model
      yield! more model ]
