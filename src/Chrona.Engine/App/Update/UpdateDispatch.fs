/// Every state change of the Chrona application: `update ctx msg model`
/// returns the next model and the effects it wants, as data. Pure: the
/// current instant and fresh ids arrive in `Ctx`.
module Chrona.Engine.App.UpdateDispatch

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update
open Chrona.Engine.App.UpdateCommands
open Chrona.Engine.App.UpdateRecording
open Chrona.Engine.App.UpdateCorrections
open Chrona.Engine.App.UpdateReports
open Chrona.Engine.App.UpdatePlaces
open Chrona.Engine.App.UpdatePeriods
open Chrona.Engine.App.UpdateCandidates
open Chrona.Engine.App.UpdateMembers
open Chrona.Engine.App.UpdateResolving
open Chrona.Engine.App.UpdateEvents
open Chrona.Engine.App.UpdateDeviceTimer
open Chrona.Engine.App.UpdateSession
open Chrona.Engine.App.UpdateSignOut
open Chrona.Engine.App.UpdateRouting

let internal step (ctx: Ctx) (msg: Msg) (model: Model) : Model * Effect list =
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
            Router = { model.Router with Current = Some(PlaceAddresses.ofFragment fragment) }
            Identity = { model.Identity with Callback = query; ReadingReturn = callback } },
        [ DescribeEnvironment; ReadConfiguration ] @ (if callback then [ ReadReturn ] else [])
    | ReturnRead target ->
        // Read once: it is forgotten from the tab at once, and kept only if it
        // is still one of Chrona's places, in canonical form.
        { model with
            Identity =
                { model.Identity with
                    ReturnTo = target |> Option.bind PlaceAddresses.captureReturn
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
    | LocationMoved fragment -> { model with Router = { model.Router with Current = Some(PlaceAddresses.ofFragment fragment) } }, []
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
    | ElsewhereRead read ->
        let read = read |> List.map (fun elsewhere -> elsewhere.OrganizationId, elsewhere) |> Map.ofList

        { model with
            Elsewhere = Map.fold (fun all id elsewhere -> Map.add id elsewhere all) model.Elsewhere read
            ElsewhereReading = model.ElsewhereReading |> List.filter (read.ContainsKey >> not) },
        []
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
            Store = { model.Store with Reading = []; OthersReading = [] }
            ElsewhereReading = []
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
let internal focusAfter (msg: Msg) (model: Model) (next: Model) =
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
