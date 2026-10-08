/// The store port on Arca (WI-0032): the engine's records opened and
/// committed through Arca's provider, against Arca's in-memory provider,
/// as the application drives it (requirements expansion 2.4 to 2.7, 21, 22,
/// 38, 39, 41). The GitHub backend's transport is checked through the
/// bridge with a fake GitHub.
module Chrona.Tests.StoreTests

open System
open Xunit
open Arca
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update
open Chrona.Application

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private configuration (environment: string) =
    """{"environment":"ENV","environmentName":"ENV","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":"deployments"},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://chrona.test/"},"organizations":[{"id":"org_acme","displayName":"Acme Consulting","slug":"acme","timeZone":"America/New_York"}]}"""
        .Replace("ENV", environment)
    |> Deployment.parse
    |> ok

let private start = DateTimeOffset(2026, 10, 8, 14, 10, 0, TimeSpan.FromHours -4.0)

let private octocat =
    { ActorId = "github:583231"
      OrganizationId = "local"
      DisplayName = "octocat"
      Kind = SignedIn "github" }

let private snapshot (visibility: RepositoryVisibility) : CapabilitySnapshot =
    { Identity =
        { Provider = "github"
          Subject = "583231"
          Login = Some "octocat"
          Kind = IdentityKind.User }
      RepositoryId = "R_1"
      Repository = RepositoryRef.create "acme" "chrona-data" |> ok
      Visibility = visibility
      CanRead = true
      CanWrite = true
      Archived = false
      Branch = BranchAccess.Writable }

let private keys = ref 0

/// One device: a page's bridge and store over the shared in-memory GitHub.
type private Device(github: InMemoryStore, visibility: RepositoryVisibility, environment: string, person: Session, config: Deployment.DeploymentConfig) =

    let bridge = Bridge.Bridge()

    let backend: Store.Backend =
        { Provider = fun _ -> github.Provider
          Resolve = fun _ -> async.Return(Ok(snapshot visibility)) }

    let store =
        Store.arca bridge backend (fun () -> start) (fun prefix -> $"{prefix}-{Threading.Interlocked.Increment keys:D8}")

    let ids = ref 0

    let ctx =
        { Now = start
          NewId = fun prefix -> $"{prefix}-{environment}-{Threading.Interlocked.Increment ids:D6}-{Guid.NewGuid():N}" }

    new(github, visibility, environment) = Device(github, visibility, environment, octocat, configuration environment)

    member val Model = Model.initial noOne InMemory start with get, set

    /// Sends a message, carries out the store effects, and feeds back every
    /// message the store produces, until nothing is left.
    member this.Send(msg: Msg) =
        let next, effects = update ctx msg this.Model
        this.Model <- next

        for effect in effects do
            match effect with
            | OpenStore(config, session, dates) -> store.Open config session dates
            | Store request -> store.Commit request
            | _ -> ()

        match bridge.Drain() with
        | Error error -> raise error
        | Ok(calls, messages) ->
            Assert.Empty(calls)

            for message in messages do
                this.Send message

    member this.Ui(name: string, value: string) = this.Send(Ui(name, None, value, None))

    /// Opens Chrona in this deployment and signs in as octocat.
    member this.Open() =
        this.Model <- { Model.initial noOne InMemory start with Deployment = Some config }
        this.Send(EnvironmentDescribed "America/New_York")
        this.Model <- { this.Model with Identity = { this.Model.Identity with Mode = SignInRequired false } }
        this.Send(IdentityChanged(SignedInAs person))

let private project (model: Model) = (Reference.selectable Reference.Project model.References).Head.Id
let private activityType (model: Model) = (Reference.selectable Reference.ActivityType model.References).Head.Id

/// Adds reference data and records a manual entry from 09:00 to 10:00 yesterday.
let private record (device: Device) (startTime: string) (endTime: string) (description: string) =
    let model = device.Model

    if (Reference.selectable Reference.Project model.References).IsEmpty then
        device.Ui("newProjectName", "HelixNote")
        device.Ui("addProject", "")
        device.Ui("newActivityTypeName", "Research")
        device.Ui("addActivityType", "")

    device.Ui("manualActivityType", activityType device.Model)
    device.Ui("manualProject", project device.Model)
    device.Ui("manualStartDate", "2026-10-07")
    device.Ui("manualStartTime", startTime)
    device.Ui("manualEndTime", endTime)
    device.Ui("manualDescription", description)
    device.Ui("manualPurpose", "Delivery")
    device.Ui("manualReason", "From notes")
    device.Ui("saveManual", "")

/// Every object's path in the repository (acme/chrona-data, main).
let private storedPaths (github: InMemoryStore) =
    github.State.Objects |> Map.toList |> List.map (fun (key, _) -> key.Substring(key.IndexOf ':' + 1))

