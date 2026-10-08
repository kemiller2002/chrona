/// The Chrona application engine (WI-0046): pure update and projection,
/// driven message by message with a fixed clock and deterministic ids.
module Chrona.Tests.AppEngineTests

open System
open Xunit
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.View
open Chrona.Engine.App
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update

let private session =
    { ActorId = "person-1"
      OrganizationId = "org-1"
      DisplayName = "Kevin Miller"
      Kind = LocalSession }

/// 2026-10-08 14:10:00 in New York (EDT).
let private start = DateTimeOffset(2026, 10, 8, 14, 10, 0, TimeSpan.FromHours -4.0)

/// Ids are unique across the whole test run, as the edge's are.
let private sequence = ref 0

let private ctxAt (now: DateTimeOffset) =
    { Now = now
      NewId = fun prefix -> $"{prefix}-{System.Threading.Interlocked.Increment sequence}" }

/// Applies messages in order, each at its own instant; keeps every effect.
let private play (steps: (DateTimeOffset * Msg) list) (model: Model) =
    steps
    |> List.fold
        (fun (model, effects) (at, msg) ->
            let next, produced = update (ctxAt at) msg model
            next, effects @ produced)
        (model, [])

let private ui name value = Ui(name, None, value, None)
let private keyed name key value = Ui(name, Some key, value, None)
let private check name key on = Ui(name, Some key, "", Some on)

let private fresh = initial session InMemory start

/// Answers every outstanding store request, as the in-memory store does.
let private settle (model: Model) =
    model.Store.Pending |> List.fold (fun m id -> fst (update (ctxAt start) (StoreAnswered(id, Committed)) m)) model

/// Started in New York with a project, an activity type and a tag, all stored.
let private ready =
    play
        [ start, Started "#/track"
          start, EnvironmentDescribed "America/New_York"
          start, ui "newProjectName" "HelixNote"
          start, ui "addProject" ""
          start, ui "newActivityTypeName" "Research"
          start, ui "addActivityType" ""
          start, ui "newTagName" "Backend"
          start, ui "addTag" "" ]
        fresh
    |> fst
    |> settle

let private idOf (kind: Reference.Kind) (model: Model) = (Reference.selectable kind model.References |> List.head).Id
let private project = idOf Reference.Project ready
let private activityType = idOf Reference.ActivityType ready
let private tag = idOf Reference.Tag ready

let private view (model: Model) = Project.project model |> Map.ofList

let private textOf (key: string) (model: Model) =
    match (view model)[key] with
    | Value(Text t) -> t
    | other -> failwith $"{key} is {other}"

let private flagOf (key: string) (model: Model) =
    match (view model)[key] with
    | Value(Flag f) -> f
    | other -> failwith $"{key} is {other}"

let private itemsOf (key: string) (model: Model) =
    match (view model)[key] with
    | Items rows -> rows |> List.map Map.ofList
    | other -> failwith $"{key} is {other}"

let private field (name: string) (row: Map<string, Scalar>) =
    match row[name] with
    | Text t -> t
    | Flag f -> string f
    | Number n -> string n

let private at (minutes: float) = start.AddMinutes minutes

let private chooseForTimer =
    [ start, ui "timerActivityType" activityType
      start, ui "timerProject" project
      start, ui "timerDescription" "Product research" ]

// ---- startup, routes and the environment --------------------------------------

[<Fact>]
let ``startup routes from the fragment and asks the browser for its time zone`` () =
    let model, effects = update (ctxAt start) (Started "#/more") fresh
    Assert.Equal(More, model.Route.Screen)
    Assert.Equal<Effect list>([ DescribeEnvironment ], effects)
    let described, _ = update (ctxAt start) (EnvironmentDescribed "America/New_York") model
    Assert.Equal("America/New_York", textOf "zoneId" described)
    // An unknown zone falls back to UTC, visibly.
    let unknown, _ = update (ctxAt start) (EnvironmentDescribed "Mars/Olympus_Mons") model
    Assert.Equal("UTC", textOf "zoneId" unknown)

[<Fact>]
let ``navigation is a request; the route changes when the location does`` () =
    let model, effects = update (ctxAt start) (keyed "navigate" "today" "") ready
    Assert.Equal(Track, model.Route.Screen)
    Assert.Equal<Effect list>([ Navigate "#/today" ], effects)
    Assert.Equal<Effect list>([ Navigate "#/more" ], snd (update (ctxAt start) (ui "goMore" "") ready))
    let moved, _ = update (ctxAt start) (LocationMoved "#/today/2026-10-01") ready
    Assert.Equal({ Screen = Today; Date = Some(DateOnly(2026, 10, 1)) }, moved.Route)
    Assert.Equal<Effect list>([ Navigate "#/today/2026-09-30" ], snd (update (ctxAt start) (ui "previousDay" "") moved))
    Assert.Equal<Effect list>([ Navigate "#/today/2026-10-02" ], snd (update (ctxAt start) (ui "nextDay" "") moved))

