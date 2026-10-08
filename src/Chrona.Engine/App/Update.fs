/// Every state change of the Chrona application: `update ctx msg model`
/// returns the next model and the effects it wants, as data. Pure: the
/// current instant and fresh ids arrive in `Ctx`; nothing here reads a clock,
/// touches the browser or stores anything. Every rule is the domain's
/// (`Chrona.Domain`); this module only routes intent to it.
module Chrona.Engine.App.Update

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Engine.App.Model

/// What the edge supplies with each message.
[<NoComparison; NoEquality>]
type Ctx =
    { Now: DateTimeOffset
      /// A fresh, unique id with the given prefix, e.g. `NewId "ACT"`.
      NewId: string -> string }

type Msg =
    | Started of hash: string
    | EnvironmentDescribed of timeZone: string
    | EnvironmentUnavailable
    | LocationMoved of hash: string
    | Ticked of generation: int
    | StoreAnswered of commitId: string * StoreOutcome
    /// A page event: its name, the enclosing item's key, the control's value
    /// and, for a checkbox, whether it is checked.
    | Ui of name: string * key: string option * value: string * isChecked: bool option

/// What the engine asks the edge to do.
type Effect =
    | Navigate of hash: string
    /// Wake the engine after `afterMs` milliseconds, for this timer run.
    | Wake of generation: int * afterMs: int
    | DescribeEnvironment
    | Store of StoreRequest

/// The timer display refreshes once a second while running (the label is
/// whole seconds); the engine computes it, never a client-side counter.
[<Literal>]
let TickMs = 1000

/// The device a timer was started on. One browser is one device until
/// sign-in gives devices identities (10.5).
[<Literal>]
let ThisDevice = "this-browser"

/// Every `data-event` name the page may send; anything else is a defect.
let eventNames =
    [ "navigate"; "goToday"; "goTrack"; "goMore"; "showDate"; "previousDay"; "nextDay"
      "timerActivityType"; "timerProject"; "timerDescription"; "startTimer"; "pauseTimer"; "resumeTimer"; "stopTimer"
      "completeActivityType"; "completeProject"; "completeDescription"; "completePurpose"; "completeTag"; "confirmLongTimer"; "saveCompletion"
      "manualActivityType"; "manualProject"; "manualStartDate"; "manualStartTime"; "manualEndDate"; "manualEndTime"
      "manualDescription"; "manualPurpose"; "manualReason"; "manualTag"; "saveManual"
      "newProjectName"; "newActivityTypeName"; "newTagName"; "addProject"; "addActivityType"; "addTag"; "referenceActive" ]

// ---- helpers ----------------------------------------------------------------

let private withProblems (form: Form) (problems: Diagnostic list) (model: Model) =
    { model with Problems = if problems.IsEmpty then model.Problems.Remove form else model.Problems.Add(form, problems) }

let private clear (form: Form) (model: Model) = withProblems form [] model

let private commandContext (ctx: Ctx) (model: Model) (zone: Zone) : Ledger.CommandContext =
    { Performer = model.Session.ActorId
      At = ctx.Now
      Source = "chrona-web"
      Zone = zone
      References = model.References
      CorrelationId = None }

/// Sends what became authoritative to the store.
let private commit (ctx: Ctx) (activities: Activity list) (references: Reference.Item list) (model: Model) =
    let id = ctx.NewId "COMMIT"

    { model with Store = { model.Store with Pending = model.Store.Pending @ [ id ] } },
    [ Store { CommitId = id; Activities = activities; References = references } ]

let private toClassification (draft: ClassificationDraft) : Classification =
    { ProjectId = draft.ProjectId.Trim()
      ClientId = None
      EngagementId = None
      ActivityTypeId = draft.ActivityTypeId.Trim()
      Tags = draft.Tags
      Description = draft.Description.Trim()
      BusinessPurpose = draft.BusinessPurpose.Trim() }

let private ofClassification (c: Classification) : ClassificationDraft =
    { ActivityTypeId = c.ActivityTypeId
      ProjectId = c.ProjectId
      Description = c.Description
      BusinessPurpose = c.BusinessPurpose
      Tags = c.Tags }

let private toggle (item: string) (on: bool) (items: string list) =
    if on then (if List.contains item items then items else items @ [ item ]) else items |> List.filter ((<>) item)

/// Records activities one after another, all or nothing: the ledger only
/// changes if every one is accepted.
let private recordAll (context: Ledger.CommandContext) (ledger: Ledger.Ledger) (activities: Activity list) =
    activities
    |> List.fold
        (fun state activity -> state |> Result.bind (fun l -> Ledger.execute context l (Ledger.Record activity)))
        (Ok ledger)

