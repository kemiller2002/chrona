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

/// Why text goes to the clipboard.
type CopyPurpose =
    /// A report's export.
    | ExportCopy
    /// A link to the current view (CHX-460).
    | LinkCopy

type Msg =
    /// The page started: its own address, the fragment it was opened at, and
    /// the query it was opened with (the provider's sign-in callback carries
    /// `code` and `state` there).
    | Started of page: PageAddress * fragment: string * query: (string * string) list
    /// The return target this tab kept across a sign-in round trip, if any.
    | ReturnRead of target: string option
    /// The deployment's configuration document, or None when it could not be read.
    | ConfigurationRead of text: string option
    | IdentityChanged of IdentityChange
    /// The organization's records, read from the store.
    | StoreOpened of StoreContents
    /// The organization's records could not be read, and why.
    | StoreUnavailable of reason: string
    /// The organization has no listed administrator: why, and whether this
    /// person may confirm themselves as one.
    | StoreNeedsConfirmation of reason: string * canConfirm: bool
    | EnvironmentDescribed of timeZone: string
    | EnvironmentUnavailable
    /// The browser moved to another address (Back, Forward, a link).
    | LocationMoved of fragment: string
    | Ticked of generation: int
    | StoreAnswered of commitId: string * StoreOutcome
    /// Changes queued in this browser before the page opened, still unsent,
    /// sent now in order (WI-0033). Each is answered like any other.
    | StoreResumed of StoreRequest list
    /// Where the unsent changes stand now.
    | SyncChanged of SyncState
    /// The activity index was rebuilt from the records, or could not be: what
    /// happened, for the person.
    | IndexRebuilt of summary: string
    /// A month asked for could not be read, and why.
    | StoreReadFailed of reason: string
    /// This account's unsent changes were discarded from this device: how many.
    | UnsentDiscarded of count: int
    /// Another tab took this browser's unsent changes over ("use this tab
    /// instead" there): these, kept before it did, are now that tab's to
    /// send (WI-0059, LCP-060).
    | UnsentHandedOver of commitIds: string list
    /// Where reading producers' inboxes stands (WI-0038).
    | InboxReconciled of InboxStatus
    /// Chrona's data was cleared from this browser, or why it was not.
    | DeviceCleared of Result<unit, string>
    /// The device's kept timer under this key, if any (WI-0055).
    | TimerLoaded of key: string * value: string option
    /// The records could not be opened; this many of this account's changes
    /// wait in this browser to be sent.
    | UnsentWaiting of count: int * notice: string option
    /// The build this page runs (WI-0063).
    | BuildKnown of build: string
    /// The page is back after being hidden, frozen or restored from the
    /// back/forward cache: it catches up.
    | PageReturned
    /// The browser says it has, or has no, network.
    | ConnectionChanged of online: bool
    /// The build the deployment serves now, if it could be read.
    | ShellChecked of build: string option
    /// The activity index changed with a commit: each person's months, and
    /// what it covers.
    | IndexChanged of history: ActivityIndex.MonthTotal list * summary: string
    /// Whether the browser took the text onto its clipboard.
    | Copied of purpose: CopyPurpose * succeeded: bool
    /// A page event: its name, the enclosing item's key, the control's value
    /// and, for a checkbox, whether it is checked.
    | Ui of name: string * key: string option * value: string * isChecked: bool option

/// What signing out did with this account's unsent work (WI-0058). What the
/// device keeps of the account follows it (WI-0057, LCP-070, LCP-086).
type UnsentChoice =
    | NothingUnsent
    | SentUnsent
    | KeptUnsent
    | DiscardedUnsent

/// What the engine asks the edge to do.
type Effect =
    /// Move the browser's address: a push for going somewhere, a replace for
    /// refining the view or correcting an address to its canonical form.
    | Navigate of Limen.Routing.NavigationEffect
    /// Keep (or, None, forget) the address to return to after sign-in in this
    /// tab's session storage: GitHub's callback carries no fragment.
    | KeepReturn of target: string option
    /// Read the return target this tab kept, after a sign-in callback.
    | ReadReturn
    /// Wake the engine after `afterMs` milliseconds, for this timer run.
    | Wake of generation: int * afterMs: int
    | DescribeEnvironment
    | Store of StoreRequest
    /// Put text on the clipboard.
    | CopyText of purpose: CopyPurpose * text: string
    /// Open the browser's print dialog for the page's printable document.
    | Print
    /// Read the deployment's configuration document.
    | ReadConfiguration
    /// Set up sign-in from the deployment's configuration: complete the
    /// provider's callback when the page was opened with one, otherwise
    /// restore a session kept in this tab.
    | BeginIdentity of Deployment.IdentityConfig * callback: (string * string) list
    /// Send the person to the provider to sign in.
    | SignIn of Retention
    /// Clear every token this tab holds and revoke it at the provider.
    | SignOut
    /// The account leaves this device: what the device keeps of it (its
    /// read cache) goes, unless its unsent work was kept here, under the
    /// deployment's shared-device policy (WI-0057). `unsent`: how much work
    /// was unsent when the person chose.
    | LeaveDevice of UnsentChoice * unsent: int
    /// Clear Chrona's data from this browser, for every account: the queue
    /// and the read cache (LCP-070, LCP-086).
    | ClearDevice
    /// Open the organization's records at the deployment's location, as this
    /// session, reading what these dates need.
    | OpenStore of Deployment.DeploymentConfig * Session * dates: DateOnly list
    /// Make this listed person the organization's administrator, then open it.
    | ConfirmAdministrator
    /// Read the month folders these dates need (a month not read yet).
    | ReadMonths of dates: DateOnly list
    /// Rebuild the activity index from the stored records (40).
    | RebuildIndex
    /// Read the producers' inboxes now (WI-0038).
    | ReadInboxes
    /// Send this account's unsent changes now.
    | SendUnsent
    /// Move focus to the control with this id: the control the person used
    /// is gone from the page, and focus must not fall to the document (35).
    | FocusControl of id: string
    /// Take this browser's unsent changes over from the tab holding them
    /// ("use this tab instead"; WI-0067, WI-0059).
    | TakeOverQueue
    /// Send the unsent changes from an earlier version as this person's.
    | SendEarlier
    /// Discard the unsent changes from an earlier version.
    | DiscardEarlier
    /// Ask again to hold this browser's unsent changes: the tab holding them
    /// may have closed (WI-0059).
    | ClaimQueue
    /// Discard this account's unsent changes from this device.
    | DiscardUnsent
    /// Read the device's kept timer for this person (WI-0055).
    | LoadTimer of key: string
    /// Keep the device's timer for this person, or clear it (None).
    | SaveTimer of key: string * value: string option
    /// Ask which Chrona build the deployment serves now (WI-0063).
    | CheckShell
    /// Load the page again, to run the newer Chrona.
    | ReloadPage

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
    [ "signIn"; "signInRetention"; "signOut"; "retryStore"; "chooseOrganization"; "confirmAdministrator"; "reloadShell"
      "signOutSend"; "signOutKeep"; "signOutDiscard"; "signOutDiscardConfirmed"; "signOutCancel"; "takeOverQueue"
      "sendEarlier"; "keepEarlier"; "discardEarlier"; "discardEarlierConfirmed"; "discardEarlierCancel"
      "clearDevice"; "clearDeviceConfirmed"; "clearDeviceCancel"
      "memberId"; "memberName"; "memberAccess"; "admitMember"; "changeMemberAccess"; "removeMember"
      "keepStored"; "retryChange"; "redoChange"; "acceptOutsideEdit"; "rebuildIndex"
      "goToday"; "goTrack"; "goMore"; "showDate"; "previousDay"; "nextDay"
      "timerActivityType"; "timerProject"; "timerDescription"; "startTimer"; "pauseTimer"; "resumeTimer"; "stopTimer"
      "completeActivityType"; "completeProject"; "completeDescription"; "completePurpose"; "completeTag"; "confirmLongTimer"; "saveCompletion"
      "manualActivityType"; "manualProject"; "manualStartDate"; "manualStartTime"; "manualEndDate"; "manualEndTime"
      "manualDescription"; "manualPurpose"; "manualReason"; "manualTag"; "saveManual"
      "newProjectName"; "newActivityTypeName"; "newTagName"; "addProject"; "addActivityType"; "addTag"; "referenceActive"
      "openReview"; "previousMonth"; "nextMonth"; "showMonth"
      "timerUseRecent"; "manualUseRecent"; "manualDuration"; "copyActivity"
      "amendActivityType"; "amendProject"; "amendDescription"; "amendPurpose"; "amendReason"; "saveAmend"
      "voidReason"; "voidActivity"; "restoreActivity"
      "splitFirst"; "splitSecond"; "splitEvidence"; "saveSplit"
      "evidenceKind"; "evidenceUrl"; "evidenceLabel"; "evidenceSource"; "evidenceNotes"; "attachEvidence"; "unlinkEvidence"
      "mergeSelect"; "mergeActivityType"; "mergeProject"; "mergeDescription"; "mergePurpose"; "saveMerge"
      "attestStatement"; "attestDay"; "resolveObligation"; "periodCadence"; "periodWeekStart"
      "periodSubmissionExpected"; "periodApprovalRequired"; "savePeriodSettings"; "submitPeriod"; "reopenReason"; "reopenPeriod"
      "reviewNote"; "approveSubmission"; "rejectSubmission"
      "candidateActivityType"; "candidateProject"; "candidateDescription"; "candidatePurpose"; "candidateReason"
      "acceptCandidate"; "rejectCandidate"; "readInboxes"
      "reportFrom"; "reportTo"; "reportProject"; "reportActivityType"; "reportTag"; "reportMethod"; "reportBillability"; "reportText"
      "reportIncludeRemoved"; "reportGrouping"; "reportFormat"; "copyExport"; "printReport"; "reportMonth"; "goReports"
      "copyLink"; "skipToContent"; "dayProject"; "previousWeek"; "nextWeek"; "weekProject"; "previousPeriod"; "nextPeriod" ]

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

/// Sends a request to the store: it waits, pending, until answered.
let private sendRequest (request: StoreRequest) (model: Model) =
    { model with
        Store = { model.Store with Pending = model.Store.Pending @ [ request ] } },
    [ Store request ]

/// Sends what became authoritative to the store.
let private commitWith (ctx: Ctx) (activities: Activity list) (references: Reference.Item list) (attestations: Review.Attestation list) (model: Model) =
    // The command's audit entries go with its records (WI-0056).
    let entries = model.Ledger.Audit |> List.skip (min model.Store.Audited model.Ledger.Audit.Length)
    let known = activities @ (model.Ledger.Activities |> Map.toList |> List.map snd)

    let request =
        { emptyRequest (ctx.NewId "COMMIT") with
            Activities = activities
            References = references
            Attestations = attestations
            Audit = AuditRecord.place known entries }

    { model with
        Store =
            { model.Store with
                Pending = model.Store.Pending @ [ request ]
                Audited = model.Ledger.Audit.Length } },
    [ Store request ]

let private commit (ctx: Ctx) (activities: Activity list) (references: Reference.Item list) (model: Model) =
    commitWith ctx activities references [] model

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

let private zoneOrProblem (model: Model) =
    match model.Zone with
    | Some zone -> Ok zone
    | None -> Error [ InvalidTimeZone "unknown" ]

let private changed (before: Ledger.Ledger) (after: Ledger.Ledger) =
    after.Activities
    |> Map.toList
    |> List.filter (fun (id, a) -> before.Activities.TryFind id <> Some a)
    |> List.map snd

/// Runs a ledger command, refused when it changes time in a period that is
/// submitted or closed: the period is reopened first (WI-0036,
/// `CHRONA.REVIEW.SUBMITTED_PERIOD`).
let private execute (model: Model) (context: Ledger.CommandContext) (ledger: Ledger.Ledger) (command: Ledger.Command) =
    Ledger.execute context ledger command
    |> Result.bind (fun after ->
        let touched = PeriodReview.touchedBy ledger.Activities (changed ledger after)

        match PeriodReview.restrictions model.PeriodConfig model.Reviews touched with
        | [] -> Ok after
        | problems -> Error problems)

/// Records activities one after another, all or nothing: the ledger only
/// changes if every one is accepted.
let private recordAll (model: Model) (context: Ledger.CommandContext) (ledger: Ledger.Ledger) (activities: Activity list) =
    activities
    |> List.fold
        (fun state activity -> state |> Result.bind (fun l -> execute model context l (Ledger.Record activity)))
        (Ok ledger)


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

// ---- the activity detail: amend, void, restore, split, evidence ------------------

