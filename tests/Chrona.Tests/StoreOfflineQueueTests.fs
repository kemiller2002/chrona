/// Offline: the queue of unsent changes in this browser, sent in order on
/// reconnect, reconciled when an answer was lost (WI-0033: 21, 23, 31, 33,
/// 34).
[<Xunit.Collection("Store")>]
module Chrona.Tests.StoreOfflineQueueTests

open System
open Xunit
open Arca
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update
open Chrona.Application
open Chrona.Tests.StoreBrowserFixture
open Chrona.Tests.StoreDeviceFixture
open Chrona.Tests.StoreFixtures

// ---- Offline: the queue of unsent changes (WI-0033: 21, 23, 31, 33, 34) ---------------------

[<Fact>]
let ``scenario 33: time recorded offline waits in this browser, is shown as unsent, and is sent in order on reconnect`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    let commits = github.State.History.Length
    Assert.Empty(queued browser)

    device.Offline <- true
    record device "09:00" "10:00" "Pairing"
    record device "11:00" "12:00" "Review"

    // Nothing reached GitHub; both wait in this browser, and the page says so.
    Assert.Equal(commits, github.State.History.Length)
    Assert.Equal(2, (queued browser).Length)
    Assert.Equal(2, device.Model.Store.Pending.Length)
    Assert.True(device.Model.Store.Sync.Offline)
    Assert.Equal("Offline: 2 changes wait to be sent", headline device)
    Assert.Contains("not reached GitHub", rowText "obligations" "title" device.Model)
    Assert.Equal<string list>([ "Pairing"; "Review"; "Setup" ], descriptions device)

    // Back online, the back-off ends: both are sent, in order, one commit each.
    device.Offline <- false
    device.Wake()
    Assert.Equal(commits + 2, github.State.History.Length)
    Assert.Empty(queued browser)
    Assert.Empty(device.Model.Store.Pending)
    Assert.False(device.Model.Store.Sync.Offline)
    Assert.Equal("All changes saved", headline device)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Pairing"; "Review"; "Setup" ], descriptions reader)

[<Fact>]
let ``unsent changes survive closing the page, and are sent when the records next open`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    device.Offline <- true
    record device "09:00" "10:00" "Pairing"
    let commits = github.State.History.Length

    // The page closes while offline; later it opens again, online.
    device.Close()
    let reopened = Device(github, RepositoryVisibility.Private, "production", browser)
    reopened.Open()
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(queued browser)
    Assert.Empty(reopened.Model.Store.Pending)
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions reopened)

[<Fact>]
let ``a change whose answer was lost with the connection is reconciled before anything is sent again`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    let commits = github.State.History.Length

    // It lands, but the answer is lost and GitHub is then out of reach.
    device.DisconnectDuringNextCommit()
    record device "09:00" "10:00" "Pairing"
    Assert.Equal(commits + 1, github.State.History.Length)

    match device.Model.Store.Problem with
    | Some(OutcomeUnknown _) -> ()
    | other -> failwith $"%A{other}"

    // Reopened online, it is found to have landed: it is not sent again.
    device.Close()
    let reopened = Device(github, RepositoryVisibility.Private, "production", browser)
    reopened.Open()
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(queued browser)
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions reopened)

[<Fact>]
let ``an edit queued offline that another device changed first becomes a conflict on reconnect, neither side dropped`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let laptop = Device(github, RepositoryVisibility.Private, "production", browser)
    laptop.Open()
    record laptop "09:00" "10:00" "Pairing"
    let id = laptop.Model.Ledger.Activities |> Map.toList |> List.head |> fst
    let phone = Device(github, RepositoryVisibility.Private, "production")
    phone.Open()

    laptop.Offline <- true
    amendFrom laptop id "From the laptop, offline"
    amendFrom phone id "From the phone"
    laptop.Offline <- false
    laptop.Wake()

    match laptop.Model.Store.Conflicts with
    | [ { Divergences = [ Reconcile.ActivityChanged(mine, Some stored) ] } ] ->
        Assert.Equal("From the laptop, offline", mine.Classification.Description)
        Assert.Equal("From the phone", stored.Classification.Description)
    | other -> failwith $"%A{other}"

    Assert.Empty(queued browser)
    Assert.Equal("From the phone", laptop.Model.Ledger.Activities[id].Classification.Description)

[<Fact>]
let ``another account's unsent changes in this browser are never sent with this account's credential`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let config = configuration "production"
    let owner = Device(github, RepositoryVisibility.Private, "production", browser)
    owner.Open()
    owner.Ui("memberId", "1001")
    owner.Ui("memberName", "hubot")
    owner.Ui("memberAccess", "ownTime")
    owner.Ui("admitMember", "")
    owner.Offline <- true
    record owner "09:00" "10:00" "Pairing"
    let commits = github.State.History.Length

    // Someone else signs in on this browser: octocat's change stays unsent.
    owner.Close()
    let other = Device(github, RepositoryVisibility.Private, "production", hubot, config, browser)
    other.Open()
    Assert.True(canWork other.Model)
    Assert.Equal(commits, github.State.History.Length)
    Assert.Equal(3, (queued browser).Length)
    Assert.False(other.Model.Store.Sync.KeptInBrowser)
    Assert.Contains("Another account", other.Model.Store.Sync.Note.Value)
    Assert.True(other.Model.Ledger.Activities.IsEmpty)

    // When octocat opens the records again, it is sent.
    other.Close()
    let back = Device(github, RepositoryVisibility.Private, "production", browser)
    back.Open()
    Assert.Equal(commits + 3, github.State.History.Length)
    Assert.Empty(queued browser)

[<Fact>]
let ``without browser storage, changes are still sent, and the page says they would not survive closing it`` () =
    let github = InMemoryStore()
    let browser = Browser(Refusing = Some "unavailable", IndexedDb = false)
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    Assert.False(device.Model.Store.Sync.KeptInBrowser)
    record device "08:00" "08:30" "Setup"
    Assert.Equal(None, device.Model.Store.Problem)
    Assert.Empty(device.Model.Store.Pending)

    device.Offline <- true
    record device "09:00" "10:00" "Pairing"
    Assert.Contains("Keep this page open", (Project.project device.Model |> Map.ofList)["storeDetail"] |> function Chrona.Engine.View.Value(Chrona.Engine.View.Text t) -> t | other -> failwith $"%A{other}")

    device.Offline <- false
    device.Wake()
    Assert.Empty(device.Model.Store.Pending)

[<Fact>]
let ``time for a month not read yet cannot be checked offline, so it is refused, not queued unchecked`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    device.Offline <- true

    device.Ui("manualActivityType", activityType device.Model)
    device.Ui("manualProject", project device.Model)
    device.Ui("manualStartDate", "2026-08-03")
    device.Ui("manualEndDate", "2026-08-03")
    device.Ui("manualStartTime", "09:00")
    device.Ui("manualEndTime", "10:00")
    device.Ui("manualDescription", "August")
    device.Ui("manualPurpose", "Delivery")
    device.Ui("manualReason", "From notes")
    device.Ui("saveManual", "")

    match device.Model.Store.Problem with
    | Some(Failed reason) -> Assert.Contains("have not been read yet", reason)
    | other -> failwith $"%A{other}"

    Assert.Empty(queued browser)
