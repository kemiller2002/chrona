/// One browser, several tabs: one tab holds the unsent changes (WI-0067,
/// WI-0059), and an older Chrona's queue moves into IndexedDB (Limen
/// LCP-065).
[<Xunit.Collection("Store")>]
module Chrona.Tests.StoreTabTests

open System
open Xunit
open Arca
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update
open Chrona.Application
open Chrona.Tests.StoreBrowserFixture
open Chrona.Tests.StoreDeviceFixture
open Chrona.Tests.StoreFixtures

// ---- One tab holds this browser's unsent changes (WI-0067, WI-0059) ---------

[<Fact>]
let ``two tabs in one browser: only the tab holding the unsent changes keeps them in IndexedDB; the other says so and sends its own from the page`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let first = Device(github, RepositoryVisibility.Private, "production", browser)
    let second = Device(github, RepositoryVisibility.Private, "production", browser)
    first.Open()
    record first "08:00" "08:30" "Setup"
    second.Open()
    let commits = github.State.History.Length

    Assert.Equal(HeldHere, first.Model.Store.Sync.Holder)
    Assert.Equal(InIndexedDb, first.Model.Store.Sync.Durability)
    Assert.Equal(HeldElsewhere false, second.Model.Store.Sync.Holder)
    Assert.False(viewFlag "queueElsewhere" first.Model)
    Assert.True(viewFlag "queueElsewhere" second.Model)
    Assert.Equal("Held by another Chrona tab", viewText "queueMode" second.Model)
    Assert.Equal("Kept in this browser (IndexedDB) by this tab", viewText "queueMode" first.Model)

    // Online, the second tab still writes directly (OQ-LIMEN-IDB-001).
    record second "07:00" "07:30" "Standup"
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(queued browser)

    // Offline, only the holder keeps its change in the browser; the other
    // tab keeps its own in the page, and says where they are.
    first.Offline <- true
    second.Offline <- true
    record first "09:00" "10:00" "Pairing"
    record second "11:00" "12:00" "Review"
    Assert.Equal(1, (inIndexedDb browser).Length)
    Assert.Empty(inLocalStorage browser)
    Assert.Equal(1, second.Model.Store.Pending.Length)
    Assert.StartsWith("Another tab is holding your unsent changes", viewText "storeDetail" second.Model)

    // Back online, each change is sent once, by the tab that holds it.
    first.Offline <- false
    second.Offline <- false
    first.Wake()
    second.Wake()
    Assert.Equal(commits + 3, github.State.History.Length)
    Assert.Empty(queued browser)
    Assert.Empty(first.Model.Store.Pending)
    Assert.Empty(second.Model.Store.Pending)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Pairing"; "Review"; "Setup"; "Standup" ], descriptions reader)

[<Fact>]
let ``"use this tab instead" takes the unsent changes over at once; the other tab's next save is fenced, and each change is sent once`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let first = Device(github, RepositoryVisibility.Private, "production", browser)
    let second = Device(github, RepositoryVisibility.Private, "production", browser)
    first.Open()
    record first "08:00" "08:30" "Setup"
    second.Open()
    let commits = github.State.History.Length

    first.Offline <- true
    second.Offline <- true
    record first "09:00" "10:00" "Pairing"
    record second "11:00" "12:00" "Review"

    // The second tab takes them over: it reads the first tab's change back,
    // keeps its own after it, and shows both as unsent.
    second.Ui("takeOverQueue", "")
    Assert.Equal(HeldHere, second.Model.Store.Sync.Holder)
    Assert.True(second.Model.Store.Sync.KeptInBrowser)
    Assert.Equal(2, (inIndexedDb browser).Length)
    Assert.Equal(2, second.Model.Store.Pending.Length)
    Assert.Equal("This tab now holds your unsent changes and sends them.", second.Model.Announcement)
    Assert.Equal<string list>([ "Pairing"; "Review"; "Setup" ], descriptions second)

    // Before the first tab hears that it lost them, it saves once more: the
    // save is fenced and writes nothing. What it kept is the second tab's
    // now; the change it just made stays in its page.
    record first "13:00" "14:00" "Planning"
    Assert.Equal(HeldElsewhere false, first.Model.Store.Sync.Holder)
    Assert.Equal(2, (inIndexedDb browser).Length)
    Assert.Equal<string list>([ "Planning" ], first.Model.Store.Pending |> List.collect (fun request -> request.Activities |> List.map _.Classification.Description))
    Assert.Equal("Your unsent changes moved to the other Chrona tab, which now sends them.", first.Model.Announcement)
    browser.DeliverLockLost()
    Assert.Equal(HeldElsewhere false, first.Model.Store.Sync.Holder)

    // Back online, every change is sent once.
    first.Offline <- false
    second.Offline <- false
    second.Wake()
    first.Wake()
    Assert.Equal(commits + 3, github.State.History.Length)
    Assert.Empty(queued browser)
    Assert.Empty(first.Model.Store.Pending)
    Assert.Empty(second.Model.Store.Pending)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Pairing"; "Planning"; "Review"; "Setup" ], descriptions reader)

