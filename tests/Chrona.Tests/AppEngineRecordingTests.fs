/// Recording time in the engine: the timer, manual entry, today, reference
/// data and quick entry (33, WI-0062).
[<Xunit.Collection("AppEngine")>]
module Chrona.Tests.AppEngineRecordingTests

open System
open Xunit
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.View
open Chrona.Engine.App
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update
open Chrona.Engine.App.UpdateDispatch
open Chrona.Tests.AppEngineFixture

// ---- the timer -------------------------------------------------------------------

[<Fact>]
let ``starting needs an activity type and a project`` () =
    let model, effects = update (ctxAt start) (ui "startTimer" "") ready
    Assert.Empty effects
    Assert.Equal<Diagnostic list>([ MissingField "activityType"; MissingField "project" ], model.Problems[TimerForm])
    Assert.Equal<string list>([ "Add an activity type."; "Add a project." ], itemsOf "timerProblems" model |> List.map (field "text"))

[<Fact>]
let ``a running timer wakes the engine once a second and the display is computed, not counted`` () =
    let running, effects = play (chooseForTimer @ [ start, ui "startTimer" "" ]) ready

    match effects with
    // The engine wakes itself, the device keeps the timer (WI-0055), and
    // focus moves to Pause, which took Start's place (WI-0064).
    | [ Wake(1, TickMs); SaveTimer(key, Some kept); FocusControl "pause-timer" ] ->
        Assert.Equal(Chrona.Domain.TimerRecord.key "org-1" "person-1", key)
        Assert.True(Chrona.Domain.TimerRecord.decode kept |> Result.isOk)
    | other -> failwith $"%A{other}"
    Assert.Equal("Timer started.", running.Announcement)
    Assert.True(flagOf "timerRunning" running)
    Assert.Equal("Research", textOf "timerTitle" running)
    Assert.Equal("HelixNote · Product research", textOf "timerProjectLine" running)
    Assert.Equal("Started at 2:10 PM · exact elapsed time", textOf "timerStarted" running)

    let ticked, more = update (ctxAt (start.AddSeconds 1938.0)) (Ticked 1) running
    Assert.Equal<Effect list>([ Wake(1, TickMs) ], more)
    Assert.Equal("00:32:18", textOf "timerElapsed" ticked)
    Assert.Equal("32 minutes and 18 seconds", textOf "timerElapsedSpoken" ticked)
    // A wake-up never repeats an announcement or interrupts one.
    Assert.Equal("Timer started.", ticked.Announcement)

[<Fact>]
let ``pausing stops the wake-ups; a wake-up from an earlier run is ignored`` () =
    let paused, _ = play (chooseForTimer @ [ start, ui "startTimer" ""; at 10.0, ui "pauseTimer" "" ]) ready
    Assert.Equal<Effect list>([], snd (update (ctxAt (at 11.0)) (Ticked 1) paused))
    Assert.Equal("00:10:00", textOf "timerElapsed" (fst (update (ctxAt (at 30.0)) (Ticked 1) paused)))
    let resumed, effects = update (ctxAt (at 20.0)) (ui "resumeTimer" "") paused
    Assert.Equal<Effect list>([ Wake(2, TickMs); FocusControl "pause-timer" ], effects |> List.filter (function SaveTimer _ -> false | _ -> true))
    Assert.Equal<Effect list>([], snd (update (ctxAt (at 21.0)) (Ticked 1) resumed))
    Assert.Equal("Paused", textOf "timerStateText" paused)

[<Fact>]
let ``focus moves to the control that takes the pressed one's place, never to the document`` () =
    let focusOf effects = effects |> List.choose (function FocusControl id -> Some id | _ -> None)
    let running, started = play (chooseForTimer @ [ start, ui "startTimer" "" ]) ready
    Assert.Equal<string list>([ "pause-timer" ], focusOf started)
    let paused, effects = update (ctxAt (at 1.0)) (ui "pauseTimer" "") running
    Assert.Equal<string list>([ "resume-timer" ], focusOf effects)
    let resumed, effects = update (ctxAt (at 2.0)) (ui "resumeTimer" "") paused
    Assert.Equal<string list>([ "pause-timer" ], focusOf effects)
    // Stopped and held: the completion form, by its heading.
    let _, effects = update (ctxAt (at 32.0)) (ui "stopTimer" "") resumed
    Assert.Equal<string list>([ "completion-title" ], focusOf effects)
    // Stopped too soon to record: Start is back.
    let _, effects = update (ctxAt (start.AddSeconds 10.0)) (ui "stopTimer" "") (fst (play (chooseForTimer @ [ start, ui "startTimer" "" ]) ready))
    Assert.Equal<string list>([ "start-timer" ], focusOf effects)
    // A command that changes nothing moves nothing.
    let _, effects = update (ctxAt start) (ui "startTimer" "") ready
    Assert.Empty(focusOf effects)

