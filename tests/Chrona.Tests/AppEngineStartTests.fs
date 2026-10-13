/// The engine starting: routes and the environment, the store port,
/// formatting, and the page coming back with a newer Chrona (37, WI-0063).
[<Xunit.Collection("AppEngine")>]
module Chrona.Tests.AppEngineStartTests

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

// ---- startup, routes and the environment --------------------------------------

[<Fact>]
let ``startup routes from the fragment and asks the browser for its time zone`` () =
    let model, effects = update (ctxAt start) (Started(Support.testPage, "#/more", [])) fresh
    Assert.Equal(Places.Settings None, model.Place)
    Assert.Equal(Some "/more", model.Router.Current)
    // Opening an address adds no history entry: it is canonical, so nothing moves.
    Assert.Equal<Effect list>([ DescribeEnvironment; ReadConfiguration ], effects)
    let model, _ = update (ctxAt start) (ConfigurationRead(Some localConfiguration)) model
    let described, _ = update (ctxAt start) (EnvironmentDescribed "America/New_York") model
    Assert.Equal("America/New_York", textOf "zoneId" described)
    // An unknown zone falls back to UTC, visibly.
    let unknown, _ = update (ctxAt start) (EnvironmentDescribed "Mars/Olympus_Mons") model
    Assert.Equal("UTC", textOf "zoneId" unknown)

[<Fact>]
let ``going somewhere adds a history entry; refining a day replaces it; Back and Forward are adopted`` () =
    let model, effects = update (ctxAt start) (ui "goToday" "") ready
    // The engine moves at once and asks the browser to follow.
    Assert.Equal(Places.Today, model.Place)
    Assert.Equal<Effect list>([ push "/" ], effects)
    Assert.Equal<Effect list>([ push "/more" ], snd (update (ctxAt start) (ui "goMore" "") ready))
    // Going where the page already is adds nothing.
    Assert.Equal<Effect list>([], snd (update (ctxAt start) (ui "goTrack" "") ready))
    // Back and Forward (or a link) report a new address: adopted, never pushed.
    let moved, effects = update (ctxAt start) (LocationMoved "#/day/2026-10-01") ready
    Assert.Equal(Places.Day(DateOnly(2026, 10, 1), None), moved.Place)
    Assert.Equal<Effect list>([], effects)
    Assert.Equal<Effect list>([ replace "/day/2026-09-30" ], snd (update (ctxAt start) (ui "previousDay" "") moved))
    Assert.Equal<Effect list>([ replace "/day/2026-10-02" ], snd (update (ctxAt start) (ui "nextDay" "") moved))

[<Fact>]
let ``an old or non-canonical address opens its place and is corrected in place`` () =
    let moved, effects = update (ctxAt start) (LocationMoved "#/today/2026-10-01") ready
    Assert.Equal(Places.Day(DateOnly(2026, 10, 1), None), moved.Place)
    Assert.Equal<Effect list>([ replace "/day/2026-10-01" ], effects)
    let reports, effects = update (ctxAt start) (LocationMoved "#/reports?format=csv&q=pairing&utm=x") ready
    Assert.Equal(Places.Reports { Places.allReports with Text = Some "pairing" }, reports.Place)
    Assert.Equal<Effect list>([ replace "/reports?q=pairing" ], effects)
    Assert.Equal("pairing", reports.Report.Text)

[<Fact>]
let ``an address that names nothing shows the not-found page, never another place`` () =
    for address, title in
        [ "#/nowhere", "Page not found"
          "#/day/2026-02-30", "Page not found"
          "#/reports?group=colour", "Page not found"
          "#/entries/ACT-none?on=2026-10-08", "Not found"
          "#/day/2026-10-08?project=PRJ-none", "Not found" ] do
        let model, effects = update (ctxAt start) (LocationMoved address) ready
        Assert.True(flagOf "screenProblem" model, address)
        Assert.Equal(title, textOf "problemTitle" model)
        Assert.Equal<Effect list>([], effects)

        for screen in [ "screenToday"; "screenTrack"; "screenMonth"; "screenMore"; "screenActivity"; "screenReview"; "screenReports" ] do
            Assert.False(flagOf screen model, $"{address} showed {screen}")

        // Nothing to copy a link to.
        Assert.False(flagOf "canCopyLink" model)

    let invalid, _ = update (ctxAt start) (LocationMoved "#/day/2026-02-30") ready
    Assert.Equal("This address is not one of Chrona's pages: its on \"2026-02-30\" should be a date such as 2026-10-08.", textOf "problemDetail" invalid)
    // Moving on clears it.
    let back, _ = update (ctxAt start) (LocationMoved "#/track") invalid
    Assert.False(flagOf "screenProblem" back)
    Assert.True(flagOf "screenTrack" back)