// ---- Opening (2.4 to 2.7) ---------------------------------------------------------

[<Fact>]
let ``a new repository is set up on first sign-in, and the records open empty`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()

    Assert.True(canWork device.Model)
    Assert.Equal(Durable "acme/chrona-data", device.Model.Store.Kind)
    Assert.True(device.Model.Ledger.Activities.IsEmpty)

    Assert.Equal<string list>(
        [ "deployments/chrona/arca-manifest.json"
          "deployments/chrona/datasets/org_acme/arca-manifest.json"
          // The person who set it up founded it: they administer it.
          "deployments/chrona/datasets/org_acme/records/chrona.member/github_3a583231.json"
          "deployments/chrona/datasets/org_acme/records/chrona.organization/org_acme.json" ],
        storedPaths github |> List.sort
    )

    // The person works in the organization the deployment serves.
    Assert.Equal("org_acme", device.Model.Session.OrganizationId)

[<Fact>]
let ``production records are never started in a public repository`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Public, "production")
    device.Open()

    Assert.False(canWork device.Model)
    Assert.Contains("never started in a public repository", device.Model.Store.Failure |> Option.defaultValue "")
    Assert.Empty(storedPaths github)

    // Staging may use a public repository.
    let staging = Device(InMemoryStore(), RepositoryVisibility.Public, "staging")
    staging.Open()
    Assert.True(canWork staging.Model)

// ---- Committing and reading back (21, 22) ------------------------------------------------

[<Fact>]
let ``what is recorded is stored, one commit per command, and reads back on another device`` () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "09:00" "10:00" "Pairing"

    Assert.Empty(first.Model.Store.Pending)
    Assert.Equal(None, first.Model.Store.Problem)
    let paths = storedPaths github
    Assert.Contains(paths, fun path -> path.StartsWith "deployments/chrona/datasets/org_acme/records/chrona.activity/github_3a583231/2026/10/")
    Assert.Equal(2, paths |> List.filter (fun path -> path.Contains "records/chrona.reference/") |> List.length)

    // Another device signs in: the same records open there.
    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()
    let stored = second.Model.Ledger.Activities |> Map.toList |> List.map snd
    Assert.Equal<string list>([ "Pairing" ], stored |> List.map _.Classification.Description)
    Assert.Equal(2, second.Model.References.Items.Count)
    Assert.Empty(second.Model.Store.Integrity)

[<Fact>]
let ``independent changes from two devices both land, and each device is shown the other's`` () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "08:00" "08:30" "Setup"

    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()

    // Both record, independently, at different times: both are kept, and the
    // second device, whose commit was decided again on the moved repository,
    // now shows the first device's entry too.
    record first "09:00" "10:00" "Pairing"
    record second "13:00" "14:00" "Review"
    Assert.Equal(None, second.Model.Store.Problem)
    let descriptions (device: Device) = device.Model.Ledger.Activities |> Map.toList |> List.map (snd >> _.Classification.Description) |> List.sort
    Assert.Equal<string list>([ "Pairing"; "Review"; "Setup" ], descriptions second)

    let third = Device(github, RepositoryVisibility.Private, "production")
    third.Open()
    Assert.Equal<string list>([ "Pairing"; "Review"; "Setup" ], descriptions third)

[<Fact>]
let ``time that overlaps what another device stored first is a conflict decided on what is stored`` () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "08:00" "08:30" "Setup"
    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()

    // The first device records 09:00-10:00; the second, not knowing, 09:30-10:30.
    record first "09:00" "10:00" "Pairing"
    record second "09:30" "10:30" "Overlap"

    match second.Model.Store.Problem with
    | Some(Conflict detail) -> Assert.Contains("overlaps time recorded elsewhere", detail)
    | other -> failwith $"%A{other}"

    // It reloaded what is stored; the overlapping entry was never stored.
    let descriptions (device: Device) = device.Model.Ledger.Activities |> Map.toList |> List.map (snd >> _.Classification.Description) |> List.sort
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions second)
    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions reader)

[<Fact>]
let ``an edit made elsewhere first makes this device's stale edit a conflict, never an overwrite`` () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "09:00" "10:00" "Pairing"
    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()
    let id = (first.Model.Ledger.Activities |> Map.toList |> List.head |> fst)

    let amend (device: Device) (text: string) =
        device.Send(LocationMoved $"#/activity/{id}")
        device.Ui("amendDescription", text)
        device.Ui("amendReason", "fix")
        device.Ui("saveAmend", "")

    amend first "From the first device"
    amend second "From the second device"

    match second.Model.Store.Problem with
    | Some(Conflict detail) -> Assert.Contains("changed elsewhere first", detail)
    | other -> failwith $"%A{other}"

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal("From the first device", reader.Model.Ledger.Activities[id].Classification.Description)
    Assert.Equal("From the first device", second.Model.Ledger.Activities[id].Classification.Description)

