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
let private n key (value: float) = key, Number value
let private t key (value: string) = key, Text value
let private f key (value: bool) = key, Flag value

let private plural (n: int) (one: string) (many: string) = if n = 1 then $"1 {one}" else $"{n} {many}"
let private quoted (text: string) = "\"" + text + "\""
let private commaList (texts: string list) = String.Join(", ", texts)
let private activities (n: int) = plural n "activity" "activities"

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
    | "evidenceLabel" -> "a label saying what the evidence is"
    | "evidenceKind" -> "the kind of evidence"
    | "memberId" -> "the person's GitHub account number"
    | "memberName" -> "the person's name"
    | "statement" -> "an attestation statement"
    | other -> other

/// A diagnostic in words, with what to do next. The stable code stays on the
/// item for support and tests.
/// What a capability lets someone do, in words.
let capabilityText =
    function
    | "RecordOwnTime" -> "record your time"
    | "AmendOwnTime" -> "correct your time"
    | "SubmitOwnTime" -> "submit your time"
    | "AttestOwnDay" -> "attest your days"
    | "ViewOwnTime" -> "see your time"
    | "ViewOrganizationTime" -> "see the organization's time"
    | "ApproveTime" -> "approve time"
    | "RejectTime" -> "reject time"
    | "ReopenTime" -> "reopen time"
    | "ManageProjects" -> "manage projects"
    | "ManageActivityTypes" -> "manage activity types"
    | "ManageTags" -> "manage tags"
    | "ManageOrganizationSettings" -> "change the organization's settings"
    | "ExportTime" -> "export time"
    | "PublishBillableTime" -> "publish billable time"
    | other -> other

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
    | InvalidEvidenceUrl _ -> "A link must be a web address starting with https:// or http://."
    | SplitDurationMismatch(expected, actual) -> $"The parts add up to {actual} minutes; they must add up to exactly {expected}."
    | EvidenceAssignmentInvalid -> "Each piece of evidence can support one part at most."
    | IncompatibleMergeSources reason -> $"These cannot be merged: {reason}."
    | PublicationStateConflict reason -> $"These cannot be merged: {reason}."
    | IllegalTransition(from, command) -> $"A record that is {from.ToLowerInvariant()} cannot be changed that way ({command})."
    | OrganizationMismatch
    | ActorMismatch -> "These belong to different people or organizations."
    | InvalidStoredRecord(path, detail) -> $"{path} is not a valid record ({detail}). It was left as it is."
    | MisplacedRecord path -> $"{path} is not where its contents say it belongs. It was left as it is."
    | DuplicateActivityId id -> $"Activity {id} is stored twice. Neither copy is shown until one is removed."
    | ImpossibleRevision id -> $"Activity {id} has an impossible revision. It was left as it is."
    | StoredOverlap(first, second) -> $"Activities {first} and {second} overlap in what is stored."
    | InvalidLineage(first, second) -> $"Activities {first} and {second} disagree about a split or merge."
    | IncompleteRead folder -> $"Not everything in {folder} could be read."
    | ExternalEdit path -> $"{path} was changed outside Chrona. It is held until it is reviewed."
    | ExternalStateClaim(_, state) ->
        $"It claims to be {state.ToLowerInvariant()}, which only Chrona's own review and publication steps give. It stays held; repair it in the repository."
    | SemanticConflict _ -> "This was changed elsewhere first. Keep what is stored, or redo your change on it."
    | StoreKeptChanging -> "The records kept changing elsewhere while this was being saved."
    | UnauthorizedCapability capability -> $"You do not have permission to {capabilityText capability} in this organization."
    | NotAMember _ -> "That person is not a member of this organization."
    | AlreadyAMember _ -> "That person is already a member."
    | LastAdministrator _ -> "Someone else must be able to manage the organization first."
    | CapabilityNotForKind(capability, _) -> $"Only a person can {capabilityText capability}."
    | other -> $"Chrona could not do that ({code other})."

let private problemItems (model: Model) (form: Form) =
    model.Problems.TryFind form
    |> Option.defaultValue []
    |> List.mapi (fun index d -> [ t "id" $"{index}"; t "code" (code d); t "text" (describe model d) ])

// ---- shell -------------------------------------------------------------------------

/// Whether the page shows a screen of these places: the address names one,
/// and no problem with it is being shown instead.
let private shows (model: Model) (screen: Places.Place -> bool) = model.RouteProblem.IsNone && screen model.Place

/// A place's relative link ("#/track"), for an `href`.
let private hrefOf (place: Places.Place) =
    match Places.href place with
    | Ok href -> href
    // Every place has an address (PlacesTests); one without is a defect.
    | Error error -> invalidOp $"No address for {place}: %A{error}"

/// The navigation section a place belongs to. A day, an activity and a
/// review belong to Today; reports to Month.
let private sectionOf (place: Places.Place) =
    match place with
    | Places.Today
    | Places.Day _
    | Places.ThisWeek
    | Places.Week _
    | Places.ThisPeriod
    | Places.Period _
    | Places.Entry _
    | Places.ReviewToday
    | Places.Review _ -> "today"
    | Places.Track -> "track"
    | Places.ThisMonth
    | Places.Month _
    | Places.Reports _ -> "month"
    | Places.Projects
    | Places.Project _
    | Places.Settings _ -> "more"
    | Places.SignIn _ -> ""

let private navigation (model: Model) =
    let section = if model.RouteProblem.IsSome then "" else sectionOf model.Place

    [ "today", "Today", "TD", Places.Today
      "track", "Track", "TR", Places.Track
      "month", "Month", "MO", Places.ThisMonth
      "more", "More", "MR", Places.Settings None ]
    |> List.map (fun (id, label, mark, place) ->
        [ t "id" id
          t "label" label
          t "mark" mark
          t "href" (hrefOf place)
          t "current" (if id = section then "page" else "false") ])

/// Where unsent changes are kept, for the person (23).
let private unsentDetail (sync: SyncState) =
    match sync.Holder, sync.KeptInBrowser, sync.Note with
    | HeldElsewhere _, _, _ ->
        "Another tab is holding your unsent changes; the changes made here are sent from this page. Keep this page open until they are sent."
    | _, true, _ -> "They are kept in this browser and sent, in order, when GitHub can be reached."
    | _, false, Some note -> $"{note} Keep this page open until they are sent."
    | _, false, None -> "This browser cannot keep them. Keep this page open until they are sent."

/// Where this browser's unsent changes are kept, as a mode (WI-0067).
let private queueMode (sync: SyncState) =
    match sync.Holder, sync.KeptInBrowser with
    | HeldElsewhere _, _ -> "Held by another Chrona tab"
    | HeldHere, true -> "Kept in this browser by this tab"
    | Unlocked, true -> "Kept in this browser; each tab keeps its own, as this browser cannot tell tabs apart"
    | _, false -> "In this page only"

