/// Leaving the device: signing out with unsent changes (WI-0058) and with the
/// device's timer (WI-0055: 10.1, 10.2, 10.5, 23).
[<Xunit.Collection("Store")>]
module Chrona.Tests.StoreSignOutTests

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

// ---- Signing out with unsent changes (WI-0058) -------------------------------------------------

[<Fact>]
let ``signing out with nothing unsent signs out at once`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    record device "08:00" "08:30" "Setup"
    device.Ui("signOut", "")
    Assert.True(signedOut device)

[<Fact>]
let ``signing out with unsent changes asks first, and keeping them leaves them for this account only`` () =
    let browser = Browser()
    let github, device = unsentOn browser (configuration "production")
    let commits = github.State.History.Length

    device.Ui("signOut", "")
    Assert.False(signedOut device)
    Assert.Equal(Some ChoosingUnsent, device.Model.Identity.SignOut)
    Assert.StartsWith("1 change has not reached GitHub", (Project.project device.Model |> Map.ofList)["signOutSummary"] |> function Chrona.Engine.View.Value(Chrona.Engine.View.Text t) -> t | other -> failwith $"%A{other}")

    device.Ui("signOutKeep", "")
    Assert.True(signedOut device)
    Assert.Equal(1, (queued browser).Length)
    Assert.Equal(commits, github.State.History.Length)

    // The same account, back online, sends them.
    device.Close()
    let back = Device(github, RepositoryVisibility.Private, "production", browser)
    back.Open()
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(queued browser)

[<Fact>]
let ``discarding unsent changes at sign-out needs a confirmation that names how many`` () =
    let browser = Browser()
    let github, device = unsentOn browser (configuration "production")
    let commits = github.State.History.Length

    device.Ui("signOut", "")
    device.Ui("signOutDiscard", "")
    Assert.Equal(Some ConfirmingDiscard, device.Model.Identity.SignOut)
    Assert.Equal(Chrona.Engine.View.Value(Chrona.Engine.View.Text "Discard 1 change? They will not be saved anywhere, and this cannot be undone."), (Project.project device.Model |> Map.ofList)["signOutDiscardText"])

    // Changing one's mind keeps them, signed in.
    device.Ui("signOutCancel", "")
    Assert.Equal(None, device.Model.Identity.SignOut)
    Assert.Equal(1, (queued browser).Length)

    device.Ui("signOut", "")
    device.Ui("signOutDiscard", "")
    device.Ui("signOutDiscardConfirmed", "")
    Assert.True(signedOut device)
    Assert.Empty(queued browser)

    device.Offline <- false
    let back = Device(github, RepositoryVisibility.Private, "production", browser)
    back.Open()
    Assert.Equal(commits, github.State.History.Length)

[<Fact>]
let ``sending unsent changes at sign-out signs out once they are stored, or comes back when they cannot be sent`` () =
    let browser = Browser()
    let github, device = unsentOn browser (configuration "production")
    let commits = github.State.History.Length

    // Still offline: the choice comes back, saying why.
    device.Ui("signOut", "")
    device.Ui("signOutSend", "")
    Assert.False(signedOut device)
    Assert.Equal(Some ChoosingUnsent, device.Model.Identity.SignOut)
    Assert.Equal(Some "GitHub cannot be reached, so they could not be sent.", device.Model.Identity.SignOutNote)

    // Online again: they are sent, and then the person is signed out.
    device.Offline <- false
    device.Ui("signOutSend", "")
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.True(signedOut device)
    Assert.Empty(queued browser)

[<Fact>]
let ``a deployment that keeps nothing on shared devices offers only sending or discarding`` () =
    let config =
        """{"environment":"production","environmentName":"production","sharedDevicePolicy":"discardOnSignOut","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":"deployments"},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://chrona.test/"},"organizations":[{"id":"org_acme","displayName":"Acme Consulting","slug":"acme","timeZone":"America/New_York","administrators":["583231"]}]}"""
        |> Deployment.parse
        |> ok

    Assert.Equal(Deployment.DiscardOnSignOut, config.SharedDevice)
    let browser = Browser()
    let _, device = unsentOn browser config
    device.Ui("signOut", "")
    Assert.Equal(Chrona.Engine.View.Value(Chrona.Engine.View.Flag true), (Project.project device.Model |> Map.ofList)["signOutCannotKeep"])

    // Keeping is not offered, and asking for it does nothing.
    device.Ui("signOutKeep", "")
    Assert.False(signedOut device)
    Assert.Equal(1, (queued browser).Length)

[<Fact>]
let ``the shared-device policy is ask unless the deployment says otherwise, and nothing else is accepted`` () =
    Assert.Equal(Deployment.Ask, (configuration "production").SharedDevice)

    let refused =
        """{"environment":"local","environmentName":"local","sharedDevicePolicy":"forget"}""" |> Deployment.parse

    Assert.True(Result.isError refused)

// ---- The device's timer (WI-0055: 10.1, 10.2, 10.5, 23) -------------------------------------------

let private startTimer (device: Device) =
    device.Ui("timerActivityType", activityType device.Model)
    device.Ui("timerProject", project device.Model)
    device.Ui("timerDescription", "Deep work")
    device.Ui("startTimer", "")
    Assert.True(match device.Model.Timer with Timer.Running _ -> true | _ -> false)

let private timerKeyOf = TimerRecord.key "org_acme" "github:583231"

[<Fact>]
let ``a running timer survives a refresh, and is recovered with its elapsed time from its timestamps`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    startTimer device
    Assert.True(browser.Storage.ContainsKey timerKeyOf)

    // The page is reloaded 25 minutes later.
    let reloaded = Device(github, RepositoryVisibility.Private, "production", browser)
    reloaded.Now <- start.AddMinutes 25.0
    reloaded.Open()

    match reloaded.Model.Timer with
    | Timer.Running timer -> Assert.Equal(25, Timer.elapsedMinutes reloaded.Now timer)
    | other -> failwith $"%A{other}"

    Assert.Equal("Your records are open. Your timer was recovered and is still running: 25m so far.", reloaded.Model.Announcement)