/// The detail drafts for an activity, at the revision it has now.
let openDetail (model: Model) (activityId: string) : Detail option =
    model.Ledger.Activities.TryFind activityId
    |> Option.map (fun a ->
        { ActivityId = activityId
          Revision = a.Revision
          Amend = ofClassification a.Classification
          AmendReason = ""
          VoidReason = ""
          SplitFirst = ""
          SplitSecond = ""
          SplitEvidence = a.Evidence |> List.map (fun e -> e.Id, 1) |> Map.ofList
          EvidenceKind = "url"
          EvidenceUrl = ""
          EvidenceLabel = ""
          EvidenceSource = ""
          EvidenceNotes = "" })

/// The report form for an address's filters: its dates as the form holds
/// them, an absent filter as "any".
let draftOf (query: Places.ReportQuery) (draft: ReportDraft) =
    let iso = Option.map Format.isoDate >> Option.defaultValue ""
    let text = Option.defaultValue ""

    { draft with
        From = iso query.From
        To = iso query.To
        ProjectId = text query.ProjectId
        ActivityTypeId = text query.ActivityTypeId
        Tag = text query.Tag
        Method = text query.Method
        Billability = text query.Billability
        Text = text query.Text
        IncludeRemoved = query.IncludeRemoved
        Grouping = query.Grouping
        Format = query.Format }

/// The address's filters for a report form.
let queryOf (draft: ReportDraft) : Places.ReportQuery =
    let some (value: string) = if value = "" then None else Some value

    { From = Format.parseIsoDate draft.From
      To = Format.parseIsoDate draft.To
      ProjectId = some draft.ProjectId
      ActivityTypeId = some draft.ActivityTypeId
      Tag = some draft.Tag
      Method = some draft.Method
      Billability = some draft.Billability
      Text = some draft.Text
      IncludeRemoved = draft.IncludeRemoved
      Grouping = draft.Grouping
      Format = draft.Format }

/// The draft a candidate's page opens with: what the observation proposes.
/// The activity type and business purpose are the person's to give.
let private openCandidate (id: string) (model: Model) =
    match model.Candidates |> List.tryFind (fun candidate -> candidate.CandidateId = id) with
    | Some candidate when model.CandidateDraftFor <> id ->
        { model with
            CandidateDraft =
                { emptyClassification with
                    ProjectId = candidate.Observation.ProjectId
                    Description = defaultArg candidate.Observation.Description "" }
            CandidateDraftFor = id
            CandidateReason = "" }
    | _ -> model

/// Keeps the drafts in step with the place: the activity screen's drafts
/// opened when it is entered and dropped when it is left, and the report
/// form set from the address's filters (its export generated afresh).
let private followRoute (model: Model) =
    // A link copied for the place left behind is not this place's.
    let model = { model with LinkStatus = ""; LinkText = "" }

    let model =
        match model.Place with
        | Places.Reports query -> { model with Report = { draftOf query model.Report with GeneratedAt = Some model.Now } }
        | _ -> model

    let model =
        match model.Place with
        | Places.Candidate id -> openCandidate id model
        | _ -> { model with CandidateDraftFor = "" }

    match model.Place, model.Detail with
    | Places.Entry(id, _), Some detail when detail.ActivityId = id -> model
    | Places.Entry(id, _), _ -> { model with Detail = openDetail model id }
    | _ -> { model with Detail = None }

/// A place in its canonical form: a week or period named by its first day
/// (the organization's week start and cadence decide which day that is).
let normalize (model: Model) (place: Places.Place) =
    match place with
    | Places.Week(on, project) -> Places.Week((weekOf model on).Start, project)
    | Places.Period on -> Places.Period (Periods.containing model.PeriodConfig on).Start
    | other -> other

/// The months a place shows stored time from. An activity named without its
/// day is looked for in every month that holds the person's time.
let private monthsNeeded (model: Model) =
    let monthOf (date: DateOnly) = date.Year, date.Month

    let between (first: DateOnly) (last: DateOnly) = [ monthOf first; monthOf last ]

    // Every month that holds the person's time, from the activity index.
    let history () =
        model.Store.History
        |> List.filter (fun total -> total.ActorId = model.Session.ActorId)
        |> List.map (fun total -> total.Year, total.Month)

    match model.Place with
    | Places.Day(on, _)
    | Places.Review on -> [ monthOf on ]
    | Places.Week(on, _) -> let week = weekOf model on in between week.Start week.Finish
    | Places.Period on -> let period = Periods.containing model.PeriodConfig on in between period.Start period.Finish
    | Places.Month(year, month) -> [ year, month ]
    | Places.Entry(id, _) when model.Ledger.Activities.ContainsKey id -> []
    | Places.Entry(_, Some on) -> [ monthOf on ]
    | Places.Entry(_, None)
    // A project's page shows all of the person's time on it.
    | Places.Project _ -> history ()
    // A candidate's day is read, so accepting it is checked against the
    // time already there; one not found yet may be decided in any month.
    | Places.Candidate id ->
        match model.Candidates |> List.tryFind (fun candidate -> candidate.CandidateId = id) with
        | Some candidate -> [ monthOf (observedDate model candidate.Observation) ]
        | None -> history ()
    | _ -> []

/// A month the person goes to that was not read yet is read now: one month
/// folder, on demand, never the whole history (38).
let private readNeeded (model: Model) =
    match model.Store.Kind with
    | Durable _ when canWork model ->
        let unread =
            monthsNeeded model
            |> List.distinct
            |> List.filter (fun month -> not (List.contains month model.Store.Months) && not (List.contains month model.Store.Reading))

        if unread.IsEmpty then
            model, []
        else
            { model with Store = { model.Store with Reading = model.Store.Reading @ unread } },
            [ ReadMonths(unread |> List.map (fun (year, month) -> DateOnly(year, month, 1))) ]
    | _ -> model, []

/// Moves to a place: the engine moves at once, and asks the browser to
/// follow (CHX-460). The place itself is settled from the new address after
/// every message (`settle`).
let private move operation (place: Places.Place) (model: Model) =
    match operation Places.codec model.Router (addressOf model place) with
    | Ok(router, effect) -> { model with Router = router }, effect |> Option.map Navigate |> Option.toList
    // Every place has an address (PlacesTests); one without is a defect.
    | Error error -> invalidOp $"No address for {place}: %A{error}"

/// Going somewhere else: a new history entry, unless it is already current.
let private navigate place model = move Limen.Routing.RouteCodec.navigate place model

/// Refining the current view (a filter, a date, a month): the current entry
/// is replaced, so Back steps to the previous place, not the previous filter.
let private refine place model = move Limen.Routing.RouteCodec.refine place model

let private detail (f: Detail -> Detail) (model: Model) =
    { model with Detail = model.Detail |> Option.map f }, []

/// Runs a ledger command for the detail screen: on success the ledger moves,
/// what changed is committed and the detail reopens at the new revision.
let private ledgerCommand (ctx: Ctx) (form: Form) (announcement: string) (command: Detail -> Ledger.Command) (model: Model) =
    match model.Detail, zoneOrProblem model with
    | None, _ -> model, []
    | _, Error problems -> withProblems form problems model, []
    | Some d, Ok zone ->
        match execute model (commandContext ctx model zone) model.Ledger (command d) with
        | Error problems -> withProblems form problems model, []
        | Ok ledger ->
            let next = { clear form model with Ledger = ledger; Announcement = announcement }
            commit ctx (changed model.Ledger ledger) [] { next with Detail = openDetail next d.ActivityId }

let private saveAmend (ctx: Ctx) (model: Model) =
    ledgerCommand
        ctx
        AmendForm
        "Amended. The earlier version stays in the history."
        (fun d ->
            Ledger.Amend(
                d.ActivityId,
                d.Revision,
                { Classification = Some(toClassification d.Amend)
                  Billability = None
                  BillingReference = None
                  Retime = None
                  Reason = d.AmendReason.Trim() }
            ))
        model

let private voidActivity ctx model =
    ledgerCommand ctx VoidForm "Removed from totals. The record and its history are kept." (fun d -> Ledger.Void(d.ActivityId, d.Revision, d.VoidReason.Trim())) model

let private restoreActivity ctx model =
    ledgerCommand ctx VoidForm "Restored to totals." (fun d -> Ledger.Restore(d.ActivityId, d.Revision)) model

let private minutesOf (text: string) =
    match Int32.TryParse(text.Trim()) with
    | true, n -> n
    | _ -> 0

let private saveSplit (ctx: Ctx) (model: Model) =
    let part (d: Detail) (index: int) (minutes: string) : Ledger.SplitPart =
        { ActivityId = ctx.NewId "ACT"
          Minutes = minutesOf minutes
          Classification = None
          EvidenceIds = d.SplitEvidence |> Map.toList |> List.filter (fun (_, p) -> p = index) |> List.map fst }

    ledgerCommand
        ctx
        SplitForm
        "Split into two activities. The original is kept, superseded."
        (fun d -> Ledger.Split(d.ActivityId, d.Revision, [ part d 1 d.SplitFirst; part d 2 d.SplitSecond ]))
        model

/// What a person typed, or None for nothing.
let private optionalText (text: string) =
    if String.IsNullOrWhiteSpace text then None else Some(text.Trim())

let private attachEvidence (ctx: Ctx) (model: Model) =
    ledgerCommand
        ctx
        EvidenceForm
        "Evidence linked."
        (fun d ->
            Ledger.LinkEvidence(
                d.ActivityId,
                d.Revision,
                { Id = ctx.NewId "EVD"
                  Url = d.EvidenceUrl.Trim()
                  Kind = d.EvidenceKind.Trim()
                  Label = d.EvidenceLabel.Trim()
                  CapturedAt = ctx.Now
                  Hash = None
                  Source = optionalText d.EvidenceSource
                  Notes = optionalText d.EvidenceNotes }
            ))
        model

let private unlinkEvidence (ctx: Ctx) (evidenceId: string) (model: Model) =
    ledgerCommand ctx EvidenceForm "Evidence unlinked. The link stays in the history." (fun d -> Ledger.UnlinkEvidence(d.ActivityId, d.Revision, evidenceId)) model

// ---- merge and attestation --------------------------------------------------------

let private saveMerge (ctx: Ctx) (model: Model) =
    match zoneOrProblem model with
    | Error problems -> withProblems MergeForm problems model, []
    | Ok zone ->
        let sources =
            model.MergeSelection
            |> List.choose (fun id -> model.Ledger.Activities.TryFind id |> Option.map (fun a -> id, a.Revision))

        let draft = toClassification model.MergeDraft

        // The merged record keeps the earliest source's classification,
        // except for what the person changed in the merge form.
        let classification =
            sources
            |> List.choose (fun (id, _) -> model.Ledger.Activities.TryFind id)
            |> List.sortBy (fun a -> a.Occurrence.LocalDate, a.Occurrence.LocalTime)
            |> List.tryHead
            |> Option.map (fun earliest ->
                let pick (typed: string) (kept: string) = if typed = "" then kept else typed
                let kept = earliest.Classification

                { kept with
                    ActivityTypeId = pick draft.ActivityTypeId kept.ActivityTypeId
                    ProjectId = pick draft.ProjectId kept.ProjectId
                    Description = pick draft.Description kept.Description
                    BusinessPurpose = pick draft.BusinessPurpose kept.BusinessPurpose })

        let newId = ctx.NewId "ACT"

        match execute model (commandContext ctx model zone) model.Ledger (Ledger.Merge(sources, newId, classification)) with
        | Error problems -> withProblems MergeForm (List.distinct problems) model, []
        | Ok ledger ->
            { clear MergeForm model with
                Ledger = ledger
                MergeSelection = []
                MergeDraft = emptyClassification
                Announcement = $"Merged {sources.Length} activities into one. The originals are kept, superseded." }
            |> commit ctx (changed model.Ledger ledger) []

let private attestDay (ctx: Ctx) (model: Model) =
    match zoneOrProblem model with
    | Error problems -> withProblems AttestForm problems model, []
    | Ok zone ->
        let workflow = { Review.start model.Ledger with Attestations = model.Attestations }
        let date = selectedDate model

        // A day in a submitted or closed period is attested before it is
        // submitted, or after the period is reopened (WI-0036).
        let attested =
            match PeriodReview.restrictions model.PeriodConfig model.Reviews [ model.Session.ActorId, date ] with
            | [] -> Review.attest (commandContext ctx model zone) date model.AttestStatement workflow
            | problems -> Error problems

        match attested with
        | Error problems -> withProblems AttestForm problems model, []
        | Ok(next, attestation) ->
            { clear AttestForm model with
                Attestations = next.Attestations
                AttestStatement = ""
                Announcement = $"Attested {Format.longDate date}." }
            |> commitWith ctx [] [] [ attestation ]

// ---- periods under review (WI-0036) ---------------------------------------------