let private storeLines (model: Model) =
    let store = model.Store

    match store.Problem, store.Kind with
    | _, _ when not store.Conflicts.IsEmpty ->
        "attention",
        "Not saved: changed elsewhere",
        plural store.Conflicts.Length "change waits" "changes wait"
        + " for your decision under More. Nothing of yours was discarded."
    | Some(Conflict _), _ -> "attention", "Not saved: changed elsewhere", "Resolve it under More."
    | Some(Failed detail), _ -> "attention", "Not saved", detail
    | Some(OutcomeUnknown detail), _ -> "attention", "Save outcome unknown", detail
    | _, _ when not store.Pending.IsEmpty && store.Sync.Offline ->
        "attention",
        "Offline: " + plural store.Pending.Length "change waits" "changes wait" + " to be sent",
        unsentDetail store.Sync
    | _, _ when not store.Pending.IsEmpty && not store.Sync.KeptInBrowser ->
        "progress", "Saving…", plural store.Pending.Length "change" "changes" + ". " + unsentDetail store.Sync
    | _, _ when not store.Pending.IsEmpty -> "progress", "Saving…", plural store.Pending.Length "change" "changes"
    | _, InMemory ->
        "memory",
        "Kept in this tab only",
        "This deployment stores nothing: records last until this tab closes."
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
      // The activity's own address, with its day so only that month is read.
      t "href" (hrefOf (Places.Entry(activity.ActivityId, Some activity.Occurrence.LocalDate)))
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

let private percent (part: int) (whole: int) =
    if whole = 0 then "—" else $"{int (Math.Round(100.0 * float part / float whole))}%%"

/// The project the day's ledger is filtered to, from the address.
let private dayProject (model: Model) =
    match model.Place with
    | Places.Day(_, project) -> project
    | _ -> None

let private today (model: Model) =
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
      text "weekHref" (hrefOf (Places.Week((weekOf model date).Start, project)))
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

// ---- track -------------------------------------------------------------------------