[<Fact>]
let ``routes round-trip and anything unknown lands on Today`` () =
    for route in [ { Screen = Today; Date = None }; { Screen = Today; Date = Some(DateOnly(2026, 2, 3)) }; { Screen = Track; Date = None }; { Screen = More; Date = None } ] do
        Assert.Equal(route, Routes.parse (Routes.hash route))

    Assert.Equal({ Screen = Today; Date = None }, Routes.parse "#/nowhere")
    Assert.Equal({ Screen = Today; Date = None }, Routes.parse "")

[<Fact>]
let ``an event name the engine does not define is a defect, never ignored`` () =
    Assert.Throws<ArgumentException>(fun () -> update (ctxAt start) (ui "fly" "") ready |> ignore) |> ignore

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
    Assert.Equal<Effect list>([ Wake(1, TickMs) ], effects)
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
    Assert.Equal<Effect list>([ Wake(2, TickMs) ], effects)
    Assert.Equal<Effect list>([], snd (update (ctxAt (at 21.0)) (Ticked 1) resumed))
    Assert.Equal("Paused", textOf "timerStateText" paused)

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
    | [ Store request ] ->
        Assert.Equal<Activity.Activity list>([ activity ], request.Activities)
        Assert.Equal<string list>([ request.CommitId ], saved.Store.Pending)
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

let private manualDraft (date: string) =
    [ start, ui "manualActivityType" activityType
      start, ui "manualProject" project
      start, ui "manualStartDate" date
      start, ui "manualStartTime" "09:00"
      start, ui "manualEndTime" "10:15"
      start, ui "manualDescription" "Visual research"
      start, ui "manualPurpose" "Mobile patterns" ]

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

// ---- the store port -----------------------------------------------------------------

[<Fact>]
let ``store answers other than Committed stay visible until the next success`` () =
    let pending, _ = play (manualDraft "2026-10-08" @ [ start, ui "saveManual" "" ]) ready
    let id = pending.Store.Pending.Head

    for outcome, headline in
        [ Conflict "changed on another device", "Not saved: changed elsewhere"
          Failed "offline", "Not saved"
          OutcomeUnknown "connection lost", "Save outcome unknown" ] do
        let answered, _ = update (ctxAt start) (StoreAnswered(id, outcome)) pending
        Assert.Equal(headline, textOf "storeHeadline" answered)
        Assert.Equal("attention", textOf "storeTone" answered)

    let durable = { pending with Store = { pending.Store with Kind = Durable "GitHub" } }
    Assert.Equal("All changes saved", textOf "storeHeadline" (fst (update (ctxAt start) (StoreAnswered(id, Committed)) durable)))

// ---- formatting -----------------------------------------------------------------------

[<Fact>]
let ``times, durations and dates read the way the legacy application wrote them`` () =
    Assert.Equal<string list>([ "0m"; "52m"; "1h 46m"; "3h" ], [ 0; 52; 106; 180 ] |> List.map Format.minutes)
    Assert.Equal("4.3h", Format.decimalHours 258)
    Assert.Equal("9:18 AM", Format.clock (TimeOnly(9, 18)))
    Assert.Equal("Thursday, October 8", Format.longDate (DateOnly(2026, 10, 8)))
    Assert.Equal("01:02:03", Format.elapsed (TimeSpan(1, 2, 3)))
    Assert.Equal("1 hour, 2 minutes and 3 seconds", Format.elapsedSpoken (TimeSpan(1, 2, 3)))
    Assert.Equal("0 seconds", Format.elapsedSpoken TimeSpan.Zero)
    Assert.Equal("KM", Format.initials "Kevin Miller")
    Assert.Equal(None, Format.parseTime "09:00:30")
    Assert.Equal(None, Format.parseIsoDate "10/08/2026")

// ---- lifecycle, merge, review and month (WI-0047) ------------------------------------