let private zoneOrProblem (model: Model) =
    match model.Zone with
    | Some zone -> Ok zone
    | None -> Error [ InvalidTimeZone "unknown" ]

let private changed (before: Ledger.Ledger) (after: Ledger.Ledger) =
    after.Activities
    |> Map.toList
    |> List.filter (fun (id, a) -> before.Activities.TryFind id <> Some a)
    |> List.map snd

// ---- the timer ---------------------------------------------------------------

let private startTimer (ctx: Ctx) (model: Model) =
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

let private pauseTimer (ctx: Ctx) (model: Model) =
    match Timer.pause ctx.Now model.Timer with
    | Error problem -> withProblems TimerForm [ problem ] model, []
    | Ok timer -> { clear TimerForm model with Timer = timer; Announcement = "Timer paused." }, []

let private resumeTimer (ctx: Ctx) (model: Model) =
    match Timer.resume ctx.Now model.Timer with
    | Error problem -> withProblems TimerForm [ problem ] model, []
    | Ok timer ->
        let generation = model.TickGeneration + 1

        { clear TimerForm model with
            Timer = timer
            TickGeneration = generation
            Announcement = "Timer resumed." },
        [ Wake(generation, TickMs) ]

let private stopTimer (ctx: Ctx) (model: Model) =
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

let private saveCompletion (ctx: Ctx) (model: Model) =
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
            |> Result.bind (recordAll (commandContext ctx model zone) model.Ledger)

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

let private saveManual (ctx: Ctx) (model: Model) =
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
                Ledger.execute (commandContext ctx model zone) model.Ledger (Ledger.Record activity)
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

let private kindOf =
    function
    | "project" -> Some Reference.Project
    | "activityType" -> Some Reference.ActivityType
    | "tag" -> Some Reference.Tag
    | _ -> None

let private prefixOf =
    function
    | Reference.Project -> "PRJ"
    | Reference.ActivityType -> "TYP"
    | Reference.Tag -> "TAG"
    | Reference.Client -> "CLI"
    | Reference.Engagement -> "ENG"

let private referenceCommand (ctx: Ctx) (command: Reference.Command) (key: Reference.Kind * string) (model: Model) =
    match Reference.execute model.References command with
    | Error problems -> withProblems ReferenceForm problems model, []
    | Ok catalogue -> commit ctx [] [ catalogue.Items[key] ] { clear ReferenceForm model with References = catalogue }

let private addReference (ctx: Ctx) (kind: Reference.Kind) (model: Model) =
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
let private setActive (ctx: Ctx) (key: string) (active: bool) (model: Model) =
    match key.Split(':', 2) with
    | [| kind; id |] ->
        match kindOf kind |> Option.bind (fun k -> model.References.Items.TryFind(k, id) |> Option.map (fun item -> k, item)) with
        | None -> withProblems ReferenceForm [ UnknownReference(kind, id) ] model, []
        | Some(k, item) ->
            let command =
                if active then Reference.Reactivate(k, id, item.Revision) else Reference.Archive(k, id, item.Revision)

            referenceCommand ctx command (k, id) model
    | _ -> invalidArg (nameof key) $"Malformed reference key: {key}"

// ---- the dispatcher -----------------------------------------------------------

let private draft (f: ClassificationDraft -> ClassificationDraft) (field: Model -> ClassificationDraft) (set: Model -> ClassificationDraft -> Model) (model: Model) =
    set model (f (field model)), []

let private timerDraft f = draft f _.TimerDraft (fun m d -> { m with TimerDraft = d })
let private completionDraft f = draft f _.CompletionDraft (fun m d -> { m with CompletionDraft = d })

let private manualDraft f = draft f _.Manual.Classification (fun m d -> { m with Manual = { m.Manual with Classification = d } })

let private manual (f: ManualDraft -> ManualDraft) (model: Model) = { model with Manual = f model.Manual }, []

let private navigate (route: Route) (model: Model) = model, [ Navigate(Routes.hash route) ]

