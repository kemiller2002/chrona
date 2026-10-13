/// Recording time: the timer (10), manual entry, reference data (4), and
/// quick entry from recent combinations and common durations (33, WI-0062).
module Chrona.Engine.App.UpdateRecording

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update
open Chrona.Engine.App.UpdateCommands

let internal startTimer (ctx: Ctx) (model: Model) =
    let draft = toClassification model.TimerDraft

    let problems =
        [ if draft.ActivityTypeId = "" then MissingField "activityType"
          if draft.ProjectId = "" then MissingField "project"
          yield! Reference.assignmentProblems model.References [] { draft with Tags = [] } ]

    match problems, zoneOrProblem model with
    | (_ :: _), _ -> withProblems TimerForm problems model, []
    | [], Error zoneProblems -> withProblems TimerForm zoneProblems model, []
    | [], Ok zone ->
        match Timer.start (ctx.NewId "TMR") model.Session.OrganizationId model.Session.ActorId ThisDevice zone (Some draft) ctx.Now model.Timer with
        | Error problem -> withProblems TimerForm [ problem ] model, []
        | Ok timer ->
            let generation = model.TickGeneration + 1

            { clear TimerForm model with
                Timer = timer
                TickGeneration = generation
                Announcement = "Timer started." },
            [ Wake(generation, TickMs) ]

let internal pauseTimer (ctx: Ctx) (model: Model) =
    match Timer.pause ctx.Now model.Timer with
    | Error problem -> withProblems TimerForm [ problem ] model, []
    | Ok timer -> { clear TimerForm model with Timer = timer; Announcement = "Timer paused." }, []

let internal resumeTimer (ctx: Ctx) (model: Model) =
    match Timer.resume ctx.Now model.Timer with
    | Error problem -> withProblems TimerForm [ problem ] model, []
    | Ok timer ->
        let generation = model.TickGeneration + 1

        { clear TimerForm model with
            Timer = timer
            TickGeneration = generation
            Announcement = "Timer resumed." },
        [ Wake(generation, TickMs) ]

let internal stopTimer (ctx: Ctx) (model: Model) =
    match zoneOrProblem model with
    | Error problems -> withProblems TimerForm problems model, []
    | Ok zone ->
        match Timer.stop zone ctx.Now model.Timer with
        | Error problem -> withProblems TimerForm [ problem ] model, []
        | Ok(idle, stopped) when stopped.Pieces.IsEmpty ->
            { clear TimerForm model with
                Timer = idle
                Announcement = "Timer stopped after less than thirty seconds. Nothing was recorded." },
            []
        | Ok(idle, stopped) ->
            { clear TimerForm model with
                Timer = idle
                Stopped = Some stopped
                LongTimerConfirmed = false
                CompletionDraft = stopped.Timer.Classification |> Option.map ofClassification |> Option.defaultValue emptyClassification
                TimerDraft = emptyClassification
                Announcement = $"Timer stopped at {Format.clock (occurrence zone ctx.Now).LocalTime}: {Format.minutes stopped.TotalMinutes} held. Complete the record to save it." },
            []

let internal saveCompletion (ctx: Ctx) (model: Model) =
    match model.Stopped, zoneOrProblem model with
    | None, _ -> model, []
    | _, Error problems -> withProblems CompletionForm problems model, []
    | Some stopped, Ok zone ->
        let classification = toClassification model.CompletionDraft

        let reviewed =
            { stopped with
                Timer = { stopped.Timer with Classification = Some classification }
                NeedsReview = if model.LongTimerConfirmed then None else stopped.NeedsReview }

        let result =
            Timer.toActivities ctx.Now (fun i -> ctx.NewId $"ACT{i}") Billable reviewed
            |> Result.bind (recordAll model (commandContext ctx model zone) model.Ledger)

        match result with
        | Error problems -> withProblems CompletionForm (List.distinct problems) model, []
        | Ok ledger ->
            let saved = changed model.Ledger ledger

            { clear CompletionForm model with
                Ledger = ledger
                Stopped = None
                LongTimerConfirmed = false
                CompletionDraft = emptyClassification
                Announcement = $"Saved {Format.minutes stopped.TotalMinutes} of {classification.Description}." }
            |> commit ctx saved []