let private withThree =
    let entry (start: string) (finish: string) (description: string) =
        [ at 0.0, ui "manualActivityType" activityType
          at 0.0, ui "manualProject" project
          at 0.0, ui "manualStartDate" "2026-10-08"
          at 0.0, ui "manualStartTime" start
          at 0.0, ui "manualEndTime" finish
          at 0.0, ui "manualDescription" description
          at 0.0, ui "manualPurpose" "Delivery"
          at 0.0, ui "saveManual" "" ]

    play (entry "09:00" "10:00" "First" @ entry "10:00" "10:30" "Second" @ entry "11:00" "11:30" "Third") ready |> fst |> settle

let private activityId (description: string) (model: Model) =
    model.Ledger.Activities |> Map.toList |> List.map snd |> List.find (fun a -> a.Classification.Description = description) |> _.ActivityId

let private opened (description: string) (model: Model) =
    fst (update (ctxAt start) (LocationMoved $"#/activity/{activityId description model}") model)

[<Fact>]
let ``new screens have addresses: month, review and an activity`` () =
    for route in
        [ { Screen = Month; Date = Some(DateOnly(2026, 10, 1)) }
          { Screen = DayReview; Date = Some(DateOnly(2026, 10, 8)) }
          { Screen = ActivityDetail "ACT-1"; Date = None } ] do
        Assert.Equal(route, Routes.parse (Routes.hash route))

    Assert.Equal<Effect list>([ Navigate "#/activity/ACT-7" ], snd (update (ctxAt start) (keyed "openActivity" "ACT-7" "") ready))
    Assert.Equal<Effect list>([ Navigate "#/review/2026-10-08" ], snd (update (ctxAt start) (ui "openReview" "") ready))
    let october, _ = update (ctxAt start) (LocationMoved "#/month/2026-10") ready
    Assert.Equal<Effect list>([ Navigate "#/month/2026-09" ], snd (update (ctxAt start) (ui "previousMonth" "") october))
    Assert.Equal<Effect list>([ Navigate "#/month/2027-01" ], snd (update (ctxAt start) (ui "showMonth" "2027-01") october))

[<Fact>]
let ``an activity's detail opens at its revision and follows the route`` () =
    let model = opened "First" withThree
    Assert.Equal(Some 1, model.Detail |> Option.map _.Revision)
    Assert.Equal("First", textOf "detailTitle" model)
    Assert.Equal("9:00 AM – 10:00 AM", textOf "detailTimes" model)
    Assert.True(flagOf "detailCanSplit" model)
    let left, _ = update (ctxAt start) (LocationMoved "#/today") model
    Assert.True(left.Detail.IsNone)
    let missing, _ = update (ctxAt start) (LocationMoved "#/activity/nope") model
    Assert.True(flagOf "detailMissing" missing)

[<Fact>]
let ``a correction keeps the original in the history and is revalidated`` () =
    let model = opened "First" withThree
    let refused, _ = play [ start, ui "amendPurpose" ""; start, ui "saveAmend" "" ] model
    Assert.Equal<Diagnostic list>([ MissingField "businessPurpose" ], refused.Problems[AmendForm])

    let amended, effects = play [ start, ui "amendDescription" "First, corrected"; start, ui "amendReason" "Typo"; start, ui "saveAmend" "" ] model
    let a = amended.Ledger.Activities[activityId "First, corrected" amended]
    Assert.Equal(2, a.Revision)
    Assert.Equal(Some 2, amended.Detail |> Option.map _.Revision)
    Assert.Equal<string list>([ "Amended"; "Recorded" ], itemsOf "detailHistory" amended |> List.map (field "action"))
    Assert.Equal("Reason: Typo", itemsOf "detailHistory" amended |> List.head |> field "reason")
    Assert.True(effects |> List.exists (function Store r -> r.Activities = [ a ] | _ -> false))

[<Fact>]
let ``a command against a revision that changed since the detail opened is a conflict`` () =
    let model = opened "First" withThree
    let id = activityId "First" model
    let zone = model.Zone.Value

    let context: Ledger.CommandContext =
        { Performer = model.Session.ActorId; At = start; Source = "elsewhere"; Zone = zone; References = model.References; CorrelationId = None }

    // Another device changes the record after this screen opened it.
    let elsewhere = Ledger.execute context model.Ledger (Ledger.Void(id, 1, "duplicate")) |> Result.defaultWith (fun e -> failwith $"{e}")
    let stale = { model with Ledger = elsewhere }
    let refused, _ = update (ctxAt start) (ui "saveAmend" "") stale
    Assert.Equal<Diagnostic list>([ RevisionConflict(1, 2) ], refused.Problems[AmendForm])
    Assert.Equal<string list>([ "This changed since you opened it. Review the current version and try again." ], itemsOf "amendProblems" refused |> List.map (field "text"))