[<Fact>]
let ``stopping under thirty seconds records nothing, and says so`` () =
    let stopped, _ = play (chooseForTimer @ [ start, ui "startTimer" ""; start.AddSeconds 20.0, ui "stopTimer" "" ]) ready
    Assert.Equal(Timer.Idle, stopped.Timer)
    Assert.True(stopped.Stopped.IsNone)
    Assert.Equal("Timer stopped after less than thirty seconds. Nothing was recorded.", stopped.Announcement)

[<Fact>]
let ``a stopped timer is held until completed, then recorded through the domain and committed`` () =
    let stopped, _ = play (chooseForTimer @ [ start, ui "startTimer" ""; at 32.0, ui "stopTimer" "" ]) ready
    Assert.True(flagOf "hasStopped" stopped)
    Assert.Equal("32m", textOf "stoppedTotal" stopped)
    Assert.Equal("2:10 PM", textOf "stoppedStarted" stopped)
    Assert.Equal("2:42 PM", textOf "stoppedStopped" stopped)
    Assert.Equal("Product research", textOf "completeDescription" stopped)

    // The business purpose is required (R1); nothing is recorded without it.
    let refused, effects = update (ctxAt (at 33.0)) (ui "saveCompletion" "") stopped
    Assert.Empty effects
    Assert.Equal<Diagnostic list>([ MissingField "businessPurpose" ], refused.Problems[CompletionForm])
    Assert.True(refused.Ledger.Activities.IsEmpty)

    let saved, effects =
        play [ at 33.0, ui "completePurpose" "Define the mobile ledger"; at 33.0, check "completeTag" tag true; at 34.0, ui "saveCompletion" "" ] stopped

    let activity = saved.Ledger.Activities |> Map.toList |> List.exactlyOne |> snd
    Assert.Equal(32, activity.Minutes)
    Assert.Equal(Activity.EntryMethod.Timer, activity.EntryMethod)
    Assert.Equal<string list>([ tag ], activity.Classification.Tags)
    Assert.Equal("Saved 32m of Product research.", saved.Announcement)
    Assert.True(saved.Stopped.IsNone)
    Assert.False(saved.Problems.ContainsKey CompletionForm)

    match effects with
    // Completed: the activity is committed, and the device no longer keeps the timer.
    | [ Store request; SaveTimer(_, None) ] ->
        Assert.Equal<Activity.Activity list>([ activity ], request.Activities)
        Assert.Equal<string list>([ request.CommitId ], saved.Store.Pending |> List.map _.CommitId)
        Assert.Equal("Saving…", textOf "storeHeadline" saved)
        let answered, _ = update (ctxAt (at 34.0)) (StoreAnswered(request.CommitId, Committed)) saved
        Assert.Equal("Kept in this tab only", textOf "storeHeadline" answered)
        Assert.Equal(saved.Store.Committed + 1, answered.Store.Committed)
    | other -> failwith $"{other}"

[<Fact>]
let ``a long-running timer is saved only after the person confirms the time`` () =
    let stopped, _ =
        play (chooseForTimer @ [ start.AddHours -15.0, ui "startTimer" ""; start, ui "stopTimer" ""; start, ui "completePurpose" "Delivery" ]) ready

    Assert.True(flagOf "stoppedNeedsReview" stopped)
    let refused, _ = update (ctxAt start) (ui "saveCompletion" "") stopped
    Assert.Equal<Diagnostic list>([ LongRunningTimerNeedsReview 900 ], refused.Problems[CompletionForm])
    let saved, _ = play [ start, Ui("confirmLongTimer", None, "", Some true); start, ui "saveCompletion" "" ] stopped
    // Across midnight: one activity per business day.
    Assert.Equal(900, saved.Ledger.Activities |> Map.toList |> List.sumBy (snd >> _.Minutes))
    Assert.Equal(2, saved.Ledger.Activities.Count)

// ---- manual entry -------------------------------------------------------------------

[<Fact>]
let ``manual entry reports every problem at once`` () =
    let refused, effects = update (ctxAt start) (ui "saveManual" "") ready
    Assert.Empty effects

    Assert.Equal<Diagnostic list>(
        [ MissingField "start"; MissingField "end"; MissingField "project"; MissingField "activityType"; MissingField "description"; MissingField "businessPurpose" ],
        refused.Problems[ManualForm]
    )