[<Fact>]
let ``a tab that hears its unsent changes were taken over hands them to the other tab, and sends none of them itself`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let first = Device(github, RepositoryVisibility.Private, "production", browser)
    let second = Device(github, RepositoryVisibility.Private, "production", browser)
    first.Open()
    record first "08:00" "08:30" "Setup"
    second.Open()
    let commits = github.State.History.Length

    first.Offline <- true
    second.Offline <- true
    record first "09:00" "10:00" "Pairing"
    second.Ui("takeOverQueue", "")
    browser.DeliverLockLost()
    Assert.Equal(HeldElsewhere false, first.Model.Store.Sync.Holder)
    Assert.Empty(first.Model.Store.Pending)
    Assert.Equal(1, second.Model.Store.Pending.Length)

    first.Offline <- false
    second.Offline <- false
    first.Wake()
    Assert.Equal(commits, github.State.History.Length)
    second.Wake()
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(queued browser)

[<Fact>]
let ``when the holding tab closes, the other tab holds the unsent changes once the person returns to it, and sends each once`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let first = Device(github, RepositoryVisibility.Private, "production", browser)
    let second = Device(github, RepositoryVisibility.Private, "production", browser)
    first.Open()
    record first "08:00" "08:30" "Setup"
    second.Open()
    let commits = github.State.History.Length

    first.Offline <- true
    second.Offline <- true
    record first "09:00" "10:00" "Pairing"
    record second "11:00" "12:00" "Review"

    first.Close()
    second.Return()
    Assert.Equal(HeldHere, second.Model.Store.Sync.Holder)
    Assert.True(second.Model.Store.Sync.KeptInBrowser)
    Assert.Equal(2, (inIndexedDb browser).Length)
    Assert.Equal(2, second.Model.Store.Pending.Length)

    second.Offline <- false
    second.Wake()
    Assert.Equal(commits + 2, github.State.History.Length)
    Assert.Empty(queued browser)
    Assert.Empty(second.Model.Store.Pending)

[<Fact>]
let ``a change the holding tab was sending when the connection dropped is reconciled by the tab that takes over, not sent again`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let first = Device(github, RepositoryVisibility.Private, "production", browser)
    let second = Device(github, RepositoryVisibility.Private, "production", browser)
    first.Open()
    record first "08:00" "08:30" "Setup"
    second.Open()
    let commits = github.State.History.Length

    // It lands, but its answer is lost and GitHub is then out of reach.
    first.DisconnectDuringNextCommit()
    record first "09:00" "10:00" "Pairing"
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Equal(1, (inIndexedDb browser).Length)

    second.Ui("takeOverQueue", "")
    browser.DeliverLockLost()
    second.Wake()
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(queued browser)
    Assert.Empty(second.Model.Store.Pending)
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions second)

[<Fact>]
let ``a tab whose lock is taken stops keeping unsent changes in the browser, and says another tab holds them`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    device.LoseLock()
    Assert.Equal(HeldElsewhere false, device.Model.Store.Sync.Holder)
    Assert.False(device.Model.Store.Sync.KeptInBrowser)

    device.Offline <- true
    record device "09:00" "10:00" "Pairing"
    Assert.Empty(queued browser)
    Assert.Equal(1, device.Model.Store.Pending.Length)

