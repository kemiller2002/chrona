/// Opening offline from this browser's read cache (WI-0057, Limen
/// LCP-082..087), and what leaving or clearing the device does with it.
[<Xunit.Collection("Store")>]
module Chrona.Tests.StoreReadCacheTests

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

// ---- Opening offline from the read cache (WI-0057, Limen LCP-082..087) -----

[<Fact>]
let ``starting while GitHub cannot be reached, the records open from this browser's read cache, as of when they were read`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    device.Close()

    let offline = Device(github, RepositoryVisibility.Private, "production", browser)
    offline.Offline <- true
    offline.Open()
    Assert.Equal(None, offline.Model.Store.Failure)
    Assert.Equal(Some start, offline.Model.Store.Cached)
    Assert.Equal<string list>([ "Setup" ], descriptions offline)
    Assert.StartsWith("Offline: your records as of", headline offline)

/// A browser that read octocat's records from GitHub once (one activity),
/// then closed.
let private readOnce (github: InMemoryStore) (browser: Browser) =
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    let commits = github.State.History.Length
    device.Close()
    commits

[<Fact>]
let ``a change made from the read cache is decided again on what GitHub holds before it is sent, and sent once`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let commits = readOnce github browser

    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Offline <- true
    device.Open()
    Assert.True(device.Model.Store.Cached.IsSome)
    record device "09:00" "10:00" "Pairing"
    Assert.Equal(1, device.Model.Store.Pending.Length)
    Assert.Equal(commits, github.State.History.Length)

    // Kept in this browser, conditioned on nothing a cached value gave.
    match inIndexedDb browser with
    | [ entry ] -> Assert.Equal(None, entry.Operation.ExpectedChangeToken)
    | other -> failwith $"%A{other}"

    // GitHub again: the records are read from it, the change is decided on
    // them, and sent once.
    device.Offline <- false
    device.Wake()
    Assert.Equal(None, device.Model.Store.Cached)
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(device.Model.Store.Pending)
    Assert.Empty(queued browser)
    Assert.Equal("All changes saved", headline device)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions reader)

[<Fact>]
let ``a change made from the read cache to a record someone changed since is a conflict on reconnect, neither side dropped`` () =
    let github = InMemoryStore()
    let browser = Browser()
    readOnce github browser |> ignore

    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Offline <- true
    device.Open()
    let id = device.Model.Ledger.Activities |> Map.toList |> List.head |> fst

    // Meanwhile, another device changes it on GitHub.
    let phone = Device(github, RepositoryVisibility.Private, "production")
    phone.Open()
    amendFrom phone id "From the phone"

    amendFrom device id "From the cache, offline"
    device.Offline <- false
    device.Wake()

    match device.Model.Store.Conflicts with
    | [ { Divergences = [ Reconcile.ActivityChanged(mine, Some stored) ] } ] ->
        Assert.Equal("From the cache, offline", mine.Classification.Description)
        Assert.Equal("From the phone", stored.Classification.Description)
    | other -> failwith $"%A{other}"

    Assert.Empty(queued browser)
    Assert.Equal("From the phone", device.Model.Ledger.Activities[id].Classification.Description)

[<Fact>]
let ``from the read cache, a month it does not hold is refused, not queued unchecked`` () =
    let github = InMemoryStore()
    let browser = Browser()
    readOnce github browser |> ignore

    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Offline <- true
    device.Open()
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

[<Fact>]
let ``records held for review stay held when they open from the read cache`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let first = Device(github, RepositoryVisibility.Private, "production", browser)
    first.Open()
    record first "09:00" "10:00" "Pairing"
    let id, activity = first.Model.Ledger.Activities |> Map.toList |> List.head
    let path = ActivityRecord.path activity |> ok |> RelativePath.render
    let config = configuration "production"
    let folder = Storage.organizationNamespace config (Storage.binding config |> ok) "org_acme" |> ok
    let edited = { activity with Classification = { activity.Classification with Description = "Edited on github.com" }; Revision = 2 }
    github.WriteExternally(folder.Location, $"deployments/chrona/datasets/org_acme/{path}", Some(ActivityRecord.encode edited |> ok))
    first.Close()

    // Read once more from GitHub, which holds it for review, then offline.
    let second = Device(github, RepositoryVisibility.Private, "production", browser)
    second.Open()
    Assert.False(second.Model.Ledger.Activities.ContainsKey id)
    second.Close()

    let offline = Device(github, RepositoryVisibility.Private, "production", browser)
    offline.Offline <- true
    offline.Open()
    Assert.True(offline.Model.Store.Cached.IsSome)
    Assert.False(offline.Model.Ledger.Activities.ContainsKey id)
    Assert.Equal<string list>([ "CHRONA.INTEGRITY.EXTERNAL_EDIT" ], offline.Model.Store.Integrity |> List.map code)