/// The period on screen: the period page's, or the one holding today.
let selectedPeriod (model: Model) = Periods.containing model.PeriodConfig (selectedDate model)

/// Saves the organization's period settings as edited (15): the stored
/// configuration's next revision.
let private savePeriodSettings (ctx: Ctx) (model: Model) =
    let saved: PeriodConfigRecord.StoredPeriods =
        { Config = model.PeriodConfig
          Revision = model.PeriodsStored |> Option.map (fun stored -> stored.Revision + 1) |> Option.defaultValue 1
          ChangedBy = model.Session.ActorId
          ChangedAt = ctx.Now }

    { clear PeriodForm model with
        PeriodsStored = Some saved
        Announcement = "Period settings saved." }
    |> sendRequest { emptyRequest (ctx.NewId "COMMIT") with Periods = Some saved }

/// Sends a review step with the activities it changed and its audit entry.
let private sendReview (ctx: Ctx) (changedActivities: Activity list) (entries: Ledger.AuditEntry list) (review: PeriodReview.PeriodReview) (model: Model) =
    sendRequest
        { emptyRequest (ctx.NewId "COMMIT") with
            Activities = changedActivities
            Reviews = [ review ]
            Audit = AuditRecord.place changedActivities entries }
        { model with Reviews = model.Reviews @ [ review ] }

/// Submits the person's period on screen (14).
let private submitPeriod (ctx: Ctx) (model: Model) =
    match zoneOrProblem model with
    | Error problems -> withProblems ReviewForm problems model, []
    | Ok zone ->
        let period = selectedPeriod model

        match PeriodReview.submit (commandContext ctx model zone) model.PeriodConfig model.Reviews (ctx.NewId "SUB") period model.Ledger with
        | Error problems -> withProblems ReviewForm (List.distinct problems) model, []
        | Ok(ledger, review) ->
            let entries = ledger.Audit |> List.skip model.Ledger.Audit.Length

            { clear ReviewForm model with
                Ledger = ledger
                Store = { model.Store with Audited = ledger.Audit.Length }
                Announcement =
                    if model.PeriodConfig.ApprovalRequired then
                        $"Submitted {Format.longDate period.Start} to {Format.longDate period.Finish} for approval."
                    else
                        $"Submitted {Format.longDate period.Start} to {Format.longDate period.Finish}." }
            |> sendReview ctx (changed model.Ledger ledger) entries review

/// Reopens the person's own period on screen (14), with the reason:
/// withdrawing a submission waiting for approval needs only that they may
/// submit; reopening a closed period needs that they may reopen time.
let private reopenPeriod (ctx: Ctx) (model: Model) =
    let period = selectedPeriod model
    let actorId = model.Session.ActorId

    let needed =
        match PeriodReview.stateOf model.PeriodConfig.ApprovalRequired model.Reviews actorId period with
        | PeriodReview.AwaitingApproval _ -> Access.SubmitOwnTime
        | _ -> Access.ReopenTime

    match Access.authorize model.Roster model.Session.OrganizationId actorId needed, zoneOrProblem model with
    | Error refusal, _ -> withProblems ReviewForm [ refusal ] model, []
    | _, Error problems -> withProblems ReviewForm problems model, []
    | Ok(), Ok zone ->
        match PeriodReview.reopen (commandContext ctx model zone) model.PeriodConfig model.Reviews actorId period model.ReopenReason model.Ledger with
        | Error problems -> withProblems ReviewForm (List.distinct problems) model, []
        | Ok(ledger, review) ->
            let entries = ledger.Audit |> List.skip model.Ledger.Audit.Length

            { clear ReviewForm model with
                Ledger = ledger
                ReopenReason = ""
                Store = { model.Store with Audited = ledger.Audit.Length }
                Announcement = $"Reopened {Format.longDate period.Start} to {Format.longDate period.Finish}. Its time can be changed again." }
            |> sendReview ctx (changed model.Ledger ledger) entries review

/// Approves or returns another person's submission (14), over their time
/// as read for review.
let private decideSubmission (ctx: Ctx) (approve: bool) (submissionId: string) (model: Model) =
    match zoneOrProblem model with
    | Error problems -> withProblems ReviewForm problems model, []
    | Ok zone ->
        let theirs: Ledger.Ledger =
            { Activities = model.Reviewing |> List.map (fun a -> a.ActivityId, a) |> Map.ofList
              Audit = [] }

        let note = model.ReviewNotes.TryFind submissionId |> Option.map _.Trim() |> Option.filter ((<>) "")
        let context = commandContext ctx model zone

        let decided =
            if approve then
                PeriodReview.approve context model.PeriodConfig model.Reviews submissionId note theirs
            else
                PeriodReview.reject context model.PeriodConfig model.Reviews submissionId (defaultArg note "") theirs

        match decided with
        | Error problems -> withProblems ReviewForm (List.distinct problems) model, []
        | Ok(ledger, review) ->
            let decidedActivities = changed theirs ledger
            let names = model.Roster.Members.TryFind review.ActorId |> Option.map _.Principal.DisplayName |> Option.defaultValue review.ActorId

            { clear ReviewForm model with
                Reviewing =
                    model.Reviewing
                    |> List.map (fun a -> decidedActivities |> List.tryFind (fun d -> d.ActivityId = a.ActivityId) |> Option.defaultValue a)
                ReviewNotes = model.ReviewNotes.Remove submissionId
                Announcement =
                    if approve then
                        $"Approved {names}'s time, {Format.longDate review.Period.Start} to {Format.longDate review.Period.Finish}."
                    else
                        $"Returned {names}'s time, {Format.longDate review.Period.Start} to {Format.longDate review.Period.Finish}, for correction." }
            |> sendReview ctx decidedActivities ledger.Audit review

// ---- observations' candidates (WI-0038) ----------------------------------------

/// The candidate on screen, as the person may decide it.
let private candidateOnScreen (model: Model) =
    match model.Place with
    | Places.Candidate id -> visibleCandidates model |> List.tryFind (fun candidate -> candidate.CandidateId = id)
    | _ -> None

let private inboxOf (model: Model) : Observations.Inbox =
    { Observations.empty with Candidates = model.Candidates |> List.map (fun candidate -> candidate.CandidateId, candidate) |> Map.ofList }

/// Sends a candidate's decision with the activities it recorded and their
/// audit entries: one change, so the decision and its time land together.
let private sendCandidate (ctx: Ctx) (activities: Activity list) (entries: Ledger.AuditEntry list) (candidate: Observations.Candidate) (model: Model) =
    sendRequest
        { emptyRequest (ctx.NewId "COMMIT") with
            Activities = activities
            Candidates = [ candidate ]
            Audit = AuditRecord.place activities entries }
        { model with
            Candidates = model.Candidates |> List.map (fun c -> if c.CandidateId = candidate.CandidateId then candidate else c)
            CandidateDraftFor = ""
            CandidateReason = "" }

/// Accepts the candidate on screen as one activity, classified as the
/// person says (expansion 19, 26): every ordinary rule still applies, and
/// time in a submitted or closed period is refused until it is reopened.
let private acceptCandidate (ctx: Ctx) (model: Model) =
    match candidateOnScreen model, zoneOrProblem model with
    | None, _ -> model, []
    | _, Error problems -> withProblems CandidateForm problems model, []
    | Some candidate, Ok zone ->
        let classification = toClassification model.CandidateDraft

        let problems =
            [ if classification.ActivityTypeId = "" then MissingField "activityType"
              if classification.ProjectId = "" then MissingField "project"
              if classification.BusinessPurpose = "" then MissingField "businessPurpose"
              yield! Reference.assignmentProblems model.References [] { classification with Tags = [] } ]

        let context: Observations.DecisionContext =
            { Ledger = commandContext ctx model zone
              Zone = zone }

        let accepted =
            match problems with
            | _ :: _ -> Error problems
            | [] ->
                Observations.accept context (ctx.NewId "ACT") candidate.CandidateId candidate.Revision classification (inboxOf model) model.Ledger
                |> Result.bind (fun (inbox, ledger) ->
                    let touched = PeriodReview.touchedBy model.Ledger.Activities (changed model.Ledger ledger)

                    match PeriodReview.restrictions model.PeriodConfig model.Reviews touched with
                    | [] -> Ok(inbox.Candidates[candidate.CandidateId], ledger)
                    | restricted -> Error restricted)

        match accepted with
        | Error problems -> withProblems CandidateForm (List.distinct problems) model, []
        | Ok(decided, ledger) ->
            let recorded = changed model.Ledger ledger
            let entries = ledger.Audit |> List.skip model.Ledger.Audit.Length
            let total = recorded |> List.sumBy _.Minutes

            { clear CandidateForm model with
                Ledger = ledger
                Store = { model.Store with Audited = ledger.Audit.Length }
                Announcement = $"Accepted {Format.minutes total} from {candidate.Observation.SourceSystem}." }
            |> sendCandidate ctx recorded entries decided

/// Rejects the candidate on screen, with the reason (27): it stays, decided.
let private rejectCandidate (ctx: Ctx) (model: Model) =
    match candidateOnScreen model, zoneOrProblem model with
    | None, _ -> model, []
    | _, Error problems -> withProblems CandidateForm problems model, []
    | Some candidate, Ok zone ->
        let context: Observations.DecisionContext =
            { Ledger = commandContext ctx model zone
              Zone = zone }

        match Observations.reject context candidate.CandidateId candidate.Revision model.CandidateReason (inboxOf model) with
        | Error problems -> withProblems CandidateForm problems model, []
        | Ok inbox ->
            { clear CandidateForm model with Announcement = $"Rejected the observation from {candidate.Observation.SourceSystem}." }
            |> sendCandidate ctx [] [] inbox.Candidates[candidate.CandidateId]

// ---- reports --------------------------------------------------------------------

/// The report filter a draft names, over the session's own records. A date
/// the address does not name is this month's (`Places.defaultRange`).
let reportFilter (model: Model) : Reports.Filter =
    let draft = model.Report
    let first, last = Places.defaultRange (Model.today model)
    let from = Format.parseIsoDate draft.From |> Option.defaultValue first
    let ``to`` = Format.parseIsoDate draft.To |> Option.defaultValue last
    let one (value: string) = if value = "" then [] else [ value ]

    { Reports.between from ``to`` with
        ActorIds = [ model.Session.ActorId ]
        ProjectIds = one draft.ProjectId
        ActivityTypeIds = one draft.ActivityTypeId
        Tags = one draft.Tag
        EntryKinds =
            match draft.Method with
            | "manual" -> [ Reports.ManualEntries ]
            | "timer" -> [ Reports.TimerEntries ]
            | _ -> []
        Billability =
            match draft.Billability with
            | "billable" -> [ Billable ]
            | "non-billable" -> [ NonBillable ]
            | "pending" -> [ PendingClassification ]
            | _ -> []
        Text = draft.Text
        IncludeRemoved = draft.IncludeRemoved }

/// The export the report form describes, generated at the model's instant.
let exportText (model: Model) =
    let header: Reports.Header =
        { OrganizationId = model.Session.OrganizationId
          GeneratedAt = model.Report.GeneratedAt |> Option.defaultValue model.Now
          Filter = reportFilter model }

    let all = model.Ledger.Activities |> Map.toList |> List.map snd
    let policies = [ Billing.legacyDefault model.Session.OrganizationId ]

    match model.Report.Format with
    | "json" -> Reports.json policies header all
    | _ -> Reports.csv policies header all

// ---- the dispatcher -----------------------------------------------------------

let private draft (f: ClassificationDraft -> ClassificationDraft) (field: Model -> ClassificationDraft) (set: Model -> ClassificationDraft -> Model) (model: Model) =
    set model (f (field model)), []

let private timerDraft f = draft f _.TimerDraft (fun m d -> { m with TimerDraft = d })
let private completionDraft f = draft f _.CompletionDraft (fun m d -> { m with CompletionDraft = d })

let private manualDraft f = draft f _.Manual.Classification (fun m d -> { m with Manual = { m.Manual with Classification = d } })

let private manual (f: ManualDraft -> ManualDraft) (model: Model) = { model with Manual = f model.Manual }, []

// ---- quick entry (33, WI-0062) --------------------------------------------------------

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

let private recentAt (key: string option) (model: Model) =
    match key |> Option.map Int32.TryParse with
    | Some(true, index) -> recentCombinations model |> List.tryItem index
    | _ -> None

/// A recent combination fills the timer's draft.
let private timerUseRecent (key: string option) (model: Model) =
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
let private manualUseRecent (key: string option) (model: Model) =
    match recentAt key model with
    | Some c -> manual (fun d -> { d with Classification = ofClassification c }) model
    | None -> model, []