[<Fact>]
let ``a commit whose outcome GitHub did not report is reconciled, not sent again`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    record device "09:00" "10:00" "Setup"
    let commits = github.State.History.Length
    github.Arrange InMemoryFault.OutcomeUnknownLanded
    record device "11:00" "12:00" "Landed anyway"

    Assert.Equal(None, device.Model.Store.Problem)
    Assert.Equal(commits + 1, github.State.History.Length)

// ---- What is read is checked (39, 41) ------------------------------------------------------

[<Fact>]
let ``records edited outside Chrona are held and reported when the records open`` () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "09:00" "10:00" "Pairing"

    let id, activity = first.Model.Ledger.Activities |> Map.toList |> List.head
    let path = ActivityRecord.path activity |> ok |> RelativePath.render
    let folder = (configuration "production") |> fun config -> Storage.organizationNamespace config (Storage.binding config |> ok) "org_acme" |> ok

    let edited =
        { activity with
            Classification = { activity.Classification with Description = "Edited on github.com" }
            Revision = 2 }

    github.WriteExternally(folder.Location, $"deployments/chrona/datasets/org_acme/{path}", Some(ActivityRecord.encode edited |> ok))

    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()
    Assert.False(second.Model.Ledger.Activities.ContainsKey id)
    Assert.Equal<string list>([ "CHRONA.INTEGRITY.EXTERNAL_EDIT" ], second.Model.Store.Integrity |> List.map code)

// ---- The GitHub backend's transport ------------------------------------------------------

[<Fact>]
let ``the GitHub backend sends its requests through the bridge with Fides' token, and reads the repository's visibility`` () =
    let bridge = Bridge.Bridge()
    let token = AccessToken.create "gho_STORETESTTOKEN0123456789" |> ok
    let mutable unauthorized = 0

    let backend =
        Store.gitHub
            bridge
            (fun () -> Some(fun () -> async.Return(Ok token)))
            (fun () ->
                async {
                    unauthorized <- unauthorized + 1
                })

    let location = DataLocation.create "acme" "chrona-data" "main" "deployments" |> ok
    let mutable result = None
    bridge.Start(async {
        let! resolved = backend.Resolve location
        result <- Some resolved
        return []
    })

    let answers =
        dict
            [ "https://api.github.com/user", """{"id":583231,"login":"octocat","type":"User"}"""
              "https://api.github.com/repos/acme/chrona-data",
              """{"id":1,"node_id":"R_1","name":"chrona-data","full_name":"acme/chrona-data","owner":{"login":"acme"},"private":false,"visibility":"public","archived":false,"permissions":{"pull":true,"push":true,"admin":false}}"""
              "https://api.github.com/repos/acme/chrona-data/branches/main", """{"name":"main"}"""
              "https://api.github.com/repos/acme/chrona-data/rules/branches/main", "[]" ]

    let mutable sent = []

    let rec pump () =
        match bridge.Drain() with
        | Error error -> raise error
        | Ok([], _) -> ()
        | Ok(calls, _) ->
            for id, call in calls do
                match call with
                | Bridge.Http(method, url, headers, _, _, responseHeaders) ->
                    sent <- sent @ [ method, url, headers ]
                    Assert.Contains("etag", responseHeaders)
                    let body = answers[url]
                    bridge.Answer id (Bridge.Answered(AppProtocol.HttpSucceeded(200, [ "x-ratelimit-remaining", "4999" ], body))) |> ignore
                | other -> failwith $"%A{other}"

            pump ()

    pump ()

    match result with
    | Some(Ok found) ->
        Assert.Equal(RepositoryVisibility.Public, found.Visibility)
        Assert.True(found.CanWrite)
    | other -> failwith $"%A{other}"

    Assert.All(
        sent,
        fun (method, _, headers) ->
            Assert.Equal("GET", method)
            Assert.Contains(("Authorization", "Bearer gho_STORETESTTOKEN0123456789"), headers)
            Assert.Contains(("X-GitHub-Api-Version", "2022-11-28"), headers)
    )

    Assert.Equal(0, unauthorized)

// ---- Rosters, people and organizations (WI-0031) -------------------------------------

let private hubot =
    { ActorId = "github:1001"
      OrganizationId = "local"
      DisplayName = "hubot"
      Kind = SignedIn "github" }

[<Fact>]
let ``only members work in an organization; an administrator adds people, and the roster is shared across devices`` () =
    let github = InMemoryStore()
    let founder = Device(github, RepositoryVisibility.Private, "production")
    founder.Open()

    // Someone else signs in: they are not a member, so nothing opens for them.
    let config = configuration "production"
    let stranger = Device(github, RepositoryVisibility.Private, "production", hubot, config)
    stranger.Open()
    Assert.False(canWork stranger.Model)
    let view = Project.project stranger.Model |> Map.ofList
    Assert.Equal(Chrona.Engine.View.Value(Chrona.Engine.View.Flag true), view["screenNotMember"])
    Assert.True(stranger.Model.Ledger.Activities.IsEmpty)

    // The founder adds them, to keep their own time.
    founder.Ui("memberId", "1001")
    founder.Ui("memberName", "hubot")
    founder.Ui("memberAccess", "ownTime")
    founder.Ui("admitMember", "")
    Assert.Equal(None, founder.Model.Store.Problem)

    let colleague = Device(github, RepositoryVisibility.Private, "production", hubot, config)
    colleague.Open()
    Assert.True(canWork colleague.Model)
    Assert.Equal<Access.Capability Set>(Access.Grants.ownTime, Access.capabilitiesOf colleague.Model.Roster "github:1001")

    // A member who may not manage the organization cannot change the roster.
    colleague.Send(Ui("changeMemberAccess", Some "github:1001", "administrator", None))
    Assert.Equal<Access.Capability Set>(Access.Grants.ownTime, Access.capabilitiesOf colleague.Model.Roster "github:1001")

    // The founder makes them a reviewer; the change is stored and read elsewhere.
    founder.Send(Ui("changeMemberAccess", Some "github:1001", "reviewer", None))
    let again = Device(github, RepositoryVisibility.Private, "production", hubot, config)
    again.Open()
    Assert.Equal<Access.Capability Set>(Access.Grants.reviewer, Access.capabilitiesOf again.Model.Roster "github:1001")

    // The only administrator cannot remove themselves.
    founder.Send(Ui("removeMember", Some "github:583231", "", None))
    Assert.True(founder.Model.Roster.Members.ContainsKey "github:583231")

    // Removing the colleague takes effect on their next opening.
    founder.Send(Ui("removeMember", Some "github:1001", "", None))
    let removed = Device(github, RepositoryVisibility.Private, "production", hubot, config)
    removed.Open()
    Assert.False(canWork removed.Model)

[<Fact>]
let ``reference data changed on one device is what another device opens`` () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "09:00" "10:00" "Pairing"
    let project = (Reference.selectable Reference.Project first.Model.References).Head
    first.Send(Ui("referenceActive", Some $"project:{project.Id}", "", Some false))

    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()
    Assert.Equal(Reference.Archived, second.Model.References.Items[(Reference.Project, project.Id)].Status)
    // Archived, so no longer offered, but still valid on the record that carries it.
    Assert.Empty(Reference.selectable Reference.Project second.Model.References)
    Assert.Equal(project.Id, (second.Model.Ledger.Activities |> Map.toList |> List.head |> snd).Classification.ProjectId)

[<Fact>]
let ``a person works in one of the deployment's organizations at a time, each in its own folder`` () =
    let github = InMemoryStore()

    let config =
        """{"environment":"production","environmentName":"production","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":"deployments"},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://chrona.test/"},"organizations":[{"id":"org_acme","displayName":"Acme Consulting","slug":"acme","timeZone":"America/New_York"},{"id":"org_eu","displayName":"Acme Europe","slug":"acme-eu","timeZone":"Europe/Berlin"}]}"""
        |> Deployment.parse
        |> ok

    let device = Device(github, RepositoryVisibility.Private, "production", octocat, config)
    device.Open()
    Assert.Equal("org_acme", device.Model.Session.OrganizationId)
    record device "09:00" "10:00" "Acme work"

    device.Ui("chooseOrganization", "org_eu")
    Assert.Equal("org_eu", device.Model.Session.OrganizationId)
    Assert.True(canWork device.Model)
    Assert.True(device.Model.Ledger.Activities.IsEmpty)
    record device "11:00" "12:00" "Europe work"

    let paths = storedPaths github
    Assert.Contains(paths, fun path -> path.StartsWith "deployments/chrona/datasets/org_eu/records/chrona.activity/")
    Assert.Contains(paths, fun path -> path.StartsWith "deployments/chrona/datasets/org_acme/records/chrona.activity/")

    device.Ui("chooseOrganization", "org_acme")
    Assert.Equal<string list>([ "Acme work" ], device.Model.Ledger.Activities |> Map.toList |> List.map (snd >> _.Classification.Description))
