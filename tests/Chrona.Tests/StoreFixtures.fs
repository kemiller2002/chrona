/// What several of the store's test files share: a second member, devices
/// side by side, the queue as the browser keeps it, signing out, inbox files
/// and report titles.
module Chrona.Tests.StoreFixtures

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

let hubot =
    { ActorId = "github:1001"
      OrganizationId = "local"
      DisplayName = "hubot"
      Kind = SignedIn "github" }

let viewFlag (key: string) (model: Model) =
    match (Project.project model |> Map.ofList)[key] with
    | Chrona.Engine.View.Value(Chrona.Engine.View.Flag f) -> f
    | other -> failwith $"%A{other}"

let viewText (key: string) (model: Model) =
    match (Project.project model |> Map.ofList)[key] with
    | Chrona.Engine.View.Value(Chrona.Engine.View.Text t) -> t
    | other -> failwith $"%A{other}"

/// Two devices on the same records; the first records "Pairing" 09:00-10:00.
let twoDevices () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "08:00" "08:30" "Setup"
    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()
    github, first, second

let descriptions (device: Device) =
    device.Model.Ledger.Activities |> Map.toList |> List.map (snd >> _.Classification.Description) |> List.sort

let amendFrom (device: Device) (id: string) (text: string) =
    device.Send(LocationMoved $"#/activity/{id}")
    device.Ui("amendDescription", text)
    device.Ui("amendReason", "fix")
    device.Ui("saveAmend", "")

/// A record edited on github.com, then a device that opens the records.
let editedOutside (change: Activity.Activity -> Activity.Activity) =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "09:00" "10:00" "Pairing"
    let id, activity = first.Model.Ledger.Activities |> Map.toList |> List.head
    let path = ActivityRecord.path activity |> ok |> RelativePath.render
    let config = configuration "production"
    let folder = Storage.organizationNamespace config (Storage.binding config |> ok) "org_acme" |> ok

    let write (edited: Activity.Activity) =
        github.WriteExternally(folder.Location, $"deployments/chrona/datasets/org_acme/{path}", Some(ActivityRecord.encode edited |> ok))

    write (change activity)
    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()
    github, second, id, activity, write

let described (text: string) (activity: Activity.Activity) =
    { activity with
        Classification = { activity.Classification with Description = text }
        Revision = 2 }

let headline (device: Device) =
    match (Project.project device.Model |> Map.ofList)["storeHeadline"] with
    | Chrona.Engine.View.Value(Chrona.Engine.View.Text text) -> text
    | other -> failwith $"%A{other}"

let queueKey = "arca.queue.chrona.org_acme"

let outstanding (text: string) =
    (OfflineQueue.decode text |> ok).Entries
    |> List.filter (fun entry -> match entry.State with EntryState.Synchronized _ | EntryState.Abandoned _ -> false | _ -> true)

/// The queue folder's IndexedDB record, as another tab of the browser reads it.
let queueRecord (browser: Browser) =
    let tab = browser.Tab()
    let config = configuration "production"
    let folder = Storage.organizationNamespace config (Storage.binding config |> ok) "org_acme" |> ok

    async {
        match! Arca.Limen.IndexedDbQueue.openDatabase tab.Execute with
        | Error _ -> return None
        | Ok opened ->
            match! Arca.Limen.IndexedDbQueue.read tab.Execute (Limen.Store.Connection.Open opened) (Arca.Limen.IndexedDbQueue.recordKey folder) with
            | Ok found -> return found
            | Error failure -> return failwith $"%A{failure}"
    }
    |> Async.RunSynchronously

/// The unsent changes this browser keeps in IndexedDB (WI-0059).
let inIndexedDb (browser: Browser) =
    match queueRecord browser with
    | Some { Queue = Some text } -> outstanding text
    | _ -> []

/// The unsent changes this browser keeps in localStorage: where IndexedDB
/// cannot be used, or where an older Chrona kept them.
let inLocalStorage (browser: Browser) =
    match browser.Storage.TryGetValue queueKey with
    | true, text -> outstanding text
    | _ -> []

/// The unsent changes this browser keeps, wherever it keeps them.
let queued (browser: Browser) = inIndexedDb browser @ inLocalStorage browser

let folderOf (config: Deployment.DeploymentConfig) =
    Storage.organizationNamespace config (Storage.binding config |> ok) "org_acme" |> ok

let signedOut (device: Device) =
    match device.Model.Identity.Mode with
    | SignInRequired _ -> true
    | _ -> false

/// A device in a shared browser whose next change cannot reach GitHub.
let unsentOn (browser: Browser) (config: Deployment.DeploymentConfig) =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production", octocat, config, browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    device.Offline <- true
    record device "09:00" "10:00" "Pairing"
    Assert.Equal(1, device.Model.Store.Pending.Length)
    github, device

let orgPath (path: string) = $"deployments/chrona/datasets/org_acme/{path}"

/// A producer files a payload in the organization's inbox, outside Chrona.
let fileInInbox (github: InMemoryStore) (source: string) (id: string) (text: string) =
    github.WriteExternally((folderOf (configuration "production")).Location, orgPath $"inbox/{source}/{id}.json", Some text)

let observedWork (id: string) (projectId: string) (startTime: int) : Chrona.Integration.TimeObservationV1.TimeObservation =
    let begins = DateTimeOffset(2026, 10, 7, startTime, 0, 0, TimeSpan.FromHours -4.0)

    { ObservationId = id
      SourceSystem = "github"
      OrganizationId = "org_acme"
      ProjectId = projectId
      ActorId = Some octocat.ActorId
      WorkItemId = Some "PR-7"
      ExternalUrl = Some "https://example.test/pull/7"
      Timing = Chrona.Integration.TimeObservationV1.Interval(begins, begins.AddMinutes 45.0)
      Description = Some $"Reviewed {id}"
      Evidence = []
      ObservedAt = begins.AddMinutes 45.0 }

/// An organization with reference data, and a producer's four files: two
/// observations, one that is not one, and one for another organization.
let withInbox () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "08:00" "08:30" "Setup"
    let projectId = project first.Model
    let serialize = Chrona.Integration.TimeObservationV1.serialize
    fileInInbox github "github" "obs-1" (serialize (observedWork "obs-1" projectId 13))
    fileInInbox github "github" "obs-2" (serialize (observedWork "obs-2" projectId 15))
    fileInInbox github "github" "broken" """{"contract":"chrona.time-observation","version":1}"""
    fileInInbox github "github" "elsewhere" (serialize { observedWork "elsewhere" projectId 16 with OrganizationId = "org_other" })
    github, first

/// The titles of the rows a report shows.
let reportTitles (model: Model) =
    match (Project.project model |> Map.ofList)["reportRows"] with
    | Chrona.Engine.View.Items rows ->
        rows
        |> List.choose (fun row ->
            row
            |> List.tryPick (function
                | "title", Chrona.Engine.View.Text value -> Some value
                | _ -> None))
        |> List.sort
    | other -> failwith $"%A{other}"

/// Records an hour of manual time on a day, with the first reference data.
let recordOn (device: Device) (date: string) (description: string) =
    device.Ui("manualActivityType", activityType device.Model)
    device.Ui("manualProject", project device.Model)
    device.Ui("manualStartDate", date)
    device.Ui("manualEndDate", date)
    device.Ui("manualStartTime", "09:00")
    device.Ui("manualEndTime", "10:00")
    device.Ui("manualDescription", description)
    device.Ui("manualPurpose", "Delivery")
    device.Ui("manualReason", "From notes")
    device.Ui("saveManual", "")
    Assert.Equal(None, device.Model.Store.Problem)