/// A common duration sets the end from the start, visibly: the end time is
/// written into its field and said, never changed silently afterwards.
let private manualDuration (key: string option) (model: Model) =
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
let private copyActivity (activityId: string) (model: Model) =
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

let private mergeDraft f = draft f _.MergeDraft (fun m d -> { m with MergeDraft = d })

let private amendDraft (f: ClassificationDraft -> ClassificationDraft) =
    detail (fun d -> { d with Amend = f d.Amend })

let private month (offset: int) (model: Model) =
    let first = (selectedMonth model).AddMonths offset
    refine (Places.Month(first.Year, first.Month)) model

// ---- the organization's members (3) -------------------------------------------------

/// The capability set an access level grants a principal of this kind.
let accessGrant (access: string) =
    match access with
    | "administrator" -> Some Access.Grants.administrator
    | "reviewer" -> Some Access.Grants.reviewer
    | "ownTime" -> Some Access.Grants.ownTime
    | _ -> None

/// Stores a roster change made by the session's person: the changed and
/// removed memberships go to the store as one commit.
let private rosterChange (ctx: Ctx) (commands: Access.RosterCommand list) (announcement: string) (model: Model) =
    match Access.executeAll model.Session.ActorId commands model.Roster with
    | Error problems -> withProblems MemberForm problems model, []
    | Ok roster ->
        // One command is one change of each member it touches: the next
        // revision of what was stored, however many capabilities moved.
        let changed =
            roster.Members
            |> Map.toList
            |> List.map snd
            |> List.filter (fun m -> model.Roster.Members.TryFind m.Principal.PrincipalId <> Some m)
            |> List.map (fun m ->
                match model.Roster.Members.TryFind m.Principal.PrincipalId with
                | Some before -> { m with Revision = before.Revision + 1 }
                | None -> { m with Revision = 1 })

        let roster =
            { roster with
                Members = changed |> List.fold (fun members m -> Map.add m.Principal.PrincipalId m members) roster.Members }

        let removed =
            model.Roster.Members |> Map.keys |> Seq.filter (roster.Members.ContainsKey >> not) |> List.ofSeq

        let request =
            { emptyRequest (ctx.NewId "COMMIT") with
                Members = changed
                RemovedMembers = removed }

        { clear MemberForm model with
            Roster = roster
            Announcement = announcement
            Store = { model.Store with Pending = model.Store.Pending @ [ request ] } },
        [ Store request ]

let private admitMember (ctx: Ctx) (model: Model) =
    let draft = model.MemberDraft
    let id = draft.Id.Trim()
    let name = draft.Name.Trim()

    let problems =
        [ if not (id <> "" && id |> Seq.forall Char.IsAsciiDigit) then
              MissingField "memberId"
          if name = "" then MissingField "memberName" ]

    match problems, accessGrant draft.Access with
    | [], Some grant ->
        let principal: Access.Principal =
            { PrincipalId = $"github:{id}"
              Kind = Access.Human
              DisplayName = name }

        let admitted, effects = rosterChange ctx [ Access.Admit(principal, Access.Grants.forKind principal.Kind grant) ] $"{name} can now work here." model
        (if admitted.Problems.ContainsKey MemberForm then admitted else { admitted with MemberDraft = emptyMember }), effects
    | problems, _ -> withProblems MemberForm problems model, []

let private changeMemberAccess (ctx: Ctx) (principalId: string) (access: string) (model: Model) =
    match model.Roster.Members.TryFind principalId, accessGrant access with
    | Some membership, Some grant ->
        let wanted = Access.Grants.forKind membership.Principal.Kind grant
        rosterChange ctx (Access.changesTo principalId membership.Capabilities wanted) $"{membership.Principal.DisplayName}'s access changed." model
    | None, _ -> withProblems MemberForm [ NotAMember(principalId, model.Roster.OrganizationId) ] model, []
    | _, None -> model, []

let private removeMember (ctx: Ctx) (principalId: string) (model: Model) =
    let name = model.Roster.Members.TryFind principalId |> Option.map _.Principal.DisplayName |> Option.defaultValue principalId
    rosterChange ctx [ Access.Remove principalId ] $"{name} was removed." model

// ---- changes not stored, and records edited outside Chrona (WI-0035) -----------

/// What sending a request again needs the person to hold now (3): the
/// roster may have changed since it was first sent.
let private requestNeeds (request: StoreRequest) =
    [ for activity in request.Activities do
          if activity.Revision = 1 then Access.RecordOwnTime else Access.AmendOwnTime
      if not request.Attestations.IsEmpty then
          Access.AttestOwnDay
      for item in request.References do
          match item.Kind with
          | Reference.ActivityType -> Access.ManageActivityTypes
          | Reference.Tag -> Access.ManageTags
          | Reference.Client
          | Reference.Project
          | Reference.Engagement -> Access.ManageProjects
      if not (request.Members.IsEmpty && request.RemovedMembers.IsEmpty) then
          Access.ManageOrganizationSettings ]
    |> List.distinct

/// The page as it is once a request's records are stored.
let private applyRequest (request: StoreRequest) (model: Model) =
    let members =
        request.Members
        |> List.fold (fun members (m: Access.Membership) -> Map.add m.Principal.PrincipalId m members) model.Roster.Members

    let audited =
        request.Audit |> List.map _.Entry |> List.filter (fun entry -> not (List.contains entry model.Ledger.Audit))

    { model with
        Ledger =
            { Activities = request.Activities |> List.fold (fun activities a -> Map.add a.ActivityId a activities) model.Ledger.Activities
              Audit = model.Ledger.Audit @ audited }
        References =
            { model.References with
                Items = request.References |> List.fold (fun items item -> Map.add (item.Kind, item.Id) item items) model.References.Items }
        Attestations = model.Attestations @ (request.Attestations |> List.filter (fun a -> not (List.contains a model.Attestations)))
        Store =
            { model.Store with
                Audited = model.Store.Audited + audited.Length }
        Roster =
            { model.Roster with
                Members = request.RemovedMembers |> List.fold (fun members id -> Map.remove id members) members } }

let private withoutCase (caseId: string) (model: Model) =
    { model with Store = { model.Store with Conflicts = model.Store.Conflicts |> List.filter (fun case -> case.Id <> caseId) } }

let private findCase (caseId: string) (model: Model) =
    model.Store.Conflicts |> List.tryFind (fun case -> case.Id = caseId)

/// The person keeps what is stored and sets their change aside, knowingly.
let private keepStored (caseId: string) (model: Model) =
    match findCase caseId model with
    | Some _ ->
        { clear ConflictForm (withoutCase caseId model) with
            Announcement = "Your change was set aside. What is stored stays." },
        []
    | None -> model, []

/// Sends a change again that was refused only because the repository kept
/// moving: the store decides it again on what is stored then.
let private retryChange (ctx: Ctx) (caseId: string) (model: Model) =
    match findCase caseId model with
    | Some case when case.Divergences = [ Reconcile.KeptChanging ] ->
        let refusals =
            requestNeeds case.Request
            |> List.choose (fun capability ->
                match Access.authorize model.Roster model.Session.OrganizationId model.Session.ActorId capability with
                | Ok() -> None
                | Error refusal -> Some refusal)

        match refusals with
        | [] ->
            let request = { case.Request with CommitId = ctx.NewId "COMMIT" }
            let next = applyRequest request (clear ConflictForm (withoutCase caseId model))

            { next with
                Store = { next.Store with Pending = next.Store.Pending @ [ request ] }
                Announcement = "Trying your change again." },
            [ Store request ]
        | refusals -> withProblems ConflictForm refusals model, []
    | _ -> model, []

/// The local wall-clock text a form shows for an instant.
let private formTime (zone: Zone) (instant: DateTimeOffset) =
    let local = occurrence zone instant
    Format.isoDate local.LocalDate, local.LocalTime.ToString("HH:mm", Globalization.CultureInfo.InvariantCulture)

/// The person redoes their version of one activity on what is stored now,
/// through the usual forms and rules: a changed classification goes into
/// the correction form of the current version; new time that overlapped
/// goes back into the manual entry form. The change is then theirs to save.
let private redoChange (caseId: string) (activityId: string) (model: Model) =
    let divergence =
        findCase caseId model
        |> Option.bind (fun case ->
            case.Divergences
            |> List.tryFind (function
                | Reconcile.ActivityChanged(mine, _)
                | Reconcile.OverlapsStored(mine, _) -> activityId = "" || mine.ActivityId = activityId
                | _ -> false))

    match divergence, model.Zone with
    | Some(Reconcile.ActivityChanged(mine, Some _)), _ ->
        let activityId = mine.ActivityId

        match openDetail model activityId with
        | Some current when model.Ledger.Activities[activityId].Record = Recorded ->
            let next = clear ConflictForm (withoutCase caseId model)

            navigate
                (Places.Entry(activityId, Some mine.Occurrence.LocalDate))
                { next with
                    Detail = Some { current with Amend = ofClassification mine.Classification }
                    Announcement = "Your version is in the correction form, on the current record. Save it to apply it." }
        | _ -> model, []
    | Some(Reconcile.OverlapsStored(mine, _)), Some zone when mine.Revision = 1 ->
        let next = clear ConflictForm (withoutCase caseId model)

        let startDate, startTime, endDate, endTime =
            match mine.Timing with
            | Interval(start, finish) ->
                let sd, st = formTime zone start
                let ed, et = formTime zone finish
                sd, st, ed, et
            | DurationOnDate _ ->
                let date = Format.isoDate mine.Occurrence.LocalDate
                date, "", date, ""

        navigate
            Places.Track
            { next with
                Manual =
                    { Classification = ofClassification mine.Classification
                      StartDate = startDate
                      StartTime = startTime
                      EndDate = endDate
                      EndTime = endTime
                      Reason = defaultArg mine.Reason "" }
                Announcement = "Your entry is back in the form. Change its time so it no longer overlaps, then save it." }
    | _ -> model, []

/// The person accepts a record of theirs edited outside Chrona, after it
/// passes the rules of a recorded activity (41). It is stored as Chrona's
/// next revision of it.
let private acceptOutsideEdit (ctx: Ctx) (activityId: string) (model: Model) =
    match model.Store.Held |> List.tryFind (fun a -> a.ActivityId = activityId), zoneOrProblem model with
    | None, _ -> model, []
    | _, Error problems -> withProblems OutsideEditForm problems model, []
    | Some held, Ok zone ->
        let trusted = model.Ledger.Activities |> Map.toList |> List.map snd

        match Persistence.acceptance (commandContext ctx model zone) model.Roster trusted held with
        | Error problems -> withProblems OutsideEditForm problems model, []
        | Ok accepted ->
            // Accepting is audited like any change to a record (25).
            let entry: Ledger.AuditEntry =
                { Performer = model.Session.ActorId
                  At = ctx.Now
                  Source = "chrona-web"
                  Command = "accept-outside-edit"
                  ActivityIds = [ activityId ]
                  PriorRevisions = [ activityId, held.Revision ]
                  ResultingRevisions = [ activityId, accepted.Revision ]
                  Reason = None
                  CorrelationId = None }

            let request =
                { emptyRequest (ctx.NewId "COMMIT") with
                    Activities = [ accepted ]
                    Accepted = [ held ]
                    Audit = AuditRecord.place [ accepted ] [ entry ] }

            let heldPath =
                ActivityRecord.path held |> Result.toOption |> Option.map Arca.RelativePath.render

            { clear OutsideEditForm model with
                Ledger =
                    { Activities = Map.add activityId accepted model.Ledger.Activities
                      Audit = model.Ledger.Audit @ [ entry ] }
                Store =
                    { model.Store with
                        Held = model.Store.Held |> List.filter (fun a -> a.ActivityId <> activityId)
                        Integrity =
                            model.Store.Integrity
                            |> List.filter (function
                                | ExternalEdit path -> Some path <> heldPath
                                | _ -> true)
                        Pending = model.Store.Pending @ [ request ]
                        Audited = model.Ledger.Audit.Length + 1 }
                Announcement = $"Accepted the outside change to \"{accepted.Classification.Description}\"." },
            [ Store request ]