[<Fact>]
let ``removing from totals keeps the record; restoring rechecks it`` () =
    let model = opened "First" withThree
    let voided, _ = play [ start, ui "voidReason" "Duplicate"; start, ui "voidActivity" "" ] model
    Assert.True(flagOf "detailVoided" voided)
    Assert.Equal("1h", textOf "dayTotal" voided)
    let today = itemsOf "dayRecords" voided |> List.find (fun r -> field "title" r = "First")
    Assert.Equal("Removed from totals", field "state" today)
    let restored, _ = update (ctxAt start) (ui "restoreActivity" "") voided
    Assert.True(flagOf "detailRecorded" restored)
    Assert.Equal("2h", textOf "dayTotal" restored)

[<Fact>]
let ``evidence links with a label and a web address, and unlinks`` () =
    let model = opened "First" withThree
    let bad, _ = play [ start, ui "evidenceUrl" "javascript:alert(1)"; start, ui "attachEvidence" "" ] model
    Assert.Equal<Diagnostic list>([ MissingField "evidenceLabel"; InvalidEvidenceUrl "javascript:alert(1)" ], bad.Problems[EvidenceForm])
    let linked, _ = play [ start, ui "evidenceUrl" "https://example.test/pr/4"; start, ui "evidenceKind" "pull-request"; start, ui "evidenceLabel" "PR 4"; start, ui "attachEvidence" "" ] model
    let evidence = itemsOf "detailEvidence" linked |> List.exactlyOne
    Assert.Equal("Pull request", field "kind" evidence)
    let unlinked, _ = update (ctxAt start) (keyed "unlinkEvidence" (field "id" evidence) "") linked
    Assert.Empty(itemsOf "detailEvidence" unlinked)
    Assert.Equal("Evidence unlinked", itemsOf "detailHistory" unlinked |> List.head |> field "action")

[<Fact>]
let ``a split shares the exact minutes and assigns each piece of evidence once`` () =
    let linked, _ = play [ start, ui "evidenceLabel" "Commit"; start, ui "attachEvidence" "" ] (opened "First" withThree)
    let evidenceId = linked.Ledger.Activities[linked.Detail.Value.ActivityId].Evidence.Head.Id
    let refused, _ = play [ start, ui "splitFirst" "20"; start, ui "splitSecond" "20"; start, ui "saveSplit" "" ] linked
    Assert.Equal<Diagnostic list>([ SplitDurationMismatch(60, 40) ], refused.Problems[SplitForm])
    let split, _ = play [ start, ui "splitSecond" "40"; start, keyed "splitEvidence" evidenceId "2"; start, ui "saveSplit" "" ] refused
    Assert.True(flagOf "detailSuperseded" split)
    let source = split.Detail.Value.ActivityId
    let children = split.Ledger.Activities |> Map.toList |> List.map snd |> List.filter (fun a -> a.Lineage = [ source ])
    Assert.Equal<int list>([ 20; 40 ], children |> List.map _.Minutes |> List.sort)
    Assert.Equal<int list>([ 0; 1 ], children |> List.sortBy _.Minutes |> List.map (fun c -> c.Evidence.Length))
    Assert.StartsWith("Replaced by \"First\" (20m), \"First\" (40m)", textOf "detailReplacedBy" split)

[<Fact>]
let ``only adjacent activities merge; the originals are kept, superseded`` () =
    let first, second, third = activityId "First" withThree, activityId "Second" withThree, activityId "Third" withThree
    let gap, _ = play [ start, check "mergeSelect" first true; start, check "mergeSelect" third true; start, ui "saveMerge" "" ] withThree
    Assert.True(flagOf "canMerge" gap)
    Assert.Equal<Diagnostic list>([ IncompatibleMergeSources "sources are not contiguous" ], gap.Problems[MergeForm])

    let merged, _ =
        play [ start, check "mergeSelect" third false; start, check "mergeSelect" second true; start, ui "mergeDescription" "First and second"; start, ui "saveMerge" "" ] gap

    Assert.Empty merged.MergeSelection
    Assert.Equal("2h", textOf "dayTotal" merged)
    Assert.Equal<string list>([ "First and second"; "Third" ], itemsOf "dayRecords" merged |> List.map (field "title"))
    Assert.Equal("Merged 2 activities into one. The originals are kept, superseded.", merged.Announcement)