[<Fact>]
let ``manual time today is recorded exactly, and the form clears`` () =
    let saved, effects = play (manualDraft "2026-10-08" @ [ start, ui "saveManual" "" ]) ready
    let activity = saved.Ledger.Activities |> Map.toList |> List.exactlyOne |> snd
    Assert.Equal(75, activity.Minutes)
    Assert.Equal(Activity.EntryMethod.Manual, activity.EntryMethod)
    Assert.Equal(emptyManual, saved.Manual)
    Assert.Equal("Saved 1h 15m on Thursday, October 8.", saved.Announcement)
    Assert.True(effects |> List.exists (function Store _ -> true | _ -> false))

[<Fact>]
let ``any earlier day needs a reason (the legacy rule), and the future is refused`` () =
    let refused, _ = play (manualDraft "2026-10-07" @ [ start, ui "saveManual" "" ]) ready
    Assert.Equal<Diagnostic list>([ ReasonRequiredForHistoricalEntry ], refused.Problems[ManualForm])
    let explained, _ = play [ start, ui "manualReason" "From notes"; start, ui "saveManual" "" ] refused
    Assert.Equal(1, explained.Ledger.Activities.Count)

    let future, _ = play (manualDraft "2026-10-09" @ [ start, ui "manualEndDate" "2026-10-09"; start, ui "saveManual" "" ]) ready
    Assert.Contains(FutureTime, future.Problems[ManualForm])

[<Fact>]
let ``an entry spanning two dates is refused: activities do not cross midnight`` () =
    let refused, _ = play (manualDraft "2026-10-07" @ [ start, ui "manualEndDate" "2026-10-08"; start, ui "saveManual" "" ]) ready
    Assert.Equal<Diagnostic list>([ CrossesBusinessDay ], refused.Problems[ManualForm])

[<Fact>]
let ``overlapping manual time is refused and names the activity it overlaps`` () =
    let once, _ = play (manualDraft "2026-10-08" @ [ start, ui "saveManual" "" ]) ready
    let twice, _ = play (manualDraft "2026-10-08" @ [ start, ui "saveManual" "" ]) once
    Assert.Equal<string list>([ "This overlaps \"Visual research\". Adjust the times so they do not overlap." ], itemsOf "manualProblems" twice |> List.map (field "text"))
    Assert.Equal<string list>([ "CHRONA.OVERLAP.OVERLAPS_ACTIVITY" ], itemsOf "manualProblems" twice |> List.map (field "code"))

// ---- today ---------------------------------------------------------------------------

[<Fact>]
let ``today shows exact and billed totals, and the day's records in order`` () =
    let model, _ =
        play
            (manualDraft "2026-10-08"
             @ [ start, ui "saveManual" "" ]
             @ [ start, ui "manualActivityType" activityType
                 start, ui "manualProject" project
                 start, ui "manualStartDate" "2026-10-08"
                 start, ui "manualStartTime" "07:00"
                 start, ui "manualEndTime" "07:02"
                 start, ui "manualDescription" "Inbox"
                 start, ui "manualPurpose" "Triage"
                 start, ui "saveManual" "" ])
            ready

    Assert.Equal("Today", textOf "dayEyebrow" model)
    Assert.Equal("Thursday, October 8", textOf "dayTitle" model)
    Assert.Equal("1h 17m", textOf "dayTotal" model)
    // Six-minute billing per activity, never on the total: 78m + 6m.
    Assert.Equal("1h 24m billed", textOf "dayBilled" model)
    Assert.Equal("2 entries", textOf "dayEntryCount" model)
    let rows = itemsOf "dayRecords" model
    Assert.Equal<string list>([ "Inbox"; "Visual research" ], rows |> List.map (field "title"))
    Assert.Equal<string list>([ "7:00 AM"; "9:00 AM" ], rows |> List.map (field "start"))
    Assert.Equal("Research · HelixNote", field "classification" rows[1])
    Assert.Equal("Manual entry", field "method" rows[1])
    Assert.Equal("1h 18m billed", field "billed" rows[1])
    Assert.False(flagOf "dayEmpty" model)
    Assert.True(flagOf "dayEmpty" ready)

// ---- reference data -------------------------------------------------------------------

[<Fact>]
let ``reference data is added, archived and reactivated through the domain`` () =
    Assert.Equal<string list>([ "HelixNote" ], itemsOf "projects" ready |> List.map (field "name"))
    Assert.False(flagOf "needsReferences" ready)
    Assert.True(flagOf "needsReferences" fresh)
    let key = $"project:{project}"
    let archived, effects = update (ctxAt start) (check "referenceActive" key false) ready
    Assert.Equal("Archived: kept on past records", itemsOf "projects" archived |> List.head |> field "state")
    Assert.Empty(itemsOf "timerProjectOptions" archived)
    Assert.True(effects |> List.exists (function Store r -> r.References.Length = 1 | _ -> false))
    let reactivated, _ = update (ctxAt start) (check "referenceActive" key true) archived
    Assert.Equal(1, (itemsOf "timerProjectOptions" reactivated).Length)

    let blank, _ = play [ start, ui "newTagName" "  "; start, ui "addTag" "" ] ready
    Assert.Equal<Diagnostic list>([ MissingField "name" ], blank.Problems[ReferenceForm])