/// The capability each command needs (3), and where a refusal is shown.
/// Typing into a draft or moving between screens needs none.
let requirement (name: string) (key: string option) : (Access.Capability * Form) option =
    match name with
    | "startTimer"
    | "pauseTimer"
    | "resumeTimer"
    | "stopTimer" -> Some(Access.RecordOwnTime, TimerForm)
    | "saveCompletion" -> Some(Access.RecordOwnTime, CompletionForm)
    | "saveManual" -> Some(Access.RecordOwnTime, ManualForm)
    | "addProject" -> Some(Access.ManageProjects, ReferenceForm)
    | "addActivityType" -> Some(Access.ManageActivityTypes, ReferenceForm)
    | "addTag" -> Some(Access.ManageTags, ReferenceForm)
    | "referenceActive" ->
        match (defaultArg key "").Split(':', 2) |> Array.tryHead |> Option.bind kindOf with
        | Some Reference.ActivityType -> Some(Access.ManageActivityTypes, ReferenceForm)
        | Some Reference.Tag -> Some(Access.ManageTags, ReferenceForm)
        | _ -> Some(Access.ManageProjects, ReferenceForm)
    | "saveAmend" -> Some(Access.AmendOwnTime, AmendForm)
    | "voidActivity"
    | "restoreActivity" -> Some(Access.AmendOwnTime, VoidForm)
    | "saveSplit" -> Some(Access.AmendOwnTime, SplitForm)
    | "attachEvidence"
    | "unlinkEvidence" -> Some(Access.AmendOwnTime, EvidenceForm)
    | "saveMerge" -> Some(Access.AmendOwnTime, MergeForm)
    | "attestDay" -> Some(Access.AttestOwnDay, AttestForm)
    | "admitMember"
    | "changeMemberAccess"
    | "removeMember" -> Some(Access.ManageOrganizationSettings, MemberForm)
    | "periodCadence"
    | "periodWeekStart"
    | "periodSubmissionExpected"
    | "periodApprovalRequired"
    | "savePeriodSettings" -> Some(Access.ManageOrganizationSettings, PeriodForm)
    | "submitPeriod" -> Some(Access.SubmitOwnTime, ReviewForm)
    | "approveSubmission" -> Some(Access.ApproveTime, ReviewForm)
    | "rejectSubmission" -> Some(Access.RejectTime, ReviewForm)
    | "acceptCandidate"
    | "rejectCandidate" -> Some(Access.RecordOwnTime, CandidateForm)
    | "copyExport"
    | "printReport" -> Some(Access.ExportTime, ExportForm)
    | "acceptOutsideEdit" -> Some(Access.AmendOwnTime, OutsideEditForm)
    | "rebuildIndex" -> Some(Access.ManageOrganizationSettings, IndexForm)
    | _ -> None

/// Runs a command only when the person holds what it needs; otherwise the
/// stable refusal is shown where the command was made, and nothing changes.
let private authorized (name: string) (key: string option) (model: Model) (run: unit -> Model * Effect list) =
    match requirement name key with
    | None -> run ()
    | Some(capability, form) ->
        match Access.authorize model.Roster model.Session.OrganizationId model.Session.ActorId capability with
        | Ok() -> run ()
        | Error refusal -> withProblems form [ refusal ] model, []

let private onEvent (ctx: Ctx) (name: string) (key: string option) (value: string) (isChecked: bool option) (model: Model) =
    let checkedOn = isChecked |> Option.defaultValue false

    let dayProject =
        match model.Place with
        | Places.Day(_, project) -> project
        | _ -> None

    let weekProject =
        match model.Place with
        | Places.Week(_, project) -> project
        | _ -> None

    // The report form changed: the address follows, replacing the entry.
    let report (change: ReportDraft -> ReportDraft) =
        let next = { model with Report = { change model.Report with GeneratedAt = Some ctx.Now } }
        refine (Places.Reports(queryOf next.Report)) next

    match name with
    | "goToday" -> navigate Places.Today model
    | "goTrack" -> navigate Places.Track model
    | "goMore" -> navigate (Places.Settings None) model
    | "copyLink" ->
        let place = Places.explicit (Model.today model) model.Place |> normalize model

        match model.RouteProblem, Places.format (addressOf model place) with
        | None, Ok location ->
            let link = Places.share model.Page.Origin model.Page.Path location
            { model with LinkStatus = ""; LinkText = link }, [ CopyText(LinkCopy, link) ]
        | _ -> model, []
    | "dayProject" ->
        let project = if value = "" then None else Some value

        match model.Place with
        | Places.Today when project.IsNone -> model, []
        | _ -> refine (Places.Day(selectedDate model, project)) model
    | "keepStored" -> keepStored (defaultArg key value) model
    | "retryChange" -> retryChange ctx (defaultArg key value) model
    | "redoChange" -> redoChange (defaultArg key "") value model
    | "acceptOutsideEdit" -> acceptOutsideEdit ctx (defaultArg key value) model
    | "rebuildIndex" ->
        match model.Store.Kind with
        | Durable _ ->
            { clear IndexForm model with
                Store = { model.Store with Index = "Rebuilding the activity index from the records…" } },
            [ RebuildIndex ]
        | InMemory -> model, []
    | "showDate" ->
        match Format.parseIsoDate value with
        | Some date -> refine (Places.Day(date, dayProject)) model
        | None -> model, []
    | "previousWeek" -> refine (Places.Week((weekOf model (selectedDate model)).Start.AddDays -7, weekProject)) model
    | "nextWeek" -> refine (Places.Week((weekOf model (selectedDate model)).Start.AddDays 7, weekProject)) model
    | "weekProject" -> refine (Places.Week((weekOf model (selectedDate model)).Start, (if value = "" then None else Some value))) model
    | "previousPeriod" -> refine (Places.Period (Periods.previous model.PeriodConfig (Periods.containing model.PeriodConfig (selectedDate model))).Start) model
    | "nextPeriod" -> refine (Places.Period (Periods.next model.PeriodConfig (Periods.containing model.PeriodConfig (selectedDate model))).Start) model
    | "previousDay" -> refine (Places.Day((selectedDate model).AddDays -1, dayProject)) model
    | "nextDay" -> refine (Places.Day((selectedDate model).AddDays 1, dayProject)) model

    | "timerActivityType" -> timerDraft (fun d -> { d with ActivityTypeId = value }) model
    | "timerProject" -> timerDraft (fun d -> { d with ProjectId = value }) model
    | "timerDescription" -> timerDraft (fun d -> { d with Description = value }) model
    | "timerUseRecent" -> timerUseRecent key model
    | "manualUseRecent" -> manualUseRecent key model
    | "manualDuration" -> manualDuration key model
    | "copyActivity" -> copyActivity (defaultArg key (model.Detail |> Option.map _.ActivityId |> Option.defaultValue value)) model
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

    | "openReview" ->
        match model.Place with
        | Places.Today -> navigate Places.ReviewToday model
        | _ -> navigate (Places.Review(selectedDate model)) model
    | "previousMonth" -> month -1 model
    | "nextMonth" -> month 1 model
    | "showMonth" ->
        // From the month picker (its value) or a month in the history (its key).
        let month = if String.IsNullOrEmpty value then defaultArg key "" else value

        match Format.parseIsoDate $"{month}-01" with
        | Some first -> refine (Places.Month(first.Year, first.Month)) model
        | None -> model, []

    | "amendActivityType" -> amendDraft (fun d -> { d with ActivityTypeId = value }) model
    | "amendProject" -> amendDraft (fun d -> { d with ProjectId = value }) model
    | "amendDescription" -> amendDraft (fun d -> { d with Description = value }) model
    | "amendPurpose" -> amendDraft (fun d -> { d with BusinessPurpose = value }) model
    | "amendReason" -> detail (fun d -> { d with AmendReason = value }) model
    | "saveAmend" -> saveAmend ctx model
    | "voidReason" -> detail (fun d -> { d with VoidReason = value }) model
    | "voidActivity" -> voidActivity ctx model
    | "restoreActivity" -> restoreActivity ctx model
    | "splitFirst" -> detail (fun d -> { d with SplitFirst = value }) model
    | "splitSecond" -> detail (fun d -> { d with SplitSecond = value }) model
    | "splitEvidence" -> detail (fun d -> { d with SplitEvidence = d.SplitEvidence.Add(defaultArg key "", minutesOf value) }) model
    | "saveSplit" -> saveSplit ctx model
    | "evidenceKind" -> detail (fun d -> { d with EvidenceKind = value }) model
    | "evidenceUrl" -> detail (fun d -> { d with EvidenceUrl = value }) model
    | "evidenceLabel" -> detail (fun d -> { d with EvidenceLabel = value }) model
    | "evidenceSource" -> detail (fun d -> { d with EvidenceSource = value }) model
    | "evidenceNotes" -> detail (fun d -> { d with EvidenceNotes = value }) model
    | "attachEvidence" -> attachEvidence ctx model
    | "unlinkEvidence" -> unlinkEvidence ctx (defaultArg key "") model

    | "mergeSelect" -> { model with MergeSelection = toggle (defaultArg key "") checkedOn model.MergeSelection }, []
    | "mergeActivityType" -> mergeDraft (fun d -> { d with ActivityTypeId = value }) model
    | "mergeProject" -> mergeDraft (fun d -> { d with ProjectId = value }) model
    | "mergeDescription" -> mergeDraft (fun d -> { d with Description = value }) model
    | "mergePurpose" -> mergeDraft (fun d -> { d with BusinessPurpose = value }) model
    | "saveMerge" -> saveMerge ctx model

    | "resolveObligation" ->
        // The obligation's id says where it is resolved (Project.obligations).
        match (defaultArg key "").Split('-', 2) with
        | [| "stopped"; _ |] -> navigate Places.Track model
        | [| "attestation"; date |] ->
            match Format.parseIsoDate date with
            | Some on -> navigate (Places.Review on) model
            | None -> invalidArg (nameof key) $"Unknown obligation: {key}"
        | [| "store" |] -> navigate (Places.Settings(Some Places.Changes)) model
        | [| "period"; _ |] -> navigate Places.ThisPeriod model
        | [| "candidates"; _ |] -> navigate (Places.Candidates Places.awaitingCandidates) model
        | [| "inbox"; _ |] -> navigate (Places.Settings(Some Places.Inboxes)) model
        | [| "approval"; submissionId |] ->
            match PeriodReview.awaitingApproval model.PeriodConfig.ApprovalRequired model.Reviews |> List.tryFind (fun s -> s.SubmissionId = submissionId) with
            | Some submission -> navigate (Places.Period submission.Period.Start) model
            | None -> navigate Places.ThisPeriod model
        | _ -> invalidArg (nameof key) $"Unknown obligation: {key}"
    | "periodCadence" ->
        let cadence =
            match value with
            | "daily" -> Periods.Daily
            | "weekly" -> Periods.Weekly
            // A fortnight counted from the week that contains today.
            | "biweekly" -> Periods.Biweekly((Periods.containing { model.PeriodConfig with Cadence = Periods.Weekly } (Model.today model)).Start)
            | "semimonthly" -> Periods.SemiMonthly
            | "monthly" -> Periods.Monthly
            | other -> invalidArg (nameof value) $"Unknown cadence: {other}"

        { clear PeriodForm model with PeriodConfig = { model.PeriodConfig with Cadence = cadence } }, []
    | "periodWeekStart" ->
        match Enum.TryParse<DayOfWeek>(value) with
        | true, day -> { clear PeriodForm model with PeriodConfig = { model.PeriodConfig with WeekStart = day } }, []
        | _ -> invalidArg (nameof value) $"Unknown day: {value}"
    | "periodSubmissionExpected" -> { clear PeriodForm model with PeriodConfig = { model.PeriodConfig with SubmissionExpected = checkedOn } }, []
    | "periodApprovalRequired" -> { clear PeriodForm model with PeriodConfig = { model.PeriodConfig with ApprovalRequired = checkedOn } }, []
    | "savePeriodSettings" -> savePeriodSettings ctx model
    | "submitPeriod" -> submitPeriod ctx model
    | "reopenReason" -> { model with ReopenReason = value }, []
    | "candidateActivityType" -> { model with CandidateDraft = { model.CandidateDraft with ActivityTypeId = value } }, []
    | "candidateProject" -> { model with CandidateDraft = { model.CandidateDraft with ProjectId = value } }, []
    | "candidateDescription" -> { model with CandidateDraft = { model.CandidateDraft with Description = value } }, []
    | "candidatePurpose" -> { model with CandidateDraft = { model.CandidateDraft with BusinessPurpose = value } }, []
    | "candidateReason" -> { model with CandidateReason = value }, []
    | "acceptCandidate" -> acceptCandidate ctx model
    | "rejectCandidate" -> rejectCandidate ctx model
    | "readInboxes" ->
        match model.Store.Kind with
        | Durable _ -> { model with Inbox = { model.Inbox with Running = true } }, [ ReadInboxes ]
        | InMemory -> model, []
    | "reopenPeriod" -> reopenPeriod ctx model
    | "reviewNote" -> { model with ReviewNotes = model.ReviewNotes.Add(defaultArg key "", value) }, []
    | "approveSubmission" -> decideSubmission ctx true (defaultArg key "") model
    | "rejectSubmission" -> decideSubmission ctx false (defaultArg key "") model
    | "reportFrom" -> report (fun r -> { r with From = value })
    | "reportTo" -> report (fun r -> { r with To = value })
    | "reportProject" -> report (fun r -> { r with ProjectId = value })
    | "reportActivityType" -> report (fun r -> { r with ActivityTypeId = value })
    | "reportTag" -> report (fun r -> { r with Tag = value })
    | "reportMethod" -> report (fun r -> { r with Method = value })
    | "reportBillability" -> report (fun r -> { r with Billability = value })
    | "reportText" -> report (fun r -> { r with Text = value })
    | "reportIncludeRemoved" -> report (fun r -> { r with IncludeRemoved = checkedOn })
    | "reportGrouping" -> report (fun r -> { r with Grouping = value })
    | "reportFormat" ->
        let next, effects = report (fun r -> { r with Format = value })
        { next with CopyStatus = "" }, effects
    | "copyExport" -> { clear ExportForm model with CopyStatus = "" }, [ CopyText(ExportCopy, exportText model) ]
    | "printReport" -> clear ExportForm model, [ Print ]
    | "goReports" -> navigate (Places.Reports Places.allReports) model
    | "reportMonth" ->
        // The month's report, its dates in the address.
        let first = selectedMonth model
        navigate (Places.Reports { Places.allReports with From = Some first; To = Some(first.AddMonths(1).AddDays -1) }) model
    | "memberId" -> { model with MemberDraft = { model.MemberDraft with Id = value } }, []
    | "memberName" -> { model with MemberDraft = { model.MemberDraft with Name = value } }, []
    | "memberAccess" -> { model with MemberDraft = { model.MemberDraft with Access = value } }, []
    | "admitMember" -> admitMember ctx model
    | "changeMemberAccess" -> changeMemberAccess ctx (defaultArg key "") value model
    | "removeMember" -> removeMember ctx (defaultArg key "") model
    | "attestStatement" -> { model with AttestStatement = value }, []
    | "attestDay" -> attestDay ctx model

    // The page and the engine disagree: a defect, not an operational failure.
    | other -> invalidArg (nameof name) $"Unknown event name: {other}"