[<Fact>]
let ``attestation needs a statement, keeps every earlier one, and a later change is an obligation`` () =
    let review, _ = update (ctxAt start) (LocationMoved "#/review/2026-10-08") withThree
    let refused, _ = update (ctxAt start) (ui "attestDay" "") review
    Assert.Equal<Diagnostic list>([ MissingField "statement" ], refused.Problems[AttestForm])

    let attested, effects = play [ start, ui "attestStatement" "Complete."; start, ui "attestDay" "" ] review
    Assert.True(effects |> List.exists (function Store r -> r.Attestations.Length = 1 | _ -> false))
    Assert.Equal<string list>([ "Complete." ], itemsOf "attestations" attested |> List.map (field "statement"))
    Assert.False(flagOf "hasObligations" attested)

    let second = activityId "Second" attested
    let changed, _ = play [ start, LocationMoved $"#/activity/{second}"; start, ui "amendReason" "Late fix"; start, ui "amendDescription" "Second, fixed"; start, ui "saveAmend" "" ] attested
    let obligations = itemsOf "obligations" changed
    Assert.Equal<string list>([ "Thursday, October 8 changed after you attested it" ], obligations |> List.map (field "title"))
    Assert.Equal<Effect list>([ Navigate "#/review/2026-10-08" ], snd (update (ctxAt start) (keyed "resolveObligation" (field "id" obligations.Head) "") changed))

    let again, _ = play [ start, LocationMoved "#/review/2026-10-08"; start, ui "attestStatement" "Rechecked."; start, ui "attestDay" "" ] changed
    Assert.Equal<string list>([ "Rechecked."; "Complete." ], itemsOf "attestations" again |> List.map (field "statement"))
    Assert.False(flagOf "hasObligations" again)

[<Fact>]
let ``a held timer is an obligation until it is completed`` () =
    let held, _ = play (chooseForTimer @ [ start, ui "startTimer" ""; at 5.0, ui "stopTimer" "" ]) ready
    Assert.Equal<string list>([ "stopped-timer" ], itemsOf "obligations" held |> List.map (field "id"))
    Assert.Equal<Effect list>([ Navigate "#/track" ], snd (update (ctxAt start) (keyed "resolveObligation" "stopped-timer" "") held))

[<Fact>]
let ``the month sums effective records by type and by day`` () =
    let model, _ = update (ctxAt start) (LocationMoved "#/month/2026-10") withThree
    Assert.Equal("October 2026", textOf "monthTitle" model)
    Assert.Equal("2h", textOf "monthTotal" model)
    Assert.Equal("1", textOf "monthActiveDays" model)
    let byType = itemsOf "monthByType" model |> List.exactlyOne
    Assert.Equal("Research", field "name" byType)
    Assert.Equal("100%", field "shareText" byType)
    Assert.Equal<string list>([ "Thursday, October 8" ], itemsOf "monthDays" model |> List.map (field "label"))
    let september, _ = update (ctxAt start) (LocationMoved "#/month/2026-09") withThree
    Assert.True(flagOf "monthEmpty" september)

// ---- timesheet periods (WI-0048) -------------------------------------------------------

[<Fact>]
let ``Today shows the configured period around the selected day`` () =
    Assert.Equal("Oct 5 – Oct 11", textOf "periodLabel" withThree)
    Assert.Equal("2h", textOf "periodExact" withThree)
    Assert.Equal("2h", textOf "periodBillable" withThree)
    Assert.Equal("Not submitted", textOf "periodSubmission" withThree)
    Assert.Equal("Not required", textOf "periodApproval" withThree)
    Assert.Equal("America/New_York", withThree.PeriodConfig.ZoneId)

    let sunday, _ = update (ctxAt start) (ui "periodWeekStart" "Sunday") withThree
    Assert.Equal("Oct 4 – Oct 10", textOf "periodLabel" sunday)
    let monthly, _ = update (ctxAt start) (ui "periodCadence" "monthly") withThree
    Assert.Equal("Oct 1 – Oct 31", textOf "periodLabel" monthly)
    let daily, _ = update (ctxAt start) (ui "periodCadence" "daily") withThree
    Assert.Equal("Thursday, October 8", textOf "periodLabel" daily)
    let fortnight, _ = update (ctxAt start) (ui "periodCadence" "biweekly") withThree
    Assert.Equal("Oct 5 – Oct 18", textOf "periodLabel" fortnight)
    Assert.Equal<string list>([ "biweekly" ], itemsOf "periodCadenceOptions" fortnight |> List.filter (fun r -> field "selected" r = "True") |> List.map (field "id"))