let private onEvent (ctx: Ctx) (name: string) (key: string option) (value: string) (isChecked: bool option) (model: Model) =
    let checkedOn = isChecked |> Option.defaultValue false

    match name with
    | "navigate" ->
        match Routes.ofScreenName (defaultArg key value) with
        | Some screen -> navigate { Screen = screen; Date = None } model
        | None -> invalidArg (nameof value) $"Unknown screen: {value}"
    | "goToday" -> navigate { Screen = Today; Date = None } model
    | "goTrack" -> navigate { Screen = Track; Date = None } model
    | "goMore" -> navigate { Screen = More; Date = None } model
    | "showDate" ->
        match Format.parseIsoDate value with
        | Some date -> navigate { Screen = Today; Date = Some date } model
        | None -> model, []
    | "previousDay" -> navigate { Screen = Today; Date = Some((selectedDate model).AddDays -1) } model
    | "nextDay" -> navigate { Screen = Today; Date = Some((selectedDate model).AddDays 1) } model

    | "timerActivityType" -> timerDraft (fun d -> { d with ActivityTypeId = value }) model
    | "timerProject" -> timerDraft (fun d -> { d with ProjectId = value }) model
    | "timerDescription" -> timerDraft (fun d -> { d with Description = value }) model
    | "startTimer" -> startTimer ctx model
    | "pauseTimer" -> pauseTimer ctx model
    | "resumeTimer" -> resumeTimer ctx model
    | "stopTimer" -> stopTimer ctx model

    | "completeActivityType" -> completionDraft (fun d -> { d with ActivityTypeId = value }) model
    | "completeProject" -> completionDraft (fun d -> { d with ProjectId = value }) model
    | "completeDescription" -> completionDraft (fun d -> { d with Description = value }) model
    | "completePurpose" -> completionDraft (fun d -> { d with BusinessPurpose = value }) model
    | "completeTag" -> completionDraft (fun d -> { d with Tags = toggle (defaultArg key "") checkedOn d.Tags }) model
    | "confirmLongTimer" -> { model with LongTimerConfirmed = checkedOn }, []
    | "saveCompletion" -> saveCompletion ctx model

    | "manualActivityType" -> manualDraft (fun d -> { d with ActivityTypeId = value }) model
    | "manualProject" -> manualDraft (fun d -> { d with ProjectId = value }) model
    | "manualDescription" -> manualDraft (fun d -> { d with Description = value }) model
    | "manualPurpose" -> manualDraft (fun d -> { d with BusinessPurpose = value }) model
    | "manualTag" -> manualDraft (fun d -> { d with Tags = toggle (defaultArg key "") checkedOn d.Tags }) model
    | "manualStartDate" -> manual (fun d -> { d with StartDate = value; EndDate = if d.EndDate = "" then value else d.EndDate }) model
    | "manualStartTime" -> manual (fun d -> { d with StartTime = value }) model
    | "manualEndDate" -> manual (fun d -> { d with EndDate = value }) model
    | "manualEndTime" -> manual (fun d -> { d with EndTime = value }) model
    | "manualReason" -> manual (fun d -> { d with Reason = value }) model
    | "saveManual" -> saveManual ctx model

    | "newProjectName" -> { model with NewNames = model.NewNames.Add(Reference.Project, value) }, []
    | "newActivityTypeName" -> { model with NewNames = model.NewNames.Add(Reference.ActivityType, value) }, []
    | "newTagName" -> { model with NewNames = model.NewNames.Add(Reference.Tag, value) }, []
    | "addProject" -> addReference ctx Reference.Project model
    | "addActivityType" -> addReference ctx Reference.ActivityType model
    | "addTag" -> addReference ctx Reference.Tag model
    | "referenceActive" -> setActive ctx (defaultArg key "") checkedOn model

    // The page and the engine disagree: a defect, not an operational failure.
    | other -> invalidArg (nameof name) $"Unknown event name: {other}"

let update (ctx: Ctx) (msg: Msg) (model: Model) : Model * Effect list =
    // An announcement is spoken once: a person's next action replaces it;
    // wake-ups and store answers leave it alone.
    let model =
        match msg with
        | Ui _ -> { model with Now = ctx.Now; Announcement = "" }
        | _ -> { model with Now = ctx.Now }

    match msg with
    | Started hash -> { model with Route = Routes.parse hash }, [ DescribeEnvironment ]
    | EnvironmentDescribed timeZone ->
        // An unknown zone falls back to UTC, visibly: the More screen says
        // which zone is in use.
        let zone = tryZone timeZone |> Result.toOption |> Option.orElse (tryZone "UTC" |> Result.toOption)
        { model with Zone = zone }, []
    | EnvironmentUnavailable -> { model with Zone = tryZone "UTC" |> Result.toOption }, []
    | LocationMoved hash -> { model with Route = Routes.parse hash }, []
    | Ticked generation ->
        match model.Timer with
        | Timer.Running _ when generation = model.TickGeneration -> model, [ Wake(generation, TickMs) ]
        | _ -> model, []
    | StoreAnswered(commitId, outcome) ->
        let pending = model.Store.Pending |> List.filter ((<>) commitId)

        let store =
            match outcome with
            | Committed ->
                { model.Store with
                    Pending = pending
                    Committed = model.Store.Committed + 1
                    Problem = None }
            | problem -> { model.Store with Pending = pending; Problem = Some problem }

        { model with Store = store }, []
    | Ui(name, key, value, isChecked) -> onEvent ctx name key value isChecked model