// ---- sign-in (CHX-022, CHX-023) -------------------------------------------------

/// The session a page has before anyone signs in; it can do no work.
let noOne =
    { ActorId = "no-one"
      OrganizationId = "local"
      DisplayName = ""
      Kind = LocalSession }

/// The model a new session starts from: nothing of the previous person's
/// remains in memory. The route, the zone and the deployment carry over.
/// Whether this device keeps an active or stopped timer for the person (WI-0055).
let hasKeptTimer (model: Model) =
    TimerRecord.ofState model.Timer model.Stopped |> Option.isSome

// ---- the device's timer (WI-0055) ------------------------------------------------

/// Where this device keeps the person's timer.
let private timerKey (model: Model) =
    TimerRecord.key model.Session.OrganizationId model.Session.ActorId

/// Whether someone is working here whose timer this device keeps.
let private keepsTimers (model: Model) =
    match model.Identity.Mode with
    | SignedInMode
    | LocalOnly -> true
    | _ -> false

/// The records cannot be opened (offline at startup, for example), but the
/// person's own timer on this device keeps working.
let private offlineTimer (model: Model) =
    model.Identity.Mode = SignedInMode
    && model.Store.Failure.IsSome
    && hasKeptTimer model

/// A timer the device kept, recovered explicitly with its elapsed time
/// (10.2): never silently discarded, and never over a timer already here.
let private timerLoaded (key: string) (value: string option) (model: Model) =
    // Said now, and again when the records open.
    let say (note: string) (model: Model) = { model with Announcement = note; TimerNote = Some note }

    match value with
    | _ when key <> timerKey model || hasKeptTimer model -> model, []
    | None -> model, []
    | Some text ->
        match TimerRecord.decode text with
        // Left as it is in the browser: it is not overwritten while this
        // page has no timer of its own.
        | Error _ -> say "A timer kept in this browser could not be read. It was left as it is." model, []
        | Ok(TimerRecord.KeptStopped timer) ->
            match tryZone timer.ZoneId with
            | Error _ -> say "A stopped timer kept in this browser names an unknown time zone. It was left as it is." model, []
            | Ok zone ->
                let finish = timer.Segments |> List.choose _.Finish |> List.fold max (List.head timer.Segments).Start

                match Timer.stop zone finish (Timer.Paused timer) with
                | Ok(_, stopped) ->
                    say
                        $"Your stopped timer was recovered: {Format.minutes stopped.TotalMinutes} held. Complete the record to save it."
                        { model with
                            Stopped = Some stopped
                            LongTimerConfirmed = false
                            CompletionDraft =
                                stopped.Timer.Classification |> Option.map ofClassification |> Option.defaultValue emptyClassification },
                    []
                | Error _ -> model, []
        | Ok(TimerRecord.KeptPaused timer) ->
            say
                $"Your paused timer was recovered: {Format.minutes (Timer.elapsedMinutes model.Now timer)} so far."
                { model with Timer = Timer.Paused timer },
            []
        | Ok(TimerRecord.KeptRunning timer) ->
            let generation = model.TickGeneration + 1

            say
                $"Your timer was recovered and is still running: {Format.minutes (Timer.elapsedMinutes model.Now timer)} so far."
                { model with
                    Timer = Timer.Running timer
                    TickGeneration = generation },
            [ Wake(generation, TickMs) ]

// ---- the page coming back, and a newer Chrona (WI-0063) ----------------------------

/// The page is back after being hidden, frozen or restored from the
/// back/forward cache. Nothing was lost while it was away (the queue and the
/// timer are kept as they change), but it may be behind: the timer's
/// wake-ups start again from its timestamps, unsent changes are sent, the
/// records are read again, and the deployment is asked whether a newer
/// Chrona is served.
let private pageReturned (model: Model) =
    let generation = model.TickGeneration + 1

    let ticking =
        match model.Timer with
        | Timer.Running _ -> [ Wake(generation, TickMs) ]
        | _ -> []

    let records =
        match model.Store.Kind with
        | Durable _ when canWork model -> [ ReadMonths [] ]
        | _ -> []

    let unsent = if model.Store.Pending.IsEmpty && model.Store.Cached.IsNone then [] else [ SendUnsent ]
    let shell = if model.Shell.Build = Development then [] else [ CheckShell ]
    // The tab holding this browser's unsent changes may have closed meanwhile:
    // asked first, so what is sent next includes what it kept.
    let claim = if model.Store.Sync.Holder = HeldElsewhere false then [ ClaimQueue ] else []
    { model with TickGeneration = generation }, ticking @ claim @ unsent @ records @ shell

/// Reloads for the newer Chrona, unless something the page cannot keep would
/// be lost: changes not saved because they changed elsewhere live only in
/// this page until they are resolved. Unsent changes and the timer are kept
/// across a reload already.
let private reloadShell (model: Model) =
    match model.Shell.Newer with
    | Some _ when model.Store.Conflicts.IsEmpty -> model, [ ReloadPage ]
    | _ -> model, []

let private fresh (session: Session) (identity: IdentityState) (model: Model) =
    { Model.initial session model.Store.Kind model.Now with
        Place = model.Place
        RouteProblem = model.RouteProblem
        Router = model.Router
        Page = model.Page
        Zone = model.Zone
        PeriodConfig = model.PeriodConfig
        Deployment = model.Deployment
        Identity = identity }

let private configurationRead (text: string option) (model: Model) =
    let misconfigured detail =
        { model with Identity = { model.Identity with Mode = Misconfigured detail } }, []

    match text |> Option.map Deployment.parse with
    | None -> misconfigured "The deployment's configuration could not be read."
    | Some(Error diagnostic) -> misconfigured $"The deployment's configuration is not valid ({code diagnostic})."
    | Some(Ok config) ->
        let model = { model with Deployment = Some config }

        match config.Identity with
        | None ->
            let local = { model with Identity = { model.Identity with Mode = LocalOnly; Callback = [] } }
            local, [ LoadTimer(timerKey local) ]
        | Some identity ->
            { model with Identity = { model.Identity with Mode = SignInRequired true; Callback = [] } },
            [ BeginIdentity(identity, model.Identity.Callback) ]

/// The dates the first read covers: today and the current period, so the
/// today view, the period and the obligations have their records (38).
let private openingDates (model: Model) =
    let today = Model.today model
    let period = Periods.containing model.PeriodConfig today
    [ today; period.Start; period.Finish ] |> List.distinct

/// Starts reading the organization's records when the deployment keeps them;
/// until they arrive nothing can be done.
let private openStore (model: Model) =
    match model.Deployment with
    | Some config when config.Location.IsSome ->
        { model with
            Store =
                { model.Store with
                    Kind = Durable "GitHub"
                    Opening = true
                    Failure = None
                    Confirmation = None } },
        [ OpenStore(config, model.Session, openingDates model) ]
    | _ -> model, []

let private storeOpened (contents: StoreContents) (model: Model) =
    { model with
        Ledger =
            { Activities = contents.Activities |> List.map (fun a -> a.ActivityId, a) |> Map.ofList
              Audit = contents.Audit }
        References =
            { OrganizationId = model.Session.OrganizationId
              Items = contents.References |> List.map (fun item -> (item.Kind, item.Id), item) |> Map.ofList }
        Attestations = contents.Attestations
        Roster =
            { OrganizationId = model.Session.OrganizationId
              Members = contents.Members |> List.map (fun m -> m.Principal.PrincipalId, m) |> Map.ofList }
        Store =
            { model.Store with
                Kind = Durable contents.Name
                Opening = false
                Failure = None
                Confirmation = None
                Integrity = contents.Problems
                Held = contents.Held
                Months = contents.Months
                Audited = contents.Audit.Length
                Waiting = 0
                Reading = model.Store.Reading |> List.filter (fun month -> not (List.contains month contents.Months))
                History = contents.History
                Index = contents.Index
                Cached = contents.Cached }
        // The organization's periods as saved, reckoned in its zone (WI-0036).
        PeriodsStored = contents.Periods
        PeriodConfig =
            match contents.Periods with
            | Some stored -> { stored.Config with ZoneId = model.PeriodConfig.ZoneId }
            | None -> model.PeriodConfig
        Reviews = contents.Reviews
        Reviewing = contents.Reviewing
        Candidates = contents.Candidates
        // Said once, when they first open, with what recovering the timer
        // found; reading a further month is quiet.
        Announcement =
            if model.Store.Opening then
                match model.TimerNote with
                | Some note -> $"Your records are open. {note}"
                | None -> "Your records are open."
            else
                model.Announcement
        TimerNote = None },
    []

/// What an address's organization asks of the engine.
type private OrganizationStep =
    | StayHere
    | SwitchTo of organization: string
    | MissingOrganization of organization: string

/// Whether the engine knows which organizations the person may work in:
/// someone may work, under a deployment it has read.
let private knowsOrganizations (model: Model) =
    model.Deployment.IsSome
    && (match model.Identity.Mode with
        | LocalOnly
        | SignedInMode -> true
        | _ -> false)

let private organizationStep (model: Model) (address: Places.Address) =
    match address.Organization with
    | Some organization when knowsOrganizations model ->
        let known =
            if namesOrganization model then
                model.Deployment |> Option.bind (fun config -> Deployment.organization config organization) |> Option.isSome
            else
                organization = model.Session.OrganizationId

        if not known then MissingOrganization organization
        elif organization <> model.Session.OrganizationId then SwitchTo organization
        else StayHere
    | _ -> StayHere

/// The organization an address should name: the person's, where addresses
/// name one; none where they do not; and, until that is known (before
/// sign-in), whatever the address named, so a sign-in keeps it.
let private wantedOrganization (model: Model) (address: Places.Address) =
    if knowsOrganizations model then (addressOf model address.Place).Organization else address.Organization

/// The organization the page's address names, if the deployment serves it:
/// a sign-in opens that one rather than the first.
let private addressedOrganization (model: Model) (config: Deployment.DeploymentConfig) =
    model.Router.Current
    |> Option.bind (fun location -> Places.parse Limen.Routing.Router.allowAll location |> Result.toOption)
    |> Option.bind _.Organization
    |> Option.bind (Deployment.organization config)
    |> Option.map _.Id