// ---- manual entry --------------------------------------------------------------

let internal saveManual (ctx: Ctx) (model: Model) =
    let draft = model.Manual
    let startDate, endDate = Format.parseIsoDate draft.StartDate, Format.parseIsoDate draft.EndDate
    let startTime, endTime = Format.parseTime draft.StartTime, Format.parseTime draft.EndTime

    let timing =
        match startDate, startTime, endDate, endTime with
        | Some sd, Some st, Some ed, Some et when sd = ed -> Ok(ManualEntry.StartAndEnd(sd, st, et, None, None))
        | Some _, Some _, Some _, Some _ -> Error [ CrossesBusinessDay ]
        | _ ->
            Error
                [ if startDate.IsNone || startTime.IsNone then MissingField "start"
                  if endDate.IsNone || endTime.IsNone then MissingField "end" ]

    let classification = toClassification draft.Classification

    let result =
        match timing, zoneOrProblem model with
        | Error problems, _ -> Error(problems @ classificationProblems classification)
        | _, Error problems -> Error problems
        | Ok timing, Ok zone ->
            let entry: ManualEntry.ManualEntry =
                { ActivityId = ctx.NewId "ACT"
                  OrganizationId = model.Session.OrganizationId
                  ActorId = model.Session.ActorId
                  Zone = zone
                  Timing = timing
                  Classification = classification
                  Billability = Billable
                  BillingReference = noBillingReference
                  Reason = Some draft.Reason
                  WorkItemRef = None
                  Evidence = [] }

            let entryContext: ManualEntry.EntryContext =
                { Now = ctx.Now
                  HistoricalAfterDays = model.HistoricalAfterDays
                  References = model.References }

            let existing = model.Ledger.Activities |> Map.toList |> List.map snd

            ManualEntry.create entryContext existing entry
            |> Result.bind (fun activity ->
                execute model (commandContext ctx model zone) model.Ledger (Ledger.Record activity)
                |> Result.map (fun ledger -> activity, ledger))

    match result with
    | Error problems -> withProblems ManualForm (List.distinct problems) model, []
    | Ok(activity, ledger) ->
        { clear ManualForm model with
            Ledger = ledger
            Manual = emptyManual
            Announcement = $"Saved {Format.minutes activity.Minutes} on {Format.longDate activity.Occurrence.LocalDate}." }
        |> commit ctx [ activity ] []

// ---- reference data --------------------------------------------------------------

let internal kindOf =
    function
    | "project" -> Some Reference.Project
    | "activityType" -> Some Reference.ActivityType
    | "tag" -> Some Reference.Tag
    | _ -> None

let internal prefixOf =
    function
    | Reference.Project -> "PRJ"
    | Reference.ActivityType -> "TYP"
    | Reference.Tag -> "TAG"
    | Reference.Client -> "CLI"
    | Reference.Engagement -> "ENG"

let internal referenceCommand (ctx: Ctx) (command: Reference.Command) (key: Reference.Kind * string) (model: Model) =
    match Reference.execute model.References command with
    | Error problems -> withProblems ReferenceForm problems model, []
    | Ok catalogue -> commit ctx [] [ catalogue.Items[key] ] { clear ReferenceForm model with References = catalogue }

let internal addReference (ctx: Ctx) (kind: Reference.Kind) (model: Model) =
    let name = model.NewNames.TryFind kind |> Option.defaultValue ""
    let id = ctx.NewId(prefixOf kind)

    match Reference.execute model.References (Reference.Add(kind, id, name)) with
    | Error problems -> withProblems ReferenceForm problems model, []
    | Ok catalogue ->
        { clear ReferenceForm model with
            References = catalogue
            NewNames = model.NewNames.Remove kind
            Announcement = $"Added {name.Trim()}." }
        |> commit ctx [] [ catalogue.Items[(kind, id)] ]