[<Fact>]
let ``the read cache is this account's: another account on the same browser never opens from it`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let config = configuration "production"
    let owner = Device(github, RepositoryVisibility.Private, "production", browser)
    owner.Open()
    owner.Ui("memberId", "1001")
    owner.Ui("memberName", "hubot")
    owner.Ui("memberAccess", "ownTime")
    owner.Ui("admitMember", "")
    owner.Close()

    let other = Device(github, RepositoryVisibility.Private, "production", hubot, config, browser)
    other.Offline <- true
    other.Open()
    Assert.True(other.Model.Store.Failure.IsSome)
    Assert.Equal(None, other.Model.Store.Cached)

[<Fact>]
let ``a refusal from GitHub is never answered with what was cached`` () =
    let github = InMemoryStore()
    let browser = Browser()
    readOnce github browser |> ignore

    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Refusing <- Some "Your GitHub account cannot see acme/chrona-data."
    device.Open()
    Assert.Equal(Some "Your GitHub account cannot see acme/chrona-data.", device.Model.Store.Failure)
    Assert.Equal(None, device.Model.Store.Cached)

[<Fact>]
let ``signing out clears the account's read cache, unless its unsent work is kept here under 'ask'`` () =
    // Nothing unsent: the cache goes.
    let github = InMemoryStore()
    let browser = Browser()
    readOnce github browser |> ignore
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    device.Ui("signOut", "")
    Assert.True(signedOut device)
    device.Close()
    let after = Device(github, RepositoryVisibility.Private, "production", browser)
    after.Offline <- true
    after.Open()
    Assert.True(after.Model.Store.Failure.IsSome)

    // Kept under 'ask': the cache stays with it, so the account opens offline
    // and sees its unsent work in context.
    let github = InMemoryStore()
    let browser = Browser()
    readOnce github browser |> ignore
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    device.Offline <- true
    record device "09:00" "10:00" "Pairing"
    device.Ui("signOut", "")
    device.Ui("signOutKeep", "")
    Assert.True(signedOut device)
    device.Close()
    let after = Device(github, RepositoryVisibility.Private, "production", browser)
    after.Offline <- true
    after.Open()
    Assert.True(after.Model.Store.Cached.IsSome)
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions after)

[<Fact>]
let ``under discardOnSignOut, signing out always clears the account's read cache`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let config = { configuration "production" with SharedDevice = Deployment.DiscardOnSignOut }
    let first = Device(github, RepositoryVisibility.Private, "production", octocat, config, browser)
    first.Open()
    record first "08:00" "08:30" "Setup"
    first.Offline <- true
    record first "09:00" "10:00" "Pairing"
    first.Ui("signOut", "")
    first.Ui("signOutDiscard", "")
    first.Ui("signOutDiscardConfirmed", "")
    Assert.True(signedOut first)
    first.Close()

    let after = Device(github, RepositoryVisibility.Private, "production", octocat, config, browser)
    after.Offline <- true
    after.Open()
    Assert.True(after.Model.Store.Failure.IsSome)
    Assert.Equal(None, after.Model.Store.Cached)

[<Fact>]
let ``clearing this device removes the read cache and the queue for every account, then signs out`` () =
    let github = InMemoryStore()
    let browser = Browser()
    readOnce github browser |> ignore
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    Assert.True(viewFlag "canClearDevice" device.Model)
    device.Ui("clearDevice", "")
    Assert.True(viewFlag "clearDeviceConfirming" device.Model)
    device.Ui("clearDeviceConfirmed", "")
    Assert.True(signedOut device)
    device.Close()

    let after = Device(github, RepositoryVisibility.Private, "production", browser)
    after.Offline <- true
    after.Open()
    Assert.True(after.Model.Store.Failure.IsSome)
    Assert.Equal(None, after.Model.Store.Cached)

[<Fact>]
let ``this device cannot be cleared while this account has unsent work`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    device.Offline <- true
    record device "09:00" "10:00" "Pairing"
    Assert.False(viewFlag "canClearDevice" device.Model)
    device.Ui("clearDevice", "")
    Assert.Equal(None, device.Model.Identity.DeviceClear)

[<Fact>]
let ``a tab that opened from the read cache while another tab holds the queue keeps its own changes in the page and sends them once GitHub is back`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let commits = readOnce github browser
    let holder = Device(github, RepositoryVisibility.Private, "production", browser)
    holder.Open()

    let second = Device(github, RepositoryVisibility.Private, "production", browser)
    second.Offline <- true
    second.Open()
    Assert.True(second.Model.Store.Cached.IsSome)
    Assert.Equal(HeldElsewhere false, second.Model.Store.Sync.Holder)
    record second "11:00" "12:00" "Review"
    Assert.Equal(1, second.Model.Store.Pending.Length)
    Assert.Empty(queued browser)

    second.Offline <- false
    second.Wake()
    Assert.Equal(None, second.Model.Store.Cached)
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(second.Model.Store.Pending)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Review"; "Setup" ], descriptions reader)