let private identityChanged (change: IdentityChange) (model: Model) =
    let identity = model.Identity

    match change with
    | SignedInAs session ->
        // The person works in the organization the deployment serves.
        let session =
            { session with
                OrganizationId =
                    model.Deployment
                    |> Option.map (fun config -> addressedOrganization model config |> Option.defaultValue (Deployment.organizationId config))
                    |> Option.defaultValue session.OrganizationId }

        let model = fresh session { identity with Mode = SignedInMode; Notice = None } model
        let opened, effects = openStore { model with Announcement = $"Signed in as {session.DisplayName}." }
        opened, effects @ [ LoadTimer(timerKey opened) ]
    | SigningIn -> { model with Identity = { identity with Mode = SignInRequired true } }, []
    | SignedOutWith notice ->
        let signedOut = { identity with Mode = SignInRequired false; Notice = notice }

        match identity.Mode with
        // Whoever was signed in leaves nothing behind in this page.
        | SignedInMode -> { fresh noOne signedOut model with Announcement = "Signed out." }, []
        | _ -> { model with Identity = signedOut }, []
    | ProviderUnavailable -> { model with Identity = { identity with Notice = Some "provider_unavailable" } }, []

// ---- signing out with unsent changes (WI-0058) -----------------------------------

/// This account's changes that have not reached GitHub: those waiting to be
/// sent, and those not saved because the records moved (kept only in this
/// page until resolved).
let unsentCount (model: Model) =
    model.Store.Pending.Length + model.Store.Conflicts.Length

/// Everything of this account's that has not reached GitHub: changes, and
/// the device's timer.
let unsentWork (model: Model) =
    unsentCount model + (if hasKeptTimer model then 1 else 0)

let private policy (model: Model) =
    model.Deployment |> Option.map _.SharedDevice |> Option.defaultValue Deployment.Ask

/// Keeping them on this device for this account: only where the deployment
/// allows it, and only for changes the device can keep (a change not saved
/// because the records moved lives only in this page).
let canKeepUnsent (model: Model) =
    policy model = Deployment.Ask && model.Store.Conflicts.IsEmpty && unsentWork model > 0

/// Clearing Chrona's data from this browser: where records were read from
/// GitHub, and only when nothing of this account's would be lost with it.
let canClearDevice (model: Model) =
    (match model.Store.Kind with
     | Durable _ -> true
     | InMemory -> false)
    && unsentWork model = 0
    && model.Store.Conflicts.IsEmpty

/// Sending them now: when there are some to send. Whether GitHub can be
/// reached is found out by trying; if not, the choice comes back.
let canSendUnsent (model: Model) = not model.Store.Pending.IsEmpty

/// While signing out sends the unsent changes: signed out once every one is
/// stored; back to the choice when some could not be sent.
let private afterSending (model: Model) =
    let identity = model.Identity
    let choose note = { model with Identity = { identity with SignOut = Some ChoosingUnsent; SignOutNote = Some note } }, []

    match identity.SignOut with
    | Some SendingUnsent when model.Store.Pending.IsEmpty && model.Store.Conflicts.IsEmpty && hasKeptTimer model ->
        choose "Your changes are stored. Your timer is still on this device: keep it for this account, or discard it."
    | Some SendingUnsent when model.Store.Pending.IsEmpty && model.Store.Conflicts.IsEmpty ->
        { model with Identity = { identity with SignOut = None; SignOutNote = None } }, [ LeaveDevice(SentUnsent, 0); SignOut ]
    | Some SendingUnsent when model.Store.Pending.IsEmpty ->
        choose "Some changes were not saved because they changed elsewhere first. Resolve them under More, or discard them."
    | Some SendingUnsent when model.Store.Sync.Offline -> choose "GitHub cannot be reached, so they could not be sent."
    | _ -> model, []

let private onIdentityEvent (name: string) (value: string) (model: Model) =
    let identity = model.Identity

    match name, identity.Mode with
    | "signInRetention", _ ->
        let retention = if value = "tab" then ThisTab else ThisPage
        { model with Identity = { identity with Retention = retention } }, []
    | "signIn", SignInRequired false ->
        // The address to come back to is kept in this tab first: GitHub's
        // callback returns to the deployment's address, without it (CHX-460).
        let target =
            match model.Place with
            | Places.SignIn returnTo -> returnTo
            | _ -> None
            |> Option.orElse identity.ReturnTo

        { model with Identity = { identity with Mode = SignInRequired true; Notice = None } }, [ KeepReturn target; SignIn identity.Retention ]
    // Unsent changes are never left behind unknowingly (WI-0058).
    | "signOut", SignedInMode when unsentWork model > 0 ->
        { model with Identity = { identity with SignOut = Some ChoosingUnsent; SignOutNote = None } }, []
    | "signOut", SignedInMode -> model, [ LeaveDevice(NothingUnsent, 0); SignOut ]
    | "signOutSend", SignedInMode when identity.SignOut = Some ChoosingUnsent && canSendUnsent model ->
        { model with Identity = { identity with SignOut = Some SendingUnsent; SignOutNote = None } }, [ SendUnsent ]
    | "signOutKeep", SignedInMode when identity.SignOut = Some ChoosingUnsent && canKeepUnsent model ->
        { model with
            Identity = { identity with SignOut = None }
            Announcement = $"Your unsent work is kept on this device for {model.Session.DisplayName}." },
        [ LeaveDevice(KeptUnsent, unsentWork model); SignOut ]
    | "signOutDiscard", SignedInMode when identity.SignOut = Some ChoosingUnsent ->
        { model with Identity = { identity with SignOut = Some ConfirmingDiscard } }, []
    | "signOutDiscardConfirmed", SignedInMode when identity.SignOut = Some ConfirmingDiscard ->
        { model with Identity = { identity with SignOut = Some DiscardingUnsent } }, [ DiscardUnsent ]
    | "signOutCancel", SignedInMode when identity.SignOut.IsSome && identity.SignOut <> Some DiscardingUnsent ->
        { model with Identity = { identity with SignOut = None; SignOutNote = None } }, []
    | "clearDevice", SignedInMode when canClearDevice model ->
        { model with Identity = { identity with DeviceClear = Some ConfirmingClear; DeviceNote = None } }, []
    | "clearDeviceConfirmed", SignedInMode when identity.DeviceClear = Some ConfirmingClear && canClearDevice model ->
        { model with Identity = { identity with DeviceClear = Some Clearing } }, [ ClearDevice ]
    | "clearDeviceCancel", SignedInMode when identity.DeviceClear = Some ConfirmingClear ->
        { model with Identity = { identity with DeviceClear = None } }, []
    | "retryStore", SignedInMode when model.Store.Failure.IsSome -> openStore model
    | "confirmAdministrator", SignedInMode when model.Store.Confirmation |> Option.exists snd ->
        { model with Store = { model.Store with Opening = true; Confirmation = None } }, [ ConfirmAdministrator ]
    // Working in another of the deployment's organizations: nothing of the
    // current one stays in the page, and the other's records are opened.
    | "chooseOrganization", SignedInMode when value <> model.Session.OrganizationId ->
        match model.Deployment |> Option.bind (fun config -> Deployment.organization config value) with
        | Some organization ->
            let session = { model.Session with OrganizationId = organization.Id }
            let opened, effects = openStore (fresh session identity model)
            // Its home, named by the organization (CHX-460).
            let moved, more = navigate Places.Today opened
            moved, effects @ more
        | None -> model, []
    | _ -> model, []

// ---- where the person is (CHX-460) -----------------------------------------------


/// What the guards need to know about the person now.
let private standing (model: Model) : Places.Standing =
    { SignedOut = (model.Identity.Mode = SignInRequired false)
      KeptReturn = model.Identity.ReturnTo
      Administrator =
        if canWork model then
            Some(permits model Access.ManageOrganizationSettings)
        else
            None }

let private guardOf (model: Model) = Places.guard (standing model)

/// Someone may work now, and no kept return target is still being read.
let private mayResume (model: Model) =
    not model.Identity.ReadingReturn
    && (match model.Identity.Mode with
        | LocalOnly
        | SignedInMode -> true
        | _ -> false)

/// Whether a record an address names may still arrive: the records are
/// still opening, or a month is still being read.
let private stillLooking (model: Model) =
    not (canWork model) || model.Store.Opening || not model.Store.Reading.IsEmpty

/// A record the place names that does not exist, or is not the person's.
/// Decided again whenever records arrive.
let private recordProblem (model: Model) =
    let exists kind id =
        Reference.all kind model.References |> List.exists (fun item -> item.Id = id)

    let missingReference (kind: Reference.Kind, name: string) (id: string option) =
        match id with
        | Some id when canWork model && not (exists kind id) -> Some(RecordMissing(name, id))
        | _ -> None

    match model.Place with
    | Places.Entry(id, _) ->
        match model.Ledger.Activities.TryFind id with
        | Some activity when activity.ActorId = model.Session.ActorId -> None
        | Some _ -> Some(AddressProblem(Limen.Routing.RouteError.NotPermitted Places.Names.Entry))
        | None when stillLooking model -> None
        | None -> Some(RecordMissing("activity", id))
    | Places.Day(_, project)
    | Places.Week(_, project) -> missingReference (Reference.Project, "project") project
    | Places.Project id -> missingReference (Reference.Project, "project") (Some id)
    | Places.Candidate id ->
        match model.Candidates |> List.tryFind (fun candidate -> candidate.CandidateId = id) with
        | Some candidate when candidate.Observation.ActorId |> Option.forall ((=) model.Session.ActorId) -> None
        | Some _ -> Some(AddressProblem(Limen.Routing.RouteError.NotPermitted Places.Names.Candidate))
        | None when stillLooking model -> None
        | None -> Some(RecordMissing("candidate", id))
    | Places.Reports query ->
        [ missingReference (Reference.Project, "project") query.ProjectId
          missingReference (Reference.ActivityType, "activity type") query.ActivityTypeId
          missingReference (Reference.Tag, "tag") query.Tag ]
        |> List.tryPick id
    | _ -> None

/// Shows a place: entering a different one opens what it needs and reads
/// the months it shows that were not read yet.
let private arrive (place: Places.Place) (model: Model) =
    let model, effects =
        if place = model.Place then
            model, []
        else
            readNeeded (followRoute { model with Place = place })

    // An activity's drafts open once its record has arrived.
    match model.Place, model.Detail with
    | Places.Entry(id, _), None -> { model with Detail = openDetail model id }, effects
    | Places.Candidate id, _ -> openCandidate id model, effects
    | _ -> model, effects

/// Settles where the person is from the current address, after every
/// message: the address is adopted again under what the engine knows now
/// (who is signed in, what they may do, which records arrived). Adopting
/// never adds a history entry; at most it replaces the address with its
/// canonical form, or with sign-in when someone must sign in first. Once
/// someone may work, a sign-in page or a kept return target resumes to the
/// target (re-checked), replacing the entry so Back never returns to it.
let rec private settle (resumed: bool) (model: Model) : Model * Effect list =
    match model.Router.Current with
    | None -> model, []
    | Some location ->
        let router, result, correction = Limen.Routing.RouteCodec.adopt Places.codec (guardOf model) model.Router location
        let model = { model with Router = router }
        let corrected = correction |> Option.map Navigate |> Option.toList

        match result with
        | Ok { Place = Places.SignIn returnTo } when mayResume model && not resumed ->
            resumeAt (Places.resume (guardOf model) (returnTo |> Option.orElse model.Identity.ReturnTo)) model
        | Ok _ when model.Identity.ReturnTo.IsSome && mayResume model && not resumed ->
            resumeAt (Places.resume (guardOf model) model.Identity.ReturnTo) model
        | Ok address ->
            match organizationStep model address with
            | MissingOrganization organization -> { model with RouteProblem = Some(RecordMissing("organization", organization)) }, corrected
            // A link into another of the deployment's organizations: nothing of
            // this one stays in the page, and the other's records are opened.
            | SwitchTo organization ->
                let switched, opening = openStore (fresh { model.Session with OrganizationId = organization } model.Identity model)
                let settled, effects = settle resumed switched
                settled, corrected @ opening @ effects
            | StayHere ->
                // A week or period named by another of its days is named by its
                // first; the organization is named exactly when it must be.
                let wanted = { addressOf model (normalize model address.Place) with Organization = wantedOrganization model address }

                if wanted <> address && not resumed then
                    match Places.format wanted with
                    | Ok canonical -> resumeAt canonical model
                    | Error error -> invalidOp $"No address for {wanted}: %A{error}"
                else
                    let arrived, effects = arrive address.Place model
                    { arrived with RouteProblem = recordProblem arrived }, corrected @ effects
        | Error problem -> { model with RouteProblem = Some(AddressProblem problem) }, corrected

and private resumeAt (target: string) (model: Model) =
    let router, moved = Limen.Routing.Navigation.replace model.Router target
    let next, effects = settle true { model with Router = router; Identity = { model.Identity with ReturnTo = None } }
    next, (moved |> Option.map Navigate |> Option.toList) @ effects