[<Fact>]
let ``copy link copies an absolute link that opens the same view on another day`` () =
    let _, effects = update (ctxAt start) (ui "copyLink" "") ready
    Assert.Equal<Effect list>([ CopyText(LinkCopy, "http://127.0.0.1:4321/web/index.html#/track") ], effects)
    // Today, this month and today's review are made explicit.
    let today, _ = update (ctxAt start) (LocationMoved "#/") ready
    let copying, effects = update (ctxAt start) (ui "copyLink" "") today
    Assert.Equal<Effect list>([ CopyText(LinkCopy, "http://127.0.0.1:4321/web/index.html#/day/2026-10-08") ], effects)
    let month, _ = update (ctxAt start) (LocationMoved "#/month") ready
    Assert.Equal<Effect list>([ CopyText(LinkCopy, "http://127.0.0.1:4321/web/index.html#/month/2026-10") ], snd (update (ctxAt start) (ui "copyLink" "") month))
    let copied, _ = update (ctxAt start) (Copied(LinkCopy, true)) copying
    Assert.Equal("Link copied.", textOf "linkStatus" copied)
    Assert.False(flagOf "linkRefused" copied)
    // A browser that will not copy: the link is shown to select by hand.
    let refused, _ = update (ctxAt start) (Copied(LinkCopy, false)) copying
    Assert.True(flagOf "linkRefused" refused)
    Assert.Equal("http://127.0.0.1:4321/web/index.html#/day/2026-10-08", textOf "linkText" refused)
    // Moving elsewhere forgets it.
    let moved, _ = update (ctxAt start) (LocationMoved "#/more") refused
    Assert.False(flagOf "hasLinkStatus" moved)

[<Fact>]
let ``an event name the engine does not define is a defect, never ignored`` () =
    Assert.Throws<ArgumentException>(fun () -> update (ctxAt start) (ui "fly" "") ready |> ignore) |> ignore

// ---- the store port -----------------------------------------------------------------