[<Fact>]
let ``a tab that opens the records again goes on holding the unsent changes it holds`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    // Signed in again (another tab's sign-in, or a new session): the records
    // open again in the same page, which already holds the lock.
    device.Open()
    Assert.Equal(HeldHere, device.Model.Store.Sync.Holder)
    Assert.True(device.Model.Store.Sync.KeptInBrowser)
    device.Offline <- true
    record device "09:00" "10:00" "Pairing"
    Assert.Equal(1, (queued browser).Length)

// ---- Moving the queue into IndexedDB (WI-0059, Limen LCP-065) ---------------

/// The organization's project and activity type, made online, so that a
/// change recorded offline is one entry.
let private prepare (device: Device) =
    device.Ui("newProjectName", "HelixNote")
    device.Ui("addProject", "")
    device.Ui("newActivityTypeName", "Research")
    device.Ui("addActivityType", "")

/// A browser where an older Chrona kept unsent changes in localStorage: one
/// offline change, made by a page without the store pack, then closed.
let private olderChrona (github: InMemoryStore) (browser: Browser) (description: string) =
    browser.IndexedDb <- false
    let older = Device(github, RepositoryVisibility.Private, "production", browser)
    older.Open()
    if (Reference.selectable Reference.Project older.Model.References).IsEmpty then prepare older
    Assert.Equal(InLocalStorage, older.Model.Store.Sync.Durability)
    Assert.Equal("Kept in this browser (localStorage) by this tab", viewText "queueMode" older.Model)
    older.Offline <- true
    record older "09:00" "10:00" description
    older.Close()
    browser.IndexedDb <- true
    Assert.Equal(1, (inLocalStorage browser).Length)
    older

[<Fact>]
let ``unsent changes an older Chrona kept in localStorage move to IndexedDB on first load, unchanged, and are sent once`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let older = olderChrona github browser "Pairing"
    let commits = github.State.History.Length
    // The localStorage queue as it was, byte for byte.
    let before = browser.Storage[queueKey]

    // Opened while GitHub cannot be reached: the records do not open, but
    // the queue is read, moved, counted and the move is said.
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Offline <- true
    device.Open()
    Assert.Equal(1, device.Model.Store.Waiting)
    Assert.Equal(Some "1 unsent change kept in this browser's older storage moved to IndexedDB, unchanged.", device.Model.Store.Sync.Notice)
    Assert.True(viewFlag "hasQueueNotice" device.Model)
    Assert.Empty(inLocalStorage browser)
    Assert.Equal<string list>((outstanding before) |> List.map _.Operation.IdempotencyKey, (inIndexedDb browser) |> List.map _.Operation.IdempotencyKey)
    ignore older

    // Online, the records open and the moved change is sent once.
    device.Offline <- false
    device.Open()
    Assert.True(device.Model.Store.Sync.KeptInBrowser)
    Assert.Equal(InIndexedDb, device.Model.Store.Sync.Durability)
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(queued browser)
    Assert.Empty(device.Model.Store.Pending)

[<Fact>]
let ``a move to IndexedDB interrupted after the copy, before localStorage was cleared, finishes on the next load without sending anything twice`` () =
    let github = InMemoryStore()
    let browser = Browser()
    olderChrona github browser "Pairing" |> ignore
    let commits = github.State.History.Length
    let legacy = browser.Storage[queueKey]

    // The copy is made and verified; the page then closes before the old
    // queue is removed.
    let interrupted = Device(github, RepositoryVisibility.Private, "production", browser)
    interrupted.Offline <- true
    interrupted.Open()
    interrupted.Close()
    browser.Storage[queueKey] <- legacy
    Assert.Equal(1, (inIndexedDb browser).Length)
    Assert.Equal(1, (inLocalStorage browser).Length)

    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    Assert.Empty(inLocalStorage browser)
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(queued browser)
    Assert.Empty(device.Model.Store.Pending)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Pairing" ], descriptions reader |> List.filter ((=) "Pairing"))