let private step (ctx: Ctx) (msg: Msg) (model: Model) : Model * Effect list =
    // An announcement is spoken once: a person's next action replaces it;
    // wake-ups and store answers leave it alone.
    let model =
        match msg with
        | Ui _ -> { model with Now = ctx.Now; Announcement = "" }
        | _ -> { model with Now = ctx.Now }

    match msg with
    | Started(page, fragment, query) ->
        // A sign-in callback: the address to return to was kept in this tab.
        let callback = query |> List.exists (fun (name, _) -> name = "state" || name = "code" || name = "error")

        { model with
            Page = page
            Router = { model.Router with Current = Some(Places.ofFragment fragment) }
            Identity = { model.Identity with Callback = query; ReadingReturn = callback } },
        [ DescribeEnvironment; ReadConfiguration ] @ (if callback then [ ReadReturn ] else [])
    | ReturnRead target ->
        // Read once: it is forgotten from the tab at once, and kept only if it
        // is still one of Chrona's places, in canonical form.
        { model with
            Identity =
                { model.Identity with
                    ReturnTo = target |> Option.bind Places.captureReturn
                    ReadingReturn = false } },
        [ KeepReturn None ]
    | ConfigurationRead text -> configurationRead text model
    | IdentityChanged change -> identityChanged change model
    | StoreOpened contents ->
        let opened, effects = storeOpened contents model
        let read, more = readNeeded opened
        read, effects @ more
    | StoreUnavailable reason ->
        { model with Store = { model.Store with Opening = false; Failure = Some reason } }, []
    | StoreNeedsConfirmation(reason, canConfirm) ->
        { model with Store = { model.Store with Opening = false; Confirmation = Some(reason, canConfirm) } }, []
    | EnvironmentDescribed timeZone ->
        // An unknown zone falls back to UTC, visibly: the More screen says
        // which zone is in use.
        let zone = tryZone timeZone |> Result.toOption |> Option.orElse (tryZone "UTC" |> Result.toOption)

        { model with
            Zone = zone
            PeriodConfig = { model.PeriodConfig with ZoneId = zone |> Option.map _.Id |> Option.defaultValue "UTC" } },
        []
    | EnvironmentUnavailable -> { model with Zone = tryZone "UTC" |> Result.toOption }, []
    // Settled from the new address after this message, like every other.
    | LocationMoved fragment -> { model with Router = { model.Router with Current = Some(Places.ofFragment fragment) } }, []
    | Ticked generation ->
        match model.Timer with
        | Timer.Running _ when generation = model.TickGeneration -> model, [ Wake(generation, TickMs) ]
        | _ -> model, []
    | StoreAnswered(commitId, outcome) ->
        let pending = model.Store.Pending |> List.filter (fun request -> request.CommitId <> commitId)
        let sent = model.Store.Pending |> List.tryFind (fun request -> request.CommitId = commitId)

        let store =
            match outcome, sent with
            | Committed, _ ->
                { model.Store with
                    Pending = pending
                    Committed = model.Store.Committed + 1
                    Problem = None }
            // Kept for the person to resolve, with what diverged (23, 34).
            | Conflict divergences, Some request ->
                { model.Store with
                    Pending = pending
                    Problem = None
                    Conflicts =
                        model.Store.Conflicts
                        @ [ { Id = commitId
                              Request = request
                              Divergences = divergences } ] }
            | problem, _ -> { model.Store with Pending = pending; Problem = Some problem }

        { model with Store = store }, []
    | StoreResumed requests ->
        let known = model.Store.Pending |> List.map _.CommitId |> Set.ofList
        let resumed = requests |> List.filter (fun request -> not (known.Contains request.CommitId))
        { model with Store = { model.Store with Pending = model.Store.Pending @ resumed } }, []
    | SyncChanged sync ->
        let announcement =
            match model.Store.Sync.Holder, sync.Holder with
            | HeldElsewhere _, HeldHere -> "This tab now holds your unsent changes and sends them."
            | _ -> model.Announcement

        { model with Store = { model.Store with Sync = sync }; Announcement = announcement }, []
    | DeviceCleared(Ok()) ->
        { model with
            Identity = { model.Identity with DeviceClear = None; DeviceNote = None }
            Announcement = "Chrona's data was cleared from this browser." },
        [ SignOut ]
    | DeviceCleared(Error reason) ->
        { model with Identity = { model.Identity with DeviceClear = None; DeviceNote = Some reason } }, []
    | InboxReconciled status -> { model with Inbox = status }, []
    | UnsentHandedOver commitIds ->
        let handed = Set.ofList commitIds
        let pending, moved = model.Store.Pending |> List.partition (fun request -> not (handed.Contains request.CommitId))

        let announcement =
            if moved.IsEmpty then
                model.Announcement
            else
                "Your unsent changes moved to the other Chrona tab, which now sends them."

        { model with Store = { model.Store with Pending = pending }; Announcement = announcement }, []
    | IndexRebuilt summary -> { model with Store = { model.Store with Index = summary }; Announcement = summary }, []
    | UnsentDiscarded count ->
        // The device's timer goes with them (WI-0055).
        let timer = if hasKeptTimer model then [ SaveTimer(timerKey model, None) ] else []

        { model with
            Store = { model.Store with Pending = []; Conflicts = [] }
            Timer = Timer.Idle
            Stopped = None
            Identity = { model.Identity with SignOut = None; SignOutNote = None }
            Announcement = (if count = 1 then "1 unsent change was discarded." else $"{count} unsent changes were discarded.") },
        timer @ [ LeaveDevice(DiscardedUnsent, count); SignOut ]
    | UnsentWaiting(count, notice) ->
        // Reading them may have moved them from localStorage (WI-0059).
        let sync = { model.Store.Sync with Notice = notice |> Option.orElse model.Store.Sync.Notice }
        { model with Store = { model.Store with Waiting = count; Sync = sync } }, []
    | BuildKnown build ->
        let model = { model with Shell = { model.Shell with Build = build } }
        model, (if build = Development then [] else [ CheckShell ])
    | PageReturned -> pageReturned model
    // Back online: unsent changes are sent, and records shown from the read
    // cache are read from GitHub again (WI-0057).
    | ConnectionChanged true when not model.Store.Pending.IsEmpty || model.Store.Cached.IsSome -> model, [ SendUnsent ]
    | ConnectionChanged true -> model, []
    // Offline means the browser has no network: what waits is said to wait.
    | ConnectionChanged false -> { model with Store = { model.Store with Sync = { model.Store.Sync with Offline = true } } }, []
    | ShellChecked(Some build) when build <> model.Shell.Build && model.Shell.Build <> Development ->
        let first = model.Shell.Newer.IsNone

        { model with
            Shell = { model.Shell with Newer = Some build }
            Announcement = if first then "A newer Chrona is ready. Reload to use it." else model.Announcement },
        []
    | ShellChecked _ -> model, []
    | TimerLoaded(key, value) -> timerLoaded key value model
    | IndexChanged(history, summary) -> { model with Store = { model.Store with History = history; Index = summary } }, []
    | StoreReadFailed reason ->
        { model with
            Store = { model.Store with Reading = [] }
            Announcement = $"That month could not be read. {reason}" },
        []
    | Copied(ExportCopy, true) -> { model with CopyStatus = "Copied to the clipboard."; Announcement = "Copied to the clipboard." }, []
    | Copied(ExportCopy, false) ->
        let text = "This browser did not allow copying. Select the text and copy it yourself."
        { model with CopyStatus = text; Announcement = text }, []
    // Copied: the link is on the clipboard, so it is not shown to select.
    | Copied(LinkCopy, true) -> { model with LinkStatus = "Link copied."; LinkText = ""; Announcement = "Link to this view copied." }, []
    | Copied(LinkCopy, false) ->
        let text = "This browser did not allow copying. Select the link below and copy it yourself."
        { model with LinkStatus = text; Announcement = text }, []
    // The skip link moves focus to the page's content; it is not an address.
    | Ui("skipToContent", _, _, _) -> model, [ FocusControl "main" ]
    | Ui("reloadShell", _, _, _) -> reloadShell model
    // Whoever is signed in may wait for the other tab's changes: they are
    // sent with the account that made them, never another's.
    | Ui("takeOverQueue", _, _, _) when model.Store.Sync.Holder = HeldElsewhere false ->
        { model with
            Store = { model.Store with Sync = { model.Store.Sync with Holder = HeldElsewhere true } }
            Announcement = "Taking over the unsent changes from the other tab." },
        [ TakeOverQueue ]
    | Ui("takeOverQueue", _, _, _) -> model, []
    // Unsent changes from an earlier version that name no one: only the
    // person decides whose they are (Arca 0.4.0, ARCA-OFF-007).
    | Ui("sendEarlier", _, _, _) when model.Store.Sync.Earlier > 0 ->
        { model with Earlier = Undecided; Announcement = "Sending the earlier changes as yours." }, [ SendEarlier ]
    | Ui("keepEarlier", _, _, _) -> { model with Earlier = KeptEarlier }, []
    | Ui("discardEarlier", _, _, _) when model.Store.Sync.Earlier > 0 -> { model with Earlier = ConfirmingEarlierDiscard }, []
    | Ui("discardEarlierCancel", _, _, _) -> { model with Earlier = Undecided }, []
    | Ui("discardEarlierConfirmed", _, _, _) when model.Earlier = ConfirmingEarlierDiscard ->
        { model with Earlier = Undecided; Announcement = "Discarding the earlier changes." }, [ DiscardEarlier ]
    | Ui(("sendEarlier" | "discardEarlier" | "discardEarlierConfirmed"), _, _, _) -> model, []
    | Ui(("signIn"
         | "signInRetention"
         | "signOut"
         | "signOutSend"
         | "signOutKeep"
         | "signOutDiscard"
         | "signOutDiscardConfirmed"
         | "signOutCancel"
         | "clearDevice"
         | "clearDeviceConfirmed"
         | "clearDeviceCancel"
         | "retryStore"
         | "chooseOrganization"
         | "confirmAdministrator") as name,
         _,
         value,
         _) ->
        onIdentityEvent name value model
    // Nothing is recorded or shown for anyone until they may work.
    // Starting offline, the device's own timer keeps working (WI-0055).
    | Ui(("pauseTimer" | "resumeTimer" | "stopTimer") as name, key, value, isChecked) when offlineTimer model ->
        onEvent ctx name key value isChecked model
    | Ui _ when not (canWork model) -> model, []
    | Ui(name, key, value, isChecked) -> authorized name key model (fun () -> onEvent ctx name key value isChecked model)

/// Where focus goes after a timer control changed the timer: each control
/// replaces the one pressed (Start gives way to Pause, Pause to Resume, Stop
/// to the completion form), so focus moves to what took its place rather
/// than falling to the document (35, scenario 43).
let private focusAfter (msg: Msg) (model: Model) (next: Model) =
    match msg with
    | Ui(("startTimer" | "pauseTimer" | "resumeTimer" | "stopTimer"), _, _, _) ->
        // The page that could not open the records has its own timer panel.
        let gate = offlineTimer model

        match model.Timer, next.Timer with
        | Timer.Idle, Timer.Running _ -> Some "pause-timer"
        | Timer.Running _, Timer.Paused _ -> Some(if gate then "offline-resume" else "resume-timer")
        | Timer.Paused _, Timer.Running _ -> Some(if gate then "offline-pause" else "pause-timer")
        | (Timer.Running _ | Timer.Paused _), Timer.Idle when gate -> Some "offline-timer-title"
        | (Timer.Running _ | Timer.Paused _), Timer.Idle when next.Stopped.IsSome -> Some "completion-title"
        | (Timer.Running _ | Timer.Paused _), Timer.Idle -> Some "start-timer"
        | _ -> None
    | _ -> None

let update (ctx: Ctx) (msg: Msg) (model: Model) : Model * Effect list =
    let next, effects = step ctx msg model
    let next, routed = settle false next
    let effects = effects @ routed

    // The device keeps the person's timer whenever it changes, under their
    // own key; a different person or organization is never written for.
    let effects =
        let before, after = TimerRecord.ofState model.Timer model.Stopped, TimerRecord.ofState next.Timer next.Stopped

        if keepsTimers model && keepsTimers next && timerKey model = timerKey next && before <> after
           && not (effects |> List.exists (function SaveTimer _ -> true | _ -> false)) then
            effects @ [ SaveTimer(timerKey next, after |> Option.map TimerRecord.encode) ]
        else
            effects

    let effects = effects @ (focusAfter msg model next |> Option.map FocusControl |> Option.toList)

    match msg with
    // Signing out waits for the unsent changes it is sending.
    | StoreAnswered _
    | SyncChanged _
    | UnsentHandedOver _ ->
        let after, more = afterSending next
        after, effects @ more
    | _ -> next, effects