[<Fact>]
let ``store answers other than Committed stay visible until the next success`` () =
    let pending, _ = play (manualDraft "2026-10-08" @ [ start, ui "saveManual" "" ]) ready
    let id = pending.Store.Pending.Head.CommitId

    for outcome, headline in
        [ Conflict [ Reconcile.KeptChanging ], "Not saved: changed elsewhere"
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

// ---- the page coming back and a newer Chrona (37, WI-0063) ------------------------------

[<Fact>]
let ``a page coming back restarts the timer's wake-ups from its timestamps and sends what waits`` () =
    let running, _ = play (chooseForTimer @ [ start, ui "startTimer" "" ]) ready
    let back, effects = update (ctxAt (at 30.0)) PageReturned running
    // A new run of wake-ups; the one from before the page went away is ignored.
    Assert.Equal<Effect list>([ Wake(running.TickGeneration + 1, TickMs) ], effects)
    Assert.Equal<Effect list>([], snd (update (ctxAt (at 31.0)) (Ticked running.TickGeneration) back))
    Assert.Equal("00:30:00", textOf "timerElapsed" back)

    // Unsent changes are sent; the browser saying it is offline is shown.
    let pending, _ = play (manualDraft "2026-10-08" @ [ start, ui "saveManual" "" ]) ready
    Assert.Contains(SendUnsent, snd (update (ctxAt start) PageReturned pending))
    Assert.Equal<Effect list>([ SendUnsent ], snd (update (ctxAt start) (ConnectionChanged true) pending))
    let offline, _ = update (ctxAt start) (ConnectionChanged false) pending
    Assert.True(offline.Store.Sync.Offline)

    // Another tab held the unsent changes: returning asks for them again,
    // before anything is sent, so what that tab kept is sent with them (WI-0059).
    let elsewhere, _ = update (ctxAt start) (SyncChanged { pending.Store.Sync with Holder = HeldElsewhere false }) pending
    let _, effects = update (ctxAt start) PageReturned elsewhere
    Assert.Equal(Some 0, effects |> List.tryFindIndex ((=) ClaimQueue))
    Assert.True(effects |> List.findIndex ((=) ClaimQueue) < (effects |> List.findIndex ((=) SendUnsent)))
    Assert.DoesNotContain(ClaimQueue, snd (update (ctxAt start) PageReturned pending))

    // Taken over by another tab: what this tab kept is that tab's to send.
    let handed, _ = update (ctxAt start) (UnsentHandedOver(pending.Store.Pending |> List.map _.CommitId)) elsewhere
    Assert.Empty(handed.Store.Pending)
    Assert.Equal("Your unsent changes moved to the other Chrona tab, which now sends them.", handed.Announcement)

    // Shown from the read cache: back online, or returning to the page,
    // reads the records from GitHub again (WI-0057).
    let cached, _ =
        update
            (ctxAt start)
            (StoreOpened
                { Name = "acme/chrona-data"
                  Cached = Some start
                  Activities = []
                  References = []
                  Attestations = []
                  Members = []
                  Held = []
                  Audit = []
                  Problems = []
                  Months = []
                  Others = []
                  History = []
                  Index = ""
                  IndexCurrent = false
                  Periods = None
                  Reviews = []
                  Reviewing = []
                  Candidates = [] })
            ready

    Assert.Equal(Some start, cached.Store.Cached)
    Assert.Equal<Effect list>([ SendUnsent ], snd (update (ctxAt start) (ConnectionChanged true) cached))
    Assert.Contains(SendUnsent, snd (update (ctxAt start) PageReturned cached))
    Assert.StartsWith("Offline: your records as of", textOf "storeHeadline" cached)

    // A deployment that stores nothing never has unsent changes to keep.
    Assert.Equal(InMemory, ready.Store.Kind)
    Assert.Equal("None", textOf "queueMode" ready)

    // Records that cannot open still say what the queue's move did.
    let waiting, _ = update (ctxAt start) (UnsentWaiting(1, Some "moved")) ready
    Assert.Equal(1, waiting.Store.Waiting)
    Assert.Equal(Some "moved", waiting.Store.Sync.Notice)

[<Fact>]
let ``only a page built for a deployment compares builds, and a reload never loses what lives only in the page`` () =
    let dev, effects = update (ctxAt start) (BuildKnown Model.Development) ready
    Assert.Empty effects
    Assert.Equal(None, (fst (update (ctxAt start) (ShellChecked(Some "anything")) dev)).Shell.Newer)

    let built, effects = update (ctxAt start) (BuildKnown "a1b2c3") ready
    Assert.Equal<Effect list>([ CheckShell ], effects)
    Assert.Contains(CheckShell, snd (update (ctxAt start) PageReturned built))
    Assert.Equal(None, (fst (update (ctxAt start) (ShellChecked(Some "a1b2c3")) built)).Shell.Newer)
    Assert.Equal(None, (fst (update (ctxAt start) (ShellChecked None) built)).Shell.Newer)

    let newer, _ = update (ctxAt start) (ShellChecked(Some "d4e5f6")) built
    Assert.True(flagOf "shellUpdate" newer)
    Assert.Equal<Effect list>([ ReloadPage ], snd (update (ctxAt start) (ui "reloadShell" "") newer))

    // A change kept only in this page (a conflict) holds the reload back.
    let request = { emptyRequest "COMMIT-x" with Activities = [] }
    let conflicted = { newer with Store = { newer.Store with Conflicts = [ { Id = "COMMIT-x"; Request = request; Divergences = [ Reconcile.KeptChanging ] } ] } }
    Assert.True(flagOf "shellReloadBlocked" conflicted)
    Assert.Empty(snd (update (ctxAt start) (ui "reloadShell" "") conflicted))

[<Fact>]
let ``the day's ledger is filtered by project through its address`` () =
    let filtered, effects = update (ctxAt start) (ui "dayProject" project) withThree
    Assert.Equal<Effect list>([ replace $"/day/2026-10-08?project={project}" ], effects)
    Assert.Equal<string list>([ "First"; "Second"; "Third" ], itemsOf "dayRecords" filtered |> List.map (field "title"))
    Assert.Equal("HelixNote", textOf "dayFilterName" filtered)
    let other, _ = update (ctxAt start) (ui "newProjectName" "Other") filtered
    let other, _ = update (ctxAt start) (ui "addProject" "") other
    let otherId = (Reference.all Reference.Project other.References |> List.find (fun item -> item.Name = "Other")).Id
    let none, _ = update (ctxAt start) (ui "dayProject" otherId) (settle other)
    Assert.True(flagOf "dayFilteredEmpty" none)
    Assert.Empty(itemsOf "dayRecords" none)
    // The filter survives moving between days, and "All projects" clears it.
    Assert.Equal<Effect list>([ replace $"/day/2026-10-07?project={otherId}" ], snd (update (ctxAt start) (ui "previousDay" "") none))
    Assert.Equal<Effect list>([ replace "/day/2026-10-08" ], snd (update (ctxAt start) (ui "dayProject" "") none))