[<Fact>]
let ``an older localStorage queue waits, said so, while IndexedDB holds unsent changes, and moves once they are sent; none is merged or dropped`` () =
    let github = InMemoryStore()
    let browser = Browser()
    // IndexedDB already holds an unsent change.
    let current = Device(github, RepositoryVisibility.Private, "production", browser)
    current.Open()
    prepare current
    current.Offline <- true
    record current "08:00" "08:30" "Setup"
    current.Close()
    Assert.Equal(1, (inIndexedDb browser).Length)
    // An older Chrona then left one in localStorage.
    olderChrona github browser "Pairing" |> ignore
    let commits = github.State.History.Length

    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Offline <- true
    device.Open()
    Assert.Equal(Some "1 earlier unsent change waits in this browser's older storage. They move here once the changes now kept are sent; none is merged or dropped.", device.Model.Store.Sync.Notice)
    Assert.Equal(1, (inLocalStorage browser).Length)
    Assert.Equal(1, (inIndexedDb browser).Length)

    // Sent; the older queue moves on the next load, and is sent too.
    device.Offline <- false
    device.Open()
    Assert.Equal(commits + 1, github.State.History.Length)
    device.Close()

    let next = Device(github, RepositoryVisibility.Private, "production", browser)
    next.Open()
    Assert.Equal(commits + 2, github.State.History.Length)
    Assert.Empty(queued browser)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions reader)

[<Fact>]
let ``an older localStorage queue that cannot be read is left exactly as it is, and the person is told`` () =
    let github = InMemoryStore()
    let browser = Browser()
    browser.Storage[queueKey] <- "{not a queue"

    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    Assert.True(canWork device.Model)
    Assert.Equal(Some "Unsent changes kept in this browser's older storage cannot be read. They were left exactly as they are.", device.Model.Store.Sync.Notice)
    Assert.Equal("{not a queue", browser.Storage[queueKey])

[<Fact>]
let ``when the browser clears IndexedDB under the page, the queue is opened again and what this tab holds is kept there again`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    prepare device
    device.Offline <- true
    record device "09:00" "10:00" "Pairing"
    Assert.Equal(1, (inIndexedDb browser).Length)

    device.ClearIndexedDb()
    Assert.Equal(HeldHere, device.Model.Store.Sync.Holder)
    Assert.True(device.Model.Store.Sync.KeptInBrowser)
    Assert.Equal(1, (inIndexedDb browser).Length)
    Assert.Equal(1, device.Model.Store.Pending.Length)

/// Without Web Locks no tab can be told apart from another, so no durable
/// store is ever written by several tabs at once (LCP-059): each tab keeps
/// its own changes in its page, says so, and sends them itself.
[<Fact>]
let ``without Web Locks, two tabs offline in one browser each keep their own changes in the page, and nothing is overwritten or lost`` () =
    let github = InMemoryStore()
    let browser = Browser(WebLocks = false)
    let first = Device(github, RepositoryVisibility.Private, "production", browser)
    let second = Device(github, RepositoryVisibility.Private, "production", browser)
    first.Open()
    record first "08:00" "08:30" "Setup"
    second.Open()
    let commits = github.State.History.Length

    first.Offline <- true
    second.Offline <- true
    record first "09:00" "10:00" "Pairing"
    record second "11:00" "12:00" "Review"

    Assert.Empty(queued browser)

    for tab in [ first; second ] do
        Assert.False(tab.Model.Store.Sync.KeptInBrowser)
        Assert.Equal(HeldHere, tab.Model.Store.Sync.Holder)
        Assert.Equal("In this page only", viewText "queueMode" tab.Model)
        Assert.True(viewFlag "offlineInPage" tab.Model)
        Assert.Equal(1, tab.Model.Store.Pending.Length)

    first.Offline <- false
    second.Offline <- false
    first.Wake()
    second.Wake()
    Assert.Equal(commits + 2, github.State.History.Length)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Pairing"; "Review"; "Setup" ], descriptions reader)