// ---- quick entry (33, WI-0062) ----------------------------------------------------------

[<Fact>]
let ``recent combinations are offered newest first, and fill a form's classification but never its time`` () =
    let model, _ = play (entry "2026-10-08" "09:00" "10:00" "Pairing" @ entry "2026-10-08" "11:00" "12:00" "Review") ready
    let labels name = itemsOf name model |> List.map (field "label")
    Assert.Equal<string list>([ "Research · HelixNote · Review"; "Research · HelixNote · Pairing" ], labels "manualRecent")
    Assert.Equal<string list>(labels "manualRecent", labels "timerRecent")

    let typed, _ = play [ start, ui "manualStartDate" "2026-10-08"; start, ui "manualStartTime" "13:00" ] model
    let used, _ = update (ctxAt start) (keyed "manualUseRecent" "1" "") typed
    Assert.Equal("Pairing", used.Manual.Classification.Description)
    Assert.Equal(project, used.Manual.Classification.ProjectId)
    Assert.Equal(("2026-10-08", "13:00", ""), (used.Manual.StartDate, used.Manual.StartTime, used.Manual.EndTime))

    let timer, _ = update (ctxAt start) (keyed "timerUseRecent" "0" "") model
    Assert.Equal(("Review", activityType), (timer.TimerDraft.Description, timer.TimerDraft.ActivityTypeId))

[<Fact>]
let ``a common duration sets a visible end from the start, and never crosses the business day`` () =
    let none, _ = update (ctxAt start) (keyed "manualDuration" "60" "") ready
    Assert.Equal<Diagnostic list>([ MissingField "start" ], none.Problems[ManualForm])
    Assert.True(itemsOf "manualDurations" ready |> List.forall (fun row -> row["disabled"] = Chrona.Engine.View.Flag true))

    let typed, _ = play [ start, ui "manualStartDate" "2026-10-08"; start, ui "manualStartTime" "09:30" ] ready
    let set, _ = update (ctxAt start) (keyed "manualDuration" "90" "") typed
    Assert.Equal(("2026-10-08", "11:00"), (set.Manual.EndDate, set.Manual.EndTime))
    Assert.Equal("End set to 11:00 AM, 1h 30m after the start.", set.Announcement)

    // A duration the deployment does not offer does nothing.
    Assert.Equal(typed.Manual, (fst (update (ctxAt start) (keyed "manualDuration" "7" "") typed)).Manual)

    let late, _ = play [ start, ui "manualStartTime" "23:30" ] typed
    let crossing, _ = update (ctxAt start) (keyed "manualDuration" "60" "") late
    Assert.Equal<Diagnostic list>([ CrossesBusinessDay ], crossing.Problems[ManualForm])
    Assert.Equal("", crossing.Manual.EndTime)

[<Fact>]
let ``an earlier entry is copied as a new draft for today, its time left to the person`` () =
    let model, _ = play (entry "2026-10-07" "09:00" "10:00" "Pairing" @ [ start, ui "manualReason" "From notes"; start, ui "saveManual" "" ]) ready
    let id = model.Ledger.Activities |> Map.toList |> List.head |> fst
    let opened, _ = update (ctxAt start) (LocationMoved $"#/entries/{id}") model
    let copied, effects = update (ctxAt start) (ui "copyActivity" "") opened
    Assert.Equal<Effect list>([ push "/track" ], effects)
    Assert.Equal("Pairing", copied.Manual.Classification.Description)
    Assert.Equal(("2026-10-08", "", "2026-10-08", ""), (copied.Manual.StartDate, copied.Manual.StartTime, copied.Manual.EndDate, copied.Manual.EndTime))

[<Fact>]
let ``a deployment chooses its common durations, and nothing else is accepted`` () =
    let parse (text: string) = Deployment.parse text

    match parse """{"environment":"local","environmentName":"local","quickDurations":[10,20]}""" with
    | Ok config -> Assert.Equal<int list>([ 10; 20 ], config.QuickDurations)
    | Error e -> failwith $"{e}"

    match parse """{"environment":"local","environmentName":"local"}""" with
    | Ok config -> Assert.Equal<int list>(Deployment.defaultQuickDurations, config.QuickDurations)
    | Error e -> failwith $"{e}"

    for bad in [ "[]"; "[0]"; "[721]"; "[1.5]"; "[15,15]"; "\"15\"" ] do
        Assert.True(parse $$"""{"environment":"local","environmentName":"local","quickDurations":{{bad}}}""" |> Result.isError, bad)