[<Fact>]
let ``a stopped timer's held time survives a refresh until it is completed, then the device lets it go`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    startTimer device
    device.Now <- start.AddMinutes 40.0
    device.Ui("stopTimer", "")

    let reloaded = Device(github, RepositoryVisibility.Private, "production", browser)
    reloaded.Now <- start.AddMinutes 45.0
    reloaded.Open()
    Assert.Equal(40, reloaded.Model.Stopped.Value.TotalMinutes)

    reloaded.Ui("completePurpose", "Delivery")
    reloaded.Ui("saveCompletion", "")
    Assert.True(reloaded.Model.Stopped.IsNone)
    Assert.False(browser.Storage.ContainsKey timerKeyOf)
    Assert.Contains("Deep work", descriptions reloaded)

[<Fact>]
let ``a kept timer that cannot be read is left as it is, and another account never sees this one's`` () =
    let github = InMemoryStore()
    let browser = Browser()
    browser.Storage[timerKeyOf] <- "{not a timer"
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    Assert.Equal("Your records are open. A timer kept in this browser could not be read. It was left as it is.", device.Model.Announcement)
    Assert.Equal("{not a timer", browser.Storage[timerKeyOf])

    // octocat's timer, then hubot on the same browser.
    browser.Storage.Remove timerKeyOf |> ignore
    record device "08:00" "08:30" "Setup"
    device.Ui("memberId", "1001")
    device.Ui("memberName", "hubot")
    device.Ui("memberAccess", "ownTime")
    device.Ui("admitMember", "")
    startTimer device
    let other = Device(github, RepositoryVisibility.Private, "production", hubot, configuration "production", browser)
    other.Open()
    Assert.Equal(Timer.Idle, other.Model.Timer)
    Assert.True(browser.Storage.ContainsKey timerKeyOf)

[<Fact>]
let ``starting offline with nothing to open from, the timer keeps working and the unsent changes are counted`` () =
    let github = InMemoryStore()
    // No IndexedDB: no read cache to open the records from (WI-0057); the
    // queue is kept in localStorage.
    let browser = Browser(IndexedDb = false)
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    startTimer device
    device.Offline <- true
    record device "09:00" "10:00" "Pairing"

    // The page starts again while GitHub cannot be reached.
    device.Close()
    let offline = Device(github, RepositoryVisibility.Private, "production", browser)
    offline.Offline <- true
    offline.Now <- start.AddMinutes 30.0
    offline.Open()
    Assert.True(offline.Model.Store.Failure.IsSome)
    let view () = Project.project offline.Model |> Map.ofList
    Assert.Equal(Chrona.Engine.View.Value(Chrona.Engine.View.Flag true), (view ())["offlineTimer"])
    Assert.Equal(Chrona.Engine.View.Value(Chrona.Engine.View.Text "1 change waits in this browser and is sent when your records open."), (view ())["offlineWaiting"])

    // Pausing and stopping work without the records; the time is held, and kept.
    offline.Ui("pauseTimer", "")
    Assert.True(match offline.Model.Timer with Timer.Paused _ -> true | _ -> false)
    offline.Ui("stopTimer", "")
    Assert.Equal(30, offline.Model.Stopped.Value.TotalMinutes)
    Assert.Contains("\"stopped\"", browser.Storage[timerKeyOf])

[<Fact>]
let ``a running timer that overlaps time recorded on another device is an obligation with a stable code`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let laptop = Device(github, RepositoryVisibility.Private, "production", browser)
    laptop.Open()
    record laptop "08:00" "08:30" "Setup"
    startTimer laptop

    // Meanwhile, the phone records 14:15 to 14:35 today.
    let phone = Device(github, RepositoryVisibility.Private, "production")
    phone.Now <- start.AddMinutes 40.0
    phone.Open()
    phone.Ui("manualActivityType", activityType phone.Model)
    phone.Ui("manualProject", project phone.Model)
    phone.Ui("manualStartDate", "2026-10-08")
    phone.Ui("manualEndDate", "2026-10-08")
    phone.Ui("manualStartTime", "14:15")
    phone.Ui("manualEndTime", "14:35")
    phone.Ui("manualDescription", "Call")
    phone.Ui("manualPurpose", "Delivery")
    phone.Ui("saveManual", "")
    Assert.Equal(None, phone.Model.Store.Problem)

    let reopened = Device(github, RepositoryVisibility.Private, "production", browser)
    reopened.Now <- start.AddMinutes 45.0
    reopened.Open()
    let title = rowText "obligations" "title" reopened.Model
    Assert.Equal("Your timer overlaps \"Call\", recorded since it started", title)
    Assert.Contains("CHRONA.TIMER.CONCURRENT_CONFLICT", rowText "obligations" "detail" reopened.Model)

[<Fact>]
let ``signing out with a timer on the device asks first, like unsent changes`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    startTimer device

    device.Ui("signOut", "")
    Assert.Equal(Some ChoosingUnsent, device.Model.Identity.SignOut)
    device.Ui("signOutDiscard", "")
    Assert.Equal(Chrona.Engine.View.Value(Chrona.Engine.View.Text "Discard your timer? They will not be saved anywhere, and this cannot be undone."), (Project.project device.Model |> Map.ofList)["signOutDiscardText"])
    device.Ui("signOutDiscardConfirmed", "")
    Assert.True(signedOut device)
    Assert.False(browser.Storage.ContainsKey timerKeyOf)