/// The person's recent combinations, as the forms offer them (33).
let private recentRows (model: Model) =
    Update.recentCombinations model
    |> List.mapi (fun index c ->
        let parts =
            [ referenceName model Reference.ActivityType c.ActivityTypeId
              referenceName model Reference.Project c.ProjectId
              c.Description ]
            |> List.filter (String.IsNullOrWhiteSpace >> not)

        [ t "id" (string index); t "label" (String.Join(" · ", parts)) ])

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

    [ flag "hasRecent" (not (Update.recentCombinations model).IsEmpty)
      items "manualRecent" (recentRows model)
      items
          "manualDurations"
          (let noStart = Format.parseIsoDate draft.StartDate |> Option.isNone || Format.parseTime draft.StartTime |> Option.isNone

           [ for minutes in Update.quickDurations model ->
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

// ---- more ----------------------------------------------------------------------------

let private referenceRows (model: Model) (kind: Reference.Kind) =
    Reference.all kind model.References
    |> List.map (fun item ->
        let active = item.Status = Reference.Active

        [ t "key" $"{Reference.kindName kind}:{item.Id}"
          t "name" item.Name
          f "active" active
          t "state" (if active then "Offered for new work" else "Archived: kept on past records") ])

/// The period containing the selected day, summarized (15).
let private currentPeriod (model: Model) =
    let mine = model.Ledger.Activities |> Map.toList |> List.map snd |> List.filter (fun a -> a.ActorId = model.Session.ActorId)
    let period = Periods.containing model.PeriodConfig (selectedDate model)
    Periods.summarize model.PeriodConfig [ billing model ] (Model.today model) mine period

let private periodLabel (period: Periods.Period) =
    let short (d: DateOnly) = d.ToString("MMM d", Globalization.CultureInfo.InvariantCulture)
    if period.Start = period.Finish then Format.longDate period.Start else $"{short period.Start} – {short period.Finish}"

let private cadenceName =
    function
    | Periods.Daily -> "daily"
    | Periods.Weekly -> "weekly"
    | Periods.Biweekly _ -> "biweekly"
    | Periods.SemiMonthly -> "semimonthly"
    | Periods.Monthly -> "monthly"

let private submissionText =
    function
    | Periods.NothingToSubmit -> "Nothing to submit"
    | Periods.NotSubmitted -> "Not submitted"
    | Periods.PartlySubmitted -> "Partly submitted"
    | Periods.FullySubmitted -> "Submitted"

let private approvalText =
    function
    | Periods.ApprovalNotRequired -> "Not required"
    | Periods.NothingApproved -> "Not approved"
    | Periods.PartlyApproved -> "Partly approved"
    | Periods.FullyApproved -> "Approved"

let private periodView (model: Model) =
    let summary = currentPeriod model
    let submission = submissionText summary.Submission
    let approval = approvalText summary.Approval

    let cadences =
        [ "daily", "Daily"; "weekly", "Weekly"; "biweekly", "Every two weeks"; "semimonthly", "Twice a month"; "monthly", "Monthly" ]
        |> List.map (fun (id, name) -> [ t "id" id; t "name" name; f "selected" (id = cadenceName model.PeriodConfig.Cadence) ])

    let days =
        [ DayOfWeek.Monday; DayOfWeek.Tuesday; DayOfWeek.Wednesday; DayOfWeek.Thursday; DayOfWeek.Friday; DayOfWeek.Saturday; DayOfWeek.Sunday ]
        |> List.map (fun d -> [ t "id" (string d); t "name" (string d); f "selected" (d = model.PeriodConfig.WeekStart) ])

    [ text "periodLabel" (periodLabel summary.Period)
      // The period's own page (CHX-460).
      text "periodHref" (hrefOf (Places.Period summary.Period.Start))
      text "periodExact" (Format.minutes summary.ExactMinutes)
      text "periodBillable" (Format.minutes summary.BillableMinutes)
      text "periodNonBillable" (Format.minutes summary.NonBillableMinutes)
      text "periodUnclassified" (Format.minutes summary.UnclassifiedMinutes)
      text "periodSubmission" submission
      text "periodApproval" approval
      items "periodCadenceOptions" cadences
      items "periodWeekStartOptions" days ]

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


// ---- changes not stored, and records edited outside Chrona (21, 34, 41) --------------

let private activityLabel (activity: Activity) =
    let times =
        match interval activity with
        | Some(start, finish) -> $", {Format.clock start} – {Format.clock finish}"
        | None -> ""

    $"{quoted activity.Classification.Description} on {Format.longDate activity.Occurrence.LocalDate}{times}"

/// What the change was, in a few words.
let private conflictTitle (case: ConflictCase) =
    match case.Request.Activities, case.Request.References, case.Request.Members, case.Request.RemovedMembers with
    | [ activity ], [], [], [] when activity.Revision = 1 -> $"Your new entry {quoted activity.Classification.Description} was not saved"
    | [ activity ], [], [], [] -> $"Your change to {quoted activity.Classification.Description} was not saved"
    | [], (_ :: _), [], [] -> "Your change to the lists was not saved"
    | [], [], _, _ -> "Your change to People was not saved"
    | activities, _, _, _ -> "Your change to " + plural activities.Length "activity" "activities" + " was not saved"

/// What diverged, for the person.
let private divergenceText (model: Model) (divergence: Reconcile.Divergence) =
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
    | Reconcile.KeptChanging -> "The records kept changing elsewhere while it was being saved. Nothing was found wrong with it."

/// The divergence the person can redo through a form, if any.
let private redoable (model: Model) (case: ConflictCase) =
    case.Divergences
    |> List.tryPick (function
        | Reconcile.ActivityChanged(mine, Some _) ->
            match model.Ledger.Activities.TryFind mine.ActivityId with
            | Some current when current.Record = Recorded && mine.Record = Recorded && current.Classification <> mine.Classification ->
                Some "Redo yours on the current version"
            | _ -> None
        | Reconcile.OverlapsStored(mine, _) when mine.Revision = 1 && model.Zone.IsSome -> Some "Change your entry's time"
        | _ -> None)

let private conflictView (model: Model) =
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

// ---- obligations (34) -------------------------------------------------------------

/// The latest attestation of each day, and what changed since (16).
let private staleAttestations (model: Model) =
    let workflow = { Review.start model.Ledger with Attestations = model.Attestations }

    model.Attestations
    |> List.groupBy _.LocalDate
    |> List.map (fun (date, all) -> date, List.last all)
    |> List.choose (fun (date, latest) ->
        match Review.attestationChanges workflow latest with
        | [] -> None
        | changed -> Some(date, changed))

let private actionLabel =
    function
    | "goTrack" -> "Complete it"
    | "openReview" -> "Review the day"
    | "goToday" -> "See the period"
    | _ -> "See details"

/// Unresolved work, as one projection rather than scattered warnings.
let private obligations (model: Model) =
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

// ---- the activity detail -------------------------------------------------------------

let private evidenceKinds =
    [ "url", "Link"
      "github-commit", "GitHub commit"
      "pull-request", "Pull request"
      "issue", "Issue"
      "document", "Document"
      "calendar-event", "Calendar event"
      "screenshot", "Screenshot"
      "linkedin-post", "LinkedIn post"
      "other", "Other" ]

let private kindLabel (kind: string) =
    evidenceKinds |> List.tryFind (fst >> (=) kind) |> Option.map snd |> Option.defaultValue kind

let private commandLabel =
    function
    | "create" -> "Recorded"
    | "amend" -> "Amended"
    | "void" -> "Removed from totals"
    | "restore" -> "Restored"
    | "split" -> "Split"
    | "merge" -> "Merged"
    | "evidence-link" -> "Evidence linked"
    | "evidence-unlink" -> "Evidence unlinked"
    | "accept-outside-edit" -> "Outside change accepted"
    | other -> other

let private localStamp (model: Model) (instant: DateTimeOffset) =
    match model.Zone with
    | Some zone ->
        let o = occurrence zone instant
        $"{Format.longDate o.LocalDate}, {Format.clock o.LocalTime}"
    | None -> instant.ToString("u")

let private detailView (model: Model) =
    let found =
        model.Detail
        |> Option.bind (fun d -> model.Ledger.Activities.TryFind d.ActivityId |> Option.map (fun a -> d, a))

    let blank =
        [ "detailTitle"; "detailClassification"; "detailTimes"; "detailDate"; "detailDuration"; "detailBilled"; "detailMethod"; "detailState"
          "detailRevision"; "detailPurpose"; "detailReason"; "detailTags"; "detailLineage"; "detailReplacedBy"; "amendDescription"; "amendPurpose"
          "amendReason"; "voidReason"; "splitFirst"; "splitSecond"; "splitTotal"; "evidenceUrl"; "evidenceLabel"; "evidenceSource"; "evidenceNotes" ]

    match found with
    | None ->
        [ flag "detailFound" false
          // Its record may still arrive (the records are opening, or its month is
          // being read). A record that does not exist is the not-found page.
          flag "detailLoading" (match model.Place with Places.Entry _ -> model.RouteProblem.IsNone | _ -> false)
          flag "detailRecorded" false
          flag "detailVoided" false
          flag "detailSuperseded" false
          flag "detailCanSplit" false
          flag "detailHasEvidence" false
          yield! blank |> List.map (fun key -> text key "")
          items "amendTypeOptions" []
          items "amendProjectOptions" []
          items "detailEvidence" []
          items "splitEvidence" []
          items "evidenceKindOptions" []
          items "detailHistory" []
          text "detailHistoryNote" ""
          flag "hasAmendProblems" false
          items "amendProblems" []
          flag "hasVoidProblems" false
          items "voidProblems" []
          flag "hasSplitProblems" false
          items "splitProblems" []
          flag "hasEvidenceProblems" false
          items "evidenceProblems" [] ]
    | Some(d, a) ->
        let detailMissing = flag "detailMissing" false

        let recorded, voided, superseded =
            match a.Record with
            | Recorded -> true, false, false
            | Voided _ -> false, true, false
            | Superseded _ -> false, false, true

        let times =
            match interval a with
            | Some(start, finish) -> $"{Format.clock start} – {Format.clock finish}"
            | None -> "No clock times"

        let lineage =
            match a.Lineage with
            | [] -> ""
            | [ source ] when source.StartsWith "TMR" -> "From a timer."
            | sources ->
                let names =
                    sources
                    |> List.map (fun id -> model.Ledger.Activities.TryFind id |> Option.map (fun s -> quoted s.Classification.Description) |> Option.defaultValue id)

                $"From {commaList names}."

        let replacedBy =
            match a.Record with
            | Superseded children ->
                let names =
                    children
                    |> List.map (fun id -> model.Ledger.Activities.TryFind id |> Option.map (fun c -> $"{quoted c.Classification.Description} ({Format.minutes c.Minutes})") |> Option.defaultValue id)

                $"Replaced by {commaList names}. It no longer counts toward totals."
            | _ -> ""

        let history =
            // Newest first, in the order the record's revisions were made:
            // the same whether the trail was kept in this page or read back.
            let revisionOf (entry: Ledger.AuditEntry) =
                entry.ResultingRevisions |> List.tryFind (fst >> (=) a.ActivityId) |> Option.map snd |> Option.defaultValue 0

            model.Ledger.Audit
            |> List.filter (fun entry -> List.contains a.ActivityId entry.ActivityIds)
            |> List.sortByDescending (fun entry -> entry.At, revisionOf entry)
            |> List.map (fun entry ->
                let index = $"{entry.At.UtcTicks}-{entry.Command}-{revisionOf entry}"
                let revisions =
                    match entry.PriorRevisions |> List.tryFind (fst >> (=) a.ActivityId), entry.ResultingRevisions |> List.tryFind (fst >> (=) a.ActivityId) with
                    | Some(_, before), Some(_, after) -> $"Revision {before} → {after}"
                    | None, Some(_, after) -> $"Revision {after}"
                    | _ -> ""

                [ t "id" $"{index}"
                  t "action" (commandLabel entry.Command)
                  t "at" (localStamp model entry.At)
                  t "revisions" revisions
                  t "reason" (entry.Reason |> Option.filter (String.IsNullOrWhiteSpace >> not) |> Option.map (fun r -> $"Reason: {r}") |> Option.defaultValue "") ])

        [ flag "detailFound" true
          detailMissing
          flag "detailRecorded" recorded
          flag "detailVoided" voided
          flag "detailSuperseded" superseded
          flag "detailCanSplit" (recorded && a.Minutes >= 2)
          flag "detailHasEvidence" (not a.Evidence.IsEmpty)
          text "detailTitle" a.Classification.Description
          text "detailClassification" $"{referenceName model Reference.ActivityType a.Classification.ActivityTypeId} · {referenceName model Reference.Project a.Classification.ProjectId}"
          text "detailTimes" times
          text "detailDate" (Format.longDate a.Occurrence.LocalDate)
          text "detailDuration" (Format.minutes a.Minutes)
          text "detailBilled" $"{Format.minutes (Billing.billableMinutes (billing model) a.Minutes)} billed"
          text "detailMethod" (methodLabel a.EntryMethod)
          text "detailState" (match a.Record with Recorded -> "Recorded" | Voided _ -> "Removed from totals" | Superseded _ -> "Superseded")
          text "detailRevision" (string a.Revision)
          text "detailPurpose" a.Classification.BusinessPurpose
          text "detailReason" (a.Reason |> Option.defaultValue "None")
          text "detailTags" (match a.Classification.Tags with [] -> "None" | tags -> tags |> List.map (referenceName model Reference.Tag) |> String.concat ", ")
          text "detailLineage" lineage
          text "detailReplacedBy" replacedBy
          items "amendTypeOptions" (options model Reference.ActivityType d.Amend.ActivityTypeId @ (if Reference.selectable Reference.ActivityType model.References |> List.exists (fun i -> i.Id = d.Amend.ActivityTypeId) then [] else [ [ t "id" d.Amend.ActivityTypeId; t "name" $"{referenceName model Reference.ActivityType d.Amend.ActivityTypeId} (archived)"; f "selected" true ] ]))
          items "amendProjectOptions" (options model Reference.Project d.Amend.ProjectId @ (if Reference.selectable Reference.Project model.References |> List.exists (fun i -> i.Id = d.Amend.ProjectId) then [] else [ [ t "id" d.Amend.ProjectId; t "name" $"{referenceName model Reference.Project d.Amend.ProjectId} (archived)"; f "selected" true ] ]))
          text "amendDescription" d.Amend.Description
          text "amendPurpose" d.Amend.BusinessPurpose
          text "amendReason" d.AmendReason
          text "voidReason" d.VoidReason
          text "splitFirst" d.SplitFirst
          text "splitSecond" d.SplitSecond
          text "splitTotal" $"{Format.minutes a.Minutes} to share: the parts must add up to exactly {a.Minutes} minutes."
          items
              "detailEvidence"
              (a.Evidence
               |> List.map (fun e ->
                   [ t "id" e.Id
                     t "label" e.Label
                     t "kind" (kindLabel e.Kind)
                     t "url" e.Url
                     f "hasUrl" (e.Url <> "")
                     t "captured" (localStamp model e.CapturedAt)
                     t "source" (e.Source |> Option.map (fun source -> $"Source: {source}") |> Option.defaultValue "")
                     t "notes" (e.Notes |> Option.defaultValue "") ]))
          items
              "splitEvidence"
              (a.Evidence
               |> List.map (fun e ->
                   let part = d.SplitEvidence.TryFind e.Id |> Option.defaultValue 0
                   [ t "id" e.Id; t "label" e.Label; f "toFirst" (part = 1); f "toSecond" (part = 2); f "toNeither" (part = 0) ]))
          items "evidenceKindOptions" (evidenceKinds |> List.map (fun (id, name) -> [ t "id" id; t "name" name; f "selected" (id = d.EvidenceKind) ]))
          text "evidenceUrl" d.EvidenceUrl
          text "evidenceLabel" d.EvidenceLabel
          text "evidenceSource" d.EvidenceSource
          text "evidenceNotes" d.EvidenceNotes
          items "detailHistory" history
          // Revisions made before the audit trail was stored have no entry (WI-0056).
          text
              "detailHistoryNote"
              (let covered =
                  model.Ledger.Audit
                  |> List.collect _.ResultingRevisions
                  |> List.filter (fst >> (=) a.ActivityId)
                  |> List.map snd
                  |> Set.ofList

               if [ 1 .. a.Revision ] |> List.forall covered.Contains then
                   ""
               else
                   "Some earlier revisions were made before Chrona kept its history with the records; Git history is the only account of them.")
          flag "hasAmendProblems" (model.Problems.ContainsKey AmendForm)
          items "amendProblems" (problemItems model AmendForm)
          flag "hasVoidProblems" (model.Problems.ContainsKey VoidForm)
          items "voidProblems" (problemItems model VoidForm)
          flag "hasSplitProblems" (model.Problems.ContainsKey SplitForm)
          items "splitProblems" (problemItems model SplitForm)
          flag "hasEvidenceProblems" (model.Problems.ContainsKey EvidenceForm)
          items "evidenceProblems" (problemItems model EvidenceForm) ]

// ---- the day review ------------------------------------------------------------------

let private reviewView (model: Model) =
    let date = selectedDate model
    let shown = dayActivities model date
    let counted = shown |> List.filter consumesTime
    let byMethod m = counted |> List.filter (fun a -> a.EntryMethod = m)
    let summary (list: Activity list) = $"{list.Length} · {Format.minutes (list |> List.sumBy _.Minutes)}"
    let attested = model.Attestations |> List.filter (fun a -> a.LocalDate = date)
    let changed = staleAttestations model |> List.tryFind (fst >> (=) date) |> Option.map snd |> Option.defaultValue []
    let historical = counted |> List.filter (fun a -> a.EntryMethod = Manual && date < Model.today model)

    let checks =
        [ "descriptions", "Every activity says what was done", (counted |> List.forall (fun a -> a.Classification.Description <> "")), "Complete", "Missing"
          "purposes", "Every activity has a business purpose", (counted |> List.forall (fun a -> a.Classification.BusinessPurpose <> "")), "Complete", "Missing"
          "overlap", "No overlapping time", true, "Clear", "Overlap"
          "explained", "Manual entries for an earlier day are explained", (historical |> List.forall (fun a -> a.Reason.IsSome)), "Included", "Missing"
          "attested", "Nothing changed since the last attestation", changed.IsEmpty, (if attested.IsEmpty then "Not yet attested" else "Unchanged"), "Changed"
          "saved", "Every change has been stored", model.Store.Problem.IsNone && model.Store.Pending.IsEmpty && model.Store.Conflicts.IsEmpty, (match model.Store.Kind with InMemory -> "In this tab" | Durable _ -> "Saved"), "Attention" ]
        |> List.map (fun (id, title, ok, good, bad) ->
            [ t "id" id; t "title" title; t "status" (if ok then good else bad); t "tone" (if ok then "ok" else "attention") ])

    [ flag "screenReview" (shows model (function Places.ReviewToday | Places.Review _ -> true | _ -> false))
      text "reviewTitle" (Format.longDate date)
      text "reviewTotal" (Format.minutes (counted |> List.sumBy _.Minutes))
      text "reviewCount" (plural counted.Length "included entry" "included entries")
      text "reviewTimer" (summary (byMethod EntryMethod.Timer))
      text "reviewManual" (summary (byMethod Manual))
      text "reviewEvidence" (percent (counted |> List.filter (fun a -> not a.Evidence.IsEmpty) |> List.length) counted.Length)
      text "reviewCorrections" (counted |> List.filter (fun a -> a.Revision > 1) |> List.length |> string)
      items "reviewChecks" checks
      flag "hasAttestations" (not attested.IsEmpty)
      items
          "attestations"
          (attested
           |> List.rev
           |> List.mapi (fun index a -> [ t "id" $"{index}"; t "statement" a.Statement; t "at" $"Attested {localStamp model a.At} · {activities a.Covered.Length}" ]))
      flag "changedSinceAttestation" (not changed.IsEmpty)
      items
          "changedActivities"
          (changed
           |> List.map (fun id ->
               [ t "id" id
                 t "title" (model.Ledger.Activities.TryFind id |> Option.map _.Classification.Description |> Option.defaultValue id) ]))
      text "attestStatement" model.AttestStatement
      flag "hasAttestProblems" (model.Problems.ContainsKey AttestForm)
      items "attestProblems" (problemItems model AttestForm) ]

// ---- the month ------------------------------------------------------------------------

let private monthView (model: Model) =
    let first = selectedMonth model
    let last = first.AddMonths(1).AddDays -1

    let inMonth =
        model.Ledger.Activities
        |> Map.toList
        |> List.map snd
        |> List.filter (fun a -> a.ActorId = model.Session.ActorId && a.Occurrence.LocalDate >= first && a.Occurrence.LocalDate <= last)

    let counted = inMonth |> List.filter consumesTime
    let total = counted |> List.sumBy _.Minutes
    let sumOf m = counted |> List.filter (fun a -> a.EntryMethod = m) |> List.sumBy _.Minutes

    let byType =
        counted
        |> List.groupBy _.Classification.ActivityTypeId
        |> List.map (fun (id, list) -> id, list |> List.sumBy _.Minutes)
        |> List.sortByDescending snd
        |> List.map (fun (id, minutes) ->
            [ t "id" id
              t "name" (referenceName model Reference.ActivityType id)
              t "total" (Format.minutes minutes)
              n "share" (if total = 0 then 0.0 else Math.Round(100.0 * float minutes / float total))
              t "shareText" (percent minutes total) ])

    let days =
        counted
        |> List.groupBy _.Occurrence.LocalDate
        |> List.sortBy fst
        |> List.map (fun (date, list) ->
            [ t "id" (Format.isoDate date)
              t "label" (Format.longDate date)
              t "total" (Format.minutes (list |> List.sumBy _.Minutes))
              t "count" (plural list.Length "entry" "entries") ])

    let reading = List.contains (first.Year, first.Month) model.Store.Reading

    let history =
        model.Store.History
        |> List.filter (fun total -> total.ActorId = model.Session.ActorId)
        |> List.map (fun total ->
            let month = DateOnly(total.Year, total.Month, 1)

            [ t "id" $"{total.Year:D4}-{total.Month:D2}"
              t "label" (month.ToString("MMMM yyyy", Globalization.CultureInfo.InvariantCulture))
              t "total" (Format.minutes total.Minutes)
              t "count" (plural total.Activities "entry" "entries")
              t "approved" (if total.ApprovedMinutes = 0 then "" else $"{Format.minutes total.ApprovedMinutes} approved")
              t "current" (if month = first then "true" else "false") ])

    [ flag "screenMonth" (shows model (function Places.ThisMonth | Places.Month _ -> true | _ -> false))
      text "monthTitle" (first.ToString("MMMM yyyy", Globalization.CultureInfo.InvariantCulture))
      text "monthIso" $"{first.Year:D4}-{first.Month:D2}"
      text "monthTotal" (Format.minutes total)
      text "monthBilled" (Format.minutes (counted |> List.sumBy (fun a -> Billing.billableMinutes (billing model) a.Minutes)))
      text "monthDecimal" (Format.decimalHours total)
      text "monthActiveDays" (string days.Length)
      text "monthTimerManual" $"{Format.minutes (sumOf EntryMethod.Timer)} / {Format.minutes (sumOf Manual)}"
      text "monthCorrections" (counted |> List.filter (fun a -> a.Revision > 1) |> List.length |> string)
      text "monthVoided" (inMonth |> List.filter (fun a -> match a.Record with Voided _ -> true | _ -> false) |> List.length |> string)
      text "monthEvidence" (percent (counted |> List.filter (fun a -> not a.Evidence.IsEmpty) |> List.length) counted.Length)
      flag "monthEmpty" (counted.IsEmpty && not reading)
      flag "monthReading" reading
      items "monthByType" byType
      items "monthDays" days
      // Every month that holds the person's time, from the activity index
      // (derived, rebuildable): one read, not one per month (38, 40).
      flag "hasMonthHistory" (not history.IsEmpty)
      items "monthHistory" history
      text "monthIndex" model.Store.Index ]

// ---- reports ------------------------------------------------------------------------

let private reportView (model: Model) =
    let filter = Update.reportFilter model
    let draft = model.Report
    let all = model.Ledger.Activities |> Map.toList |> List.map snd
    let policies = [ billing model ]
    let rows = Reports.select filter all
    let totals = Reports.totals policies rows

    let choice (id: string) (name: string) (current: string) = [ t "id" id; t "name" name; f "selected" (id = current) ]

    let anyOf (kind: Reference.Kind) (current: string) =
        choice "" "Any" current
        :: (Reference.all kind model.References |> List.map (fun item -> choice item.Id item.Name current))

    let grouping, groupName =
        match draft.Grouping with
        | "activityType" -> Reports.ByActivityType, (fun id -> referenceName model Reference.ActivityType id)
        | "tag" -> Reports.ByTag, (fun id -> if id = "" then "No tag" else referenceName model Reference.Tag id)
        | "day" -> Reports.ByDay, (fun id -> Format.parseIsoDate id |> Option.map Format.longDate |> Option.defaultValue id)
        | _ -> Reports.ByProject, (fun id -> referenceName model Reference.Project id)

    let groups =
        Reports.groupBy policies grouping rows
        |> List.map (fun (key, sum) ->
            [ t "id" (if key = "" then "(none)" else key)
              t "name" (groupName key)
              t "exact" (Format.minutes sum.ExactMinutes)
              t "billable" (Format.minutes sum.BillableMinutes)
              t "count" (string sum.Count) ])

    let rowItems =
        rows
        |> List.map (fun a ->
            let times =
                match interval a with
                | Some(start, finish) -> $"{Format.clock start} – {Format.clock finish}"
                | None -> ""

            [ t "id" a.ActivityId
              t "date" (Format.longDate a.Occurrence.LocalDate)
              t "time" times
              t "title" a.Classification.Description
              t "classification" $"{referenceName model Reference.ActivityType a.Classification.ActivityTypeId} · {referenceName model Reference.Project a.Classification.ProjectId}"
              t "method" (methodLabel a.EntryMethod)
              t "exact" (Format.minutes a.Minutes)
              t "billed" (Format.minutes (Reports.billed policies a))
              t "state" (match a.Record with Voided _ -> "Removed from totals" | _ -> "Recorded") ])

    let range = $"{Format.longDate filter.From} to {Format.longDate filter.To}"

    [ flag "screenReports" (shows model (function Places.Reports _ -> true | _ -> false) && canWork model)
      text "reportFrom" (Format.isoDate filter.From)
      text "reportTo" (Format.isoDate filter.To)
      text "reportText" draft.Text
      text "reportRange" range
      flag "reportIncludeRemoved" draft.IncludeRemoved
      items "reportProjectOptions" (anyOf Reference.Project draft.ProjectId)
      items "reportTypeOptions" (anyOf Reference.ActivityType draft.ActivityTypeId)
      items "reportTagOptions" (anyOf Reference.Tag draft.Tag)
      items "reportMethodOptions" [ choice "" "Any" draft.Method; choice "manual" "Manual entry" draft.Method; choice "timer" "Timer" draft.Method ]
      items "reportBillabilityOptions" [ choice "" "Any" draft.Billability; choice "billable" "Billable" draft.Billability; choice "non-billable" "Non-billable" draft.Billability; choice "pending" "Not yet classified" draft.Billability ]
      items "reportGroupingOptions" [ choice "project" "Project" draft.Grouping; choice "activityType" "Activity type" draft.Grouping; choice "tag" "Tag" draft.Grouping; choice "day" "Day" draft.Grouping ]
      items "reportFormatOptions" [ choice "csv" "CSV" draft.Format; choice "json" "JSON" draft.Format ]
      text "reportCount" (plural totals.Count "activity" "activities")
      text "reportExact" (Format.minutes totals.ExactMinutes)
      text "reportBillable" (Format.minutes totals.BillableMinutes)
      text "reportNonBillable" (Format.minutes totals.NonBillableMinutes)
      text "reportTimerManual" $"{Format.minutes totals.TimerMinutes} / {Format.minutes totals.ManualMinutes}"
      text "reportApproved" $"{Format.minutes totals.ApprovedMinutes} / {Format.minutes totals.UnapprovedMinutes}"
      text "reportCorrections" $"{totals.AmendedAfterReview} / {totals.CorrectedAfterPublication}"
      flag "reportEmpty" rows.IsEmpty
      items "reportRows" rowItems
      items "reportGroups" groups
      text "exportText" (Update.exportText model)
      text "copyStatus" model.CopyStatus
      flag "hasExportProblems" (model.Problems.ContainsKey ExportForm)
      items "exportProblems" (problemItems model ExportForm)
      text "printTitle" "Time report"
      text "printSubtitle" $"{model.Session.DisplayName} · {range}"
      text "printGenerated" $"Generated {localStamp model model.Now} · {Reports.Schema}" ]

// ---- the week, a period and projects (CHX-460) --------------------------------------

/// The person's own records that were read.
let private mine (model: Model) =
    model.Ledger.Activities |> Map.toList |> List.map snd |> List.filter (fun a -> a.ActorId = model.Session.ActorId)

/// One row per day from `first` to `last`: its counted time, linked to the
/// day's ledger (with the same project filter).
let private dayRows (project: string option) (first: DateOnly) (last: DateOnly) (counted: Activity list) =
    [ for offset in 0 .. last.DayNumber - first.DayNumber do
          let date = first.AddDays offset
          let onDay = counted |> List.filter (fun a -> a.Occurrence.LocalDate = date)

          [ t "id" (Format.isoDate date)
            t "label" (Format.longDate date)
            t "total" (Format.minutes (onDay |> List.sumBy _.Minutes))
            t "count" (plural onDay.Length "entry" "entries")
            t "href" (hrefOf (Places.Day(date, project))) ] ]

let private projectOptions (model: Model) (project: string option) =
    [ t "id" ""; t "name" "All projects"; f "selected" project.IsNone ]
    :: (Reference.all Reference.Project model.References
        |> List.map (fun item -> [ t "id" item.Id; t "name" item.Name; f "selected" (Some item.Id = project) ]))

let private weekView (model: Model) =
    let on, project =
        match model.Place with
        | Places.Week(on, project) -> on, project
        | _ -> Model.today model, None

    let week = weekOf model on

    let counted =
        mine model
        |> List.filter (fun a -> consumesTime a && Periods.contains week a.Occurrence.LocalDate && project |> Option.forall ((=) a.Classification.ProjectId))

    let total = counted |> List.sumBy _.Minutes
    let billed = counted |> List.sumBy (fun a -> Billing.billableMinutes (billing model) a.Minutes)

    [ flag "screenWeek" (shows model (function Places.ThisWeek | Places.Week _ -> true | _ -> false))
      text "weekTitle" (periodLabel week)
      text "weekTotal" (Format.minutes total)
      text "weekBilled" $"{Format.minutes billed} billed"
      text "weekDecimal" (Format.decimalHours total)
      text "weekEntryCount" (plural counted.Length "entry" "entries")
      items "weekDays" (dayRows project week.Start week.Finish counted)
      items "weekProjectOptions" (projectOptions model project)
      flag "weekFiltered" project.IsSome
      text "weekFilterName" (project |> Option.map (referenceName model Reference.Project) |> Option.defaultValue "") ]

let private cadenceLabel =
    function
    | Periods.Daily -> "Daily"
    | Periods.Weekly -> "Weekly"
    | Periods.Biweekly _ -> "Every two weeks"
    | Periods.SemiMonthly -> "Twice a month"
    | Periods.Monthly -> "Monthly"

/// A timesheet period's own page: its summary and its days (15).
let private periodPage (model: Model) =
    let on =
        match model.Place with
        | Places.Period on -> on
        | _ -> Model.today model

    let period = Periods.containing model.PeriodConfig on
    let summary = Periods.summarize model.PeriodConfig [ billing model ] (Model.today model) (mine model) period
    let counted = mine model |> List.filter (fun a -> consumesTime a && Periods.contains period a.Occurrence.LocalDate)

    [ flag "screenPeriod" (shows model (function Places.ThisPeriod | Places.Period _ -> true | _ -> false))
      text "periodPageTitle" (periodLabel period)
      text "periodPageCadence" $"{cadenceLabel model.PeriodConfig.Cadence} timesheet period"
      text "periodPageExact" (Format.minutes summary.ExactMinutes)
      text "periodPageBillable" (Format.minutes summary.BillableMinutes)
      text "periodPageNonBillable" (Format.minutes summary.NonBillableMinutes)
      text "periodPageUnclassified" (Format.minutes summary.UnclassifiedMinutes)
      text "periodPageSubmission" (submissionText summary.Submission)
      text "periodPageApproval" (approvalText summary.Approval)
      items "periodDays" (dayRows None period.Start period.Finish counted) ]

let private projectState (item: Reference.Item) =
    if item.Status = Reference.Active then "Offered for new work" else "Archived: kept on past records"

let private projectsView (model: Model) =
    let projects = Reference.all Reference.Project model.References

    [ flag "screenProjects" (shows model ((=) Places.Projects))
      flag "projectsEmpty" projects.IsEmpty
      items
          "projectList"
          (projects
           |> List.map (fun item -> [ t "id" item.Id; t "name" item.Name; t "state" (projectState item); t "href" (hrefOf (Places.Project item.Id)) ])) ]

/// One project: the person's time on it, and where to look further.
let private projectView (model: Model) =
    let id =
        match model.Place with
        | Places.Project id -> id
        | _ -> ""

    let item = Reference.all Reference.Project model.References |> List.tryFind (fun item -> item.Id = id)

    let entries =
        mine model
        |> List.filter (fun a -> a.Classification.ProjectId = id && (match a.Record with Superseded _ -> false | _ -> true))
        |> List.sortByDescending (fun a -> a.Occurrence.LocalDate, a.Occurrence.LocalTime, a.ActivityId)

    let counted = entries |> List.filter consumesTime
    let total = counted |> List.sumBy _.Minutes
    let today = Model.today model

    [ flag "screenProject" (shows model (function Places.Project _ -> true | _ -> false))
      text "projectName" (item |> Option.map _.Name |> Option.defaultValue "")
      text "projectState" (item |> Option.map projectState |> Option.defaultValue "")
      text "projectTotal" (Format.minutes total)
      text "projectBilled" $"{Format.minutes (counted |> List.sumBy (fun a -> Billing.billableMinutes (billing model) a.Minutes))} billed"
      text "projectEntryCount" (plural counted.Length "entry" "entries")
      flag "projectEmpty" (entries.IsEmpty && model.Store.Reading.IsEmpty)
      flag "projectReading" (not model.Store.Reading.IsEmpty)
      items
          "projectEntries"
          (entries
           |> List.truncate 50
           |> List.map (fun a ->
               [ t "id" a.ActivityId
                 t "date" (Format.longDate a.Occurrence.LocalDate)
                 t "title" a.Classification.Description
                 t "duration" (Format.minutes a.Minutes)
                 t "state" (match a.Record with Voided _ -> "Removed from totals" | _ -> "Recorded")
                 t "href" (hrefOf (Places.Entry(a.ActivityId, Some a.Occurrence.LocalDate))) ]))
      text "projectTodayHref" (hrefOf (Places.Day(today, Some id)))
      text "projectWeekHref" (hrefOf (Places.Week((weekOf model today).Start, Some id)))
      text "projectReportHref" (hrefOf (Places.Reports { Places.allReports with ProjectId = Some id })) ]

// ---- addresses: not found, not permitted, copy link (CHX-460) -----------------------

/// What Limen's expectation for a parameter means, for a person.
let private expectedText (expected: string) =
    match expected with
    | "date" -> "a date such as 2026-10-08"
    | "month" -> "a month such as 2026-10"
    | "int" -> "a whole number"
    | "bool" -> "true or false"
    | "a single value" -> "given once"
    | other when other.StartsWith "one of " -> "one of " + (other.Substring 7).Replace("|", ", ")
    | other -> other

let private problemView (model: Model) =
    let kind, title, detail =
        match model.RouteProblem with
        | None -> "", "", ""
        | Some(RecordMissing(what, id)) ->
            "not-found", "Not found", $"Nothing in your records is the {what} with the id \"{id}\". It may have been removed, or the link may be wrong."
        | Some(AddressProblem problem) ->
            match problem with
            | Limen.Routing.RouteError.NotPermitted route when route = Places.Names.Entry ->
                "not-permitted", "Not permitted", "This activity is someone else's. Only your own time is shown to you."
            | Limen.Routing.RouteError.NotPermitted _ ->
                "not-permitted", "Not permitted", "This part of Chrona is for the organization's administrators. Ask one of them if you need it."
            | Limen.Routing.RouteError.Invalid(_, parameter, value, expected) ->
                "not-found", "Page not found", $"This address is not one of Chrona's pages: its {parameter} \"{value}\" should be {expectedText expected}."
            | Limen.Routing.RouteError.Malformed _ ->
                "not-found", "Page not found", "This address could not be read. It may have been cut short or changed when it was copied."
            | Limen.Routing.RouteError.NotFound
            | Limen.Routing.RouteError.RedirectLoop _
            | Limen.Routing.RouteError.Unmapped _ -> "not-found", "Page not found", "Chrona has no page at this address."

    [ flag "screenProblem" model.RouteProblem.IsSome
      text "problemKind" kind
      text "problemTitle" title
      text "problemDetail" detail ]

/// "Copy link" for the view shown: offered whenever the address names one.
let private linkView (model: Model) =
    [ flag "canCopyLink" model.RouteProblem.IsNone
      flag "hasLinkStatus" (model.LinkStatus <> "")
      text "linkStatus" model.LinkStatus
      // The browser would not copy it: the link, to select by hand.
      flag "linkRefused" (model.LinkStatus <> "" && model.LinkText <> "")
      text "linkText" model.LinkText ]

// ---- the whole view ---------------------------------------------------------------

// ---- sign-in (CHX-022, CHX-023) ---------------------------------------------------

/// What a sign-in outcome means to the person, by its stable code.
let noticeText (code: string) =
    match code with
    | "signed_out" -> "You signed out. This tab no longer holds your GitHub token."
    | "state_invalid" -> "That sign-in did not start in this tab, or was already used. Sign in again."
    | "state_expired" -> "The sign-in took longer than ten minutes. Sign in again."
    | "provider_denied" -> "GitHub sign-in was cancelled."
    | "expired" -> "Your session ended. Sign in again."
    | "revoked" -> "Your session is no longer valid. Sign in again."
    | "provider_unavailable" -> "GitHub or the sign-in service cannot be reached. Chrona will try again."
    | "repository_access_denied" -> "Your GitHub account cannot read this deployment's data repository."
    | "sign_in_failed" -> "Sign-in could not start. Try again."
    | other -> $"Sign-in was refused ({other})."

let private retentionText =
    function
    | ThisPage -> "Your token is kept in this page only: reloading or closing it signs you out."
    | ThisTab -> "Your token is kept in this tab until it closes."

let private identityView (model: Model) =
    let identity = model.Identity
    let notice = identity.Notice

    [ flag "shellVisible" (canWork model)
      flag "screenConfiguring" (identity.Mode = Configuring)
      flag "screenMisconfigured" (match identity.Mode with Misconfigured _ -> true | _ -> false)
      text "misconfiguredDetail" (match identity.Mode with Misconfigured detail -> detail | _ -> "")
      flag "screenSignIn" (match identity.Mode with SignInRequired _ -> true | _ -> false)
      flag "signInBusy" (identity.Mode = SignInRequired true)
      text "signInButton" (if identity.Mode = SignInRequired true then "Signing in…" else "Sign in with GitHub")
      flag "retentionPage" (identity.Retention = ThisPage)
      flag "retentionTab" (identity.Retention = ThisTab)
      flag "hasSignInNotice" (notice.IsSome && not (canWork model))
      flag "hasSessionNotice" (notice.IsSome && canWork model)
      text "signInNotice" (notice |> Option.map noticeText |> Option.defaultValue "")
      text "signInNoticeCode" (notice |> Option.defaultValue "")
      flag "accountSignedIn" (identity.Mode = SignedInMode)
      // Signing out with unsent changes (WI-0058).
      flag "signOutChoosing" (identity.SignOut = Some ChoosingUnsent)
      flag "signOutSending" (identity.SignOut = Some SendingUnsent)
      flag "signOutConfirming" (identity.SignOut = Some ConfirmingDiscard)
      flag "signOutDiscarding" (identity.SignOut = Some DiscardingUnsent)
      flag "signOutCannotSend" (not (Update.canSendUnsent model))
      flag "signOutCannotKeep" (not (Update.canKeepUnsent model))
      text
          "signOutSummary"
          (let count = Update.unsentCount model
           let changes = if count = 1 then "1 change has" else $"{count} changes have"

           match count, Update.hasKeptTimer model with
           | 0, true -> "Your timer is on this device. Signing out must not leave it behind without your knowing."
           | _, true -> changes + " not reached GitHub, and your timer is on this device. Signing out must not leave them behind without your knowing."
           | _ -> changes + " not reached GitHub. Signing out must not leave them behind without your knowing.")
      text "signOutNote" (identity.SignOutNote |> Option.defaultValue "")
      text
          "signOutDiscardText"
          (let count = Update.unsentCount model
           let changes = if count = 1 then "1 change" else $"{count} changes"

           (match count, Update.hasKeptTimer model with
            | 0, true -> "Discard your timer?"
            | _, true -> $"Discard {changes} and your timer?"
            | _ -> $"Discard {changes}?")
           + " They will not be saved anywhere, and this cannot be undone.")
      flag "accountLocal" (identity.Mode = LocalOnly)
      text "accountLogin" model.Session.DisplayName
      text "accountProvider" (match model.Session.Kind with SignedIn provider -> provider | LocalSession -> "")
      text "accountRetention" (retentionText identity.Retention)
      flag "screenOpening" (identity.Mode = SignedInMode && model.Store.Opening)
      flag "screenStoreFailed" (identity.Mode = SignedInMode && model.Store.Failure.IsSome)
      // A newer Chrona deployed since this page started (WI-0063).
      flag "shellUpdate" model.Shell.Newer.IsSome
      flag "shellReloadBlocked" (not model.Store.Conflicts.IsEmpty)
      text
          "shellUpdateText"
          (if model.Store.Conflicts.IsEmpty then
               "Reload when you are ready. Your unsent changes and your timer are kept; text typed into a form and not saved is not."
           else
               "Resolve the changes not saved under More first: they live only in this page.")
      text "storeFailure" (model.Store.Failure |> Option.defaultValue "")
      // Starting offline: the device's own timer and unsent changes (WI-0055).
      flag "offlineTimer" (identity.Mode = SignedInMode && model.Store.Failure.IsSome && Update.hasKeptTimer model)
      text
          "offlineWaiting"
          (match model.Store.Waiting with
           | 0 -> ""
           | 1 -> "1 change waits in this browser and is sent when your records open."
           | n -> $"{n} changes wait in this browser and are sent when your records open.")
      flag "offlineStopped" model.Stopped.IsSome
      text
          "offlineStoppedText"
          (model.Stopped
           |> Option.map (fun stopped -> $"Stopped: {Format.minutes stopped.TotalMinutes} held. Complete it once your records open.")
           |> Option.defaultValue "")
      flag "hasStoreProblems" (not model.Store.Integrity.IsEmpty)
      text "storeProblemSummary" (plural model.Store.Integrity.Length "record needs attention" "records need attention")
      items
          "storeProblems"
          (model.Store.Integrity
           |> List.mapi (fun index d -> [ t "id" $"{index}"; t "code" (code d); t "text" (describe model d) ])) ]

let private accessName (capabilities: Set<Access.Capability>) =
    if capabilities = Access.Grants.administrator then "administrator"
    elif capabilities = Access.Grants.reviewer then "reviewer"
    elif capabilities = Access.Grants.ownTime then "ownTime"
    else "custom"

let private organizationName (model: Model) (organizationId: string) =
    model.Deployment
    |> Option.bind (fun config -> Deployment.organization config organizationId)
    |> Option.map _.DisplayName
    |> Option.defaultValue organizationId

/// The organizations of the deployment, the members of the current one,
/// and the gate for someone who is not a member (3, 2.5).
let private membershipView (model: Model) =
    let organizations = model.Deployment |> Option.map _.Organizations |> Option.defaultValue []
    let me = model.Session.ActorId
    let subject = (me.Split(':', 2) |> Array.last)

    let notMember =
        model.Identity.Mode = SignedInMode
        && not model.Store.Opening
        && model.Store.Failure.IsNone
        && model.Store.Confirmation.IsNone
        && not (model.Roster.Members.ContainsKey me)

    [ flag "screenNotMember" notMember
      flag "screenConfirmAdministrator" (model.Identity.Mode = SignedInMode && model.Store.Confirmation.IsSome)
      text "confirmationDetail" (model.Store.Confirmation |> Option.map fst |> Option.defaultValue "")
      flag "canConfirmAdministrator" (model.Store.Confirmation |> Option.exists snd)
      text
          "notMemberDetail"
          $"You signed in as {model.Session.DisplayName}. GitHub account {subject} is not a member of {organizationName model model.Session.OrganizationId}. Ask one of its administrators to add GitHub account {subject}."
      text "organizationName" (organizationName model model.Session.OrganizationId)
      flag "canChooseOrganization" (model.Identity.Mode = SignedInMode && organizations.Length > 1)
      items
          "organizationOptions"
          [ for organization in organizations ->
                [ t "id" organization.Id; t "name" organization.DisplayName; f "selected" (organization.Id = model.Session.OrganizationId) ] ]
      // A local session has no one else to add.
      flag "canManageMembers" (model.Identity.Mode = SignedInMode && permits model Access.ManageOrganizationSettings)
      items
          "members"
          [ for KeyValue(id, membership) in model.Roster.Members ->
                [ t "id" id
                  t "name" membership.Principal.DisplayName
                  t "account" (id.Split(':', 2) |> Array.last)
                  t "access" (accessName membership.Capabilities)
                  f "isYou" (id = me) ] ]
      text "memberId" model.MemberDraft.Id
      text "memberName" model.MemberDraft.Name
      text "memberAccess" model.MemberDraft.Access
      flag "hasMemberProblems" (model.Problems.ContainsKey MemberForm)
      items "memberProblems" (problemItems model MemberForm)
      // The activity index: derived, kept with every change, rebuilt from
      // the records on request (40).
      flag "canRebuildIndex" (model.Identity.Mode = SignedInMode && permits model Access.ManageOrganizationSettings)
      text "indexStatus" model.Store.Index
      flag "hasIndexProblems" (model.Problems.ContainsKey IndexForm)
      items "indexProblems" (problemItems model IndexForm) ]

/// What the session's person may do in the organization (3).
let private accessView (model: Model) =
    let held = Access.capabilitiesOf model.Roster model.Session.ActorId

    [ text "accessOrganization" model.Roster.OrganizationId
      items
          "accessCapabilities"
          [ for capability in Access.allCapabilities do
                if held.Contains capability then
                    let name = Access.capabilityName capability
                    [ t "id" name; t "name" (capabilityText name) ] ]
      flag "hasPeriodProblems" (model.Problems.ContainsKey PeriodForm)
      items "periodProblems" (problemItems model PeriodForm) ]

let project (model: Model) : View =
    // Without ViewOwnTime nothing of the person's time is shown.
    let model =
        if permits model Access.ViewOwnTime then
            model
        else
            { model with
                Ledger = Ledger.empty
                Attestations = []
                Timer = Timer.Idle
                Stopped = None }

    let tone, headline, detail = storeLines model

    let needsReferences =
        (Reference.selectable Reference.Project model.References).IsEmpty
        || (Reference.selectable Reference.ActivityType model.References).IsEmpty

    [ flag "screenToday" (shows model (function Places.Today | Places.Day _ -> true | _ -> false))
      flag "screenTrack" (shows model ((=) Places.Track))
      flag "screenMore" (shows model (function Places.Settings _ -> true | _ -> false))
      flag "screenActivity" (shows model (function Places.Entry _ -> true | _ -> false))
      yield! problemView model
      yield! linkView model
      yield! weekView model
      yield! periodPage model
      yield! projectsView model
      yield! projectView model
      items "navigation" (navigation model)
      text "announcement" model.Announcement
      yield! identityView model
      yield! accessView model
      yield! membershipView model
      text "sessionName" model.Session.DisplayName
      text "sessionInitials" (Format.initials model.Session.DisplayName)
      text "storeTone" tone
      text "storeHeadline" headline
      text "storeDetail" detail
      flag "queueElsewhere" (match model.Store.Sync.Holder with HeldElsewhere _ -> true | _ -> false)
      flag "queueWaiting" (model.Store.Sync.Holder = HeldElsewhere true)
      text "queueMode" (queueMode model.Store.Sync)
      text "todayLong" (Format.longDate (Model.today model))
      flag "needsReferences" needsReferences
      yield! today model
      yield! timer model
      yield! completion model
      yield! manual model
      yield! more model
      yield! conflictView model
      flag "hasObligations" (not (obligations model).IsEmpty)
      items "obligations" (obligations model)
      yield! detailView model
      yield! reviewView model
      yield! monthView model
      yield! periodView model
      yield! reportView model ]