/// `key` is `<kind>:<id>`, as the page's item key.
let internal setActive (ctx: Ctx) (key: string) (active: bool) (model: Model) =
    match key.Split(':', 2) with
    | [| kind; id |] ->
        match kindOf kind |> Option.bind (fun k -> model.References.Items.TryFind(k, id) |> Option.map (fun item -> k, item)) with
        | None -> withProblems ReferenceForm [ UnknownReference(kind, id) ] model, []
        | Some(k, item) ->
            let command =
                if active then Reference.Reactivate(k, id, item.Revision) else Reference.Archive(k, id, item.Revision)

            referenceCommand ctx command (k, id) model
    | _ -> invalidArg (nameof key) $"Malformed reference key: {key}"

/// The person's most recent combinations of activity type, project and
/// description, newest first, while their reference data is still offered.
let recentCombinations (model: Model) : Classification list =
    let active kind id =
        Reference.selectable kind model.References |> List.exists (fun item -> item.Id = id)

    model.Ledger.Activities
    |> Map.toList
    |> List.map snd
    |> List.filter (fun a -> a.ActorId = model.Session.ActorId && consumesTime a)
    |> List.sortByDescending (fun a -> a.Occurrence.LocalDate, a.Occurrence.LocalTime, a.LastChangedAt)
    |> List.map _.Classification
    |> List.filter (fun c -> active Reference.ActivityType c.ActivityTypeId && active Reference.Project c.ProjectId)
    |> List.distinctBy (fun c -> c.ActivityTypeId, c.ProjectId, c.Description)
    |> List.truncate 5

/// The common durations the manual form offers (the deployment's, or the default).
let quickDurations (model: Model) =
    model.Deployment |> Option.map _.QuickDurations |> Option.defaultValue Deployment.defaultQuickDurations

let internal recentAt (key: string option) (model: Model) =
    match key |> Option.map Int32.TryParse with
    | Some(true, index) -> recentCombinations model |> List.tryItem index
    | _ -> None

/// A recent combination fills the timer's draft.
let internal timerUseRecent (key: string option) (model: Model) =
    match recentAt key model with
    | Some c ->
        { model with
            TimerDraft =
                { model.TimerDraft with
                    ActivityTypeId = c.ActivityTypeId
                    ProjectId = c.ProjectId
                    Description = c.Description } },
        []
    | None -> model, []

/// A recent combination fills the manual entry's classification; its time
/// is left exactly as typed.
let internal manualUseRecent (key: string option) (model: Model) =
    match recentAt key model with
    | Some c -> manual (fun d -> { d with Classification = ofClassification c }) model
    | None -> model, []

/// A common duration sets the end from the start, visibly: the end time is
/// written into its field and said, never changed silently afterwards.
let internal manualDuration (key: string option) (model: Model) =
    let draft = model.Manual

    match key |> Option.map Int32.TryParse, Format.parseIsoDate draft.StartDate, Format.parseTime draft.StartTime with
    | Some(true, minutes), Some date, Some startTime when List.contains minutes (quickDurations model) ->
        let finish = date.ToDateTime(startTime).AddMinutes(float minutes)

        if DateOnly.FromDateTime finish <> date then
            withProblems ManualForm [ CrossesBusinessDay ] model, []
        else
            let endTime = TimeOnly.FromDateTime finish

            { model with
                Manual =
                    { draft with
                        EndDate = draft.StartDate
                        EndTime = endTime.ToString("HH:mm", Globalization.CultureInfo.InvariantCulture) }
                Announcement = $"End set to {Format.clock endTime}, {Format.minutes minutes} after the start." },
            []
    | Some(true, _), _, _ -> withProblems ManualForm [ MissingField "start" ] model, []
    | _ -> model, []

/// An earlier entry's classification copied into a new manual entry for
/// today; the new entry's time is the person's to set.
let internal copyActivity (activityId: string) (model: Model) =
    match model.Ledger.Activities.TryFind activityId with
    | Some activity ->
        let today = Format.isoDate (Model.today model)

        { model with
            Manual =
                { emptyManual with
                    Classification = ofClassification activity.Classification
                    StartDate = today
                    EndDate = today }
            Announcement = $"Copied {activity.Classification.Description} into a new entry. Set its time, then save it." }
        |> navigate Places.Track
    | None -> model, []
