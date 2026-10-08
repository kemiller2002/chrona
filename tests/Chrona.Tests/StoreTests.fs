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
    """{"environment":"ENV","environmentName":"ENV","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":"deployments"},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://chrona.test/"},"organizations":[{"id":"org_acme","displayName":"Acme Consulting","slug":"acme","timeZone":"America/New_York","administrators":["583231"]}]}"""
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

    /// Commits that someone else's commit to the repository beats first.
    let mutable beaten = 0

    let provider location =
        { github.Provider with
            Commit =
                fun operation ->
                    async {
                        if beaten > 0 then
                            beaten <- beaten - 1
                            github.WriteExternally(location, $"other-application/{beaten}.txt", Some "another application's file")

                        return! github.Provider.Commit operation
                    } }

    let backend: Store.Backend =
        { Provider = provider
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
            | ConfirmAdministrator -> store.Confirm()
            | _ -> ()

        match bridge.Drain() with
        | Error error -> raise error
        | Ok(calls, messages) ->
            Assert.Empty(calls)

            for message in messages do
                this.Send message

    member this.Ui(name: string, value: string) = this.Send(Ui(name, None, value, None))

    member this.Ui(name: string, key: string, value: string) = this.Send(Ui(name, Some key, value, None))

    /// The next `count` commits are each beaten by another application's commit.
    member _.Beaten(count: int) = beaten <- count

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

/// A text of the first row of a projected list.
let private rowText (list: string) (key: string) (model: Model) =
    match Project.project model |> List.tryFind (fst >> (=) list) with
    | Some(_, Chrona.Engine.View.Items(row :: _)) ->
        match row |> List.tryFind (fst >> (=) key) with
        | Some(_, Chrona.Engine.View.Text value) -> value
        | other -> failwith $"%A{other}"
    | other -> failwith $"%A{other}"

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

    // Kept for the person, with what diverged and a stable code; not dropped.
    match second.Model.Store.Conflicts with
    | [ { Divergences = [ Reconcile.OverlapsStored(mine, stored) ] } ] ->
        Assert.Equal("Overlap", mine.Classification.Description)
        Assert.Equal("Pairing", stored.Classification.Description)
    | other -> failwith $"%A{other}"

    Assert.Equal("CHRONA.OVERLAP.OVERLAPS_ACTIVITY", rowText "conflicts" "code" second.Model)

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

    match second.Model.Store.Conflicts with
    | [ { Divergences = [ Reconcile.ActivityChanged(mine, Some stored) ] } ] ->
        Assert.Equal("From the second device", mine.Classification.Description)
        Assert.Equal("From the first device", stored.Classification.Description)
    | other -> failwith $"%A{other}"

    Assert.Equal("CHRONA.CONCURRENCY.SEMANTIC_CONFLICT", rowText "conflicts" "code" second.Model)

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
        """{"environment":"production","environmentName":"production","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":"deployments"},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://chrona.test/"},"organizations":[{"id":"org_acme","displayName":"Acme Consulting","slug":"acme","timeZone":"America/New_York","administrators":["583231"]},{"id":"org_eu","displayName":"Acme Europe","slug":"acme-eu","timeZone":"Europe/Berlin","administrators":["583231"]}]}"""
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

// ---- Bootstrap administrators (WI-0053) ----------------------------------------------

let private configured (environment: string) (administrators: string) =
    """{"environment":"ENV","environmentName":"ENV","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":"deployments"},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://chrona.test/"},"organizations":[{"id":"org_acme","displayName":"Acme Consulting","slug":"acme","timeZone":"America/New_York","administrators":ADMINS}]}"""
        .Replace("ENV", environment)
        .Replace("ADMINS", administrators)
    |> Deployment.parse
    |> ok

let private viewFlag (key: string) (model: Model) =
    match (Project.project model |> Map.ofList)[key] with
    | Chrona.Engine.View.Value(Chrona.Engine.View.Flag f) -> f
    | other -> failwith $"%A{other}"

[<Fact>]
let ``an account the configuration does not list cannot set an organization up, and nothing is written`` () =
    let github = InMemoryStore()
    let stranger = Device(github, RepositoryVisibility.Private, "production", hubot, configured "production" """["583231"]""")
    stranger.Open()
    Assert.False(canWork stranger.Model)
    Assert.Contains("listed administrators can set it up", stranger.Model.Store.Failure |> Option.defaultValue "")
    Assert.DoesNotContain(storedPaths github, fun path -> path.Contains "datasets/org_acme")

[<Fact>]
let ``with no administrators listed, production refuses to set up; a local environment keeps first-opener founding`` () =
    let github = InMemoryStore()
    let production = Device(github, RepositoryVisibility.Private, "production", octocat, configured "production" "[]")
    production.Open()
    Assert.Contains("has no administrators in this deployment's configuration", production.Model.Store.Failure |> Option.defaultValue "")
    Assert.DoesNotContain(storedPaths github, fun path -> path.Contains "datasets/org_acme")

    let local = Device(InMemoryStore(), RepositoryVisibility.Private, "local", octocat, configured "local" "[]")
    local.Open()
    Assert.True(canWork local.Model)
    Assert.True(Access.permits local.Model.Roster "github:583231" Access.ManageOrganizationSettings)

[<Fact>]
let ``an organization from before the rule, administered by an unlisted account, waits for a listed account to confirm`` () =
    let github = InMemoryStore()

    // Set up earlier, locally, by hubot, who is not listed now.
    let earlier = Device(github, RepositoryVisibility.Private, "local", hubot, configured "local" "[]")
    earlier.Open()
    record earlier "09:00" "10:00" "Earlier work"

    let rules = configured "production" """["583231"]"""

    // hubot, the stored administrator, is not granted anything and cannot confirm.
    let unlisted = Device(github, RepositoryVisibility.Private, "production", hubot, rules)
    unlisted.Open()
    Assert.False(canWork unlisted.Model)
    Assert.True(viewFlag "screenConfirmAdministrator" unlisted.Model)
    Assert.False(viewFlag "canConfirmAdministrator" unlisted.Model)
    Assert.True(unlisted.Model.Ledger.Activities.IsEmpty)
    unlisted.Ui("confirmAdministrator", "")
    Assert.False(canWork unlisted.Model)

    // octocat, listed, is not granted silently either: they confirm.
    let listed = Device(github, RepositoryVisibility.Private, "production", octocat, rules)
    listed.Open()
    Assert.False(canWork listed.Model)
    Assert.True(viewFlag "canConfirmAdministrator" listed.Model)
    Assert.DoesNotContain(storedPaths github, fun path -> path.EndsWith "chrona.member/github_3a583231.json")
    listed.Ui("confirmAdministrator", "")
    Assert.True(canWork listed.Model)
    Assert.True(Access.permits listed.Model.Roster "github:583231" Access.ManageOrganizationSettings)

    // Now that a listed administrator exists, hubot works as the roster says.
    let again = Device(github, RepositoryVisibility.Private, "production", hubot, rules)
    again.Open()
    Assert.True(canWork again.Model)
    // Nothing stored was lost while it waited.
    Assert.Equal<string list>([ "Earlier work" ], again.Model.Ledger.Activities |> Map.toList |> List.map (snd >> _.Classification.Description))

[<Fact>]
let ``an organization with no members at all is not founded by whoever opens it`` () =
    let github = InMemoryStore()
    let earlier = Device(github, RepositoryVisibility.Private, "local", hubot, configured "local" "[]")
    earlier.Open()

    // Its roster is removed outside Chrona.
    let folder =
        let config = configured "production" """["583231"]"""
        Storage.organizationNamespace config (Storage.binding config |> ok) "org_acme" |> ok

    github.WriteExternally(folder.Location, "deployments/chrona/datasets/org_acme/records/chrona.member/github_3a1001.json", None)

    let opener = Device(github, RepositoryVisibility.Private, "production", hubot, configured "production" """["583231"]""")
    opener.Open()
    Assert.False(canWork opener.Model)
    Assert.True(viewFlag "screenConfirmAdministrator" opener.Model)
    Assert.DoesNotContain(storedPaths github, fun path -> path.Contains "chrona.member/")

// ---- Resolving what was not stored (WI-0035: 21, 23, 26, 34) ------------------------------

/// Two devices on the same records; the first records "Pairing" 09:00-10:00.
let private twoDevices () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "08:00" "08:30" "Setup"
    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()
    github, first, second

let private descriptions (device: Device) =
    device.Model.Ledger.Activities |> Map.toList |> List.map (snd >> _.Classification.Description) |> List.sort

let private amendFrom (device: Device) (id: string) (text: string) =
    device.Send(LocationMoved $"#/activity/{id}")
    device.Ui("amendDescription", text)
    device.Ui("amendReason", "fix")
    device.Ui("saveAmend", "")

[<Fact>]
let ``a stale edit is redone on the current version only when the person saves it again`` () =
    let github, first, second = twoDevices ()
    let id = first.Model.Ledger.Activities |> Map.toList |> List.head |> fst
    amendFrom first id "From the first device"
    amendFrom second id "From the second device"
    let case = second.Model.Store.Conflicts.Head
    Assert.Equal("Redo yours on the current version", rowText "conflicts" "redoLabel" second.Model)

    // Redoing puts the person's version into the correction form of the
    // current record; nothing is written until they save it.
    let commits = github.State.History.Length
    second.Ui("redoChange", case.Id, id)
    second.Send(LocationMoved $"#/activity/{id}")
    Assert.Empty(second.Model.Store.Conflicts)
    Assert.Equal(commits, github.State.History.Length)
    let detail = second.Model.Detail.Value
    Assert.Equal("From the second device", detail.Amend.Description)
    Assert.Equal(second.Model.Ledger.Activities[id].Revision, detail.Revision)

    second.Ui("amendReason", "Redone after a conflict")
    second.Ui("saveAmend", "")
    Assert.Empty(second.Model.Store.Conflicts)
    Assert.Equal(None, second.Model.Store.Problem)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal("From the second device", reader.Model.Ledger.Activities[id].Classification.Description)

[<Fact>]
let ``keeping what is stored sets the person's change aside, knowingly, and writes nothing`` () =
    let github, first, second = twoDevices ()
    let id = first.Model.Ledger.Activities |> List.ofSeq |> List.head |> _.Key
    amendFrom first id "From the first device"
    amendFrom second id "From the second device"
    let commits = github.State.History.Length

    // Until the person decides, it is an obligation, and the day is not saved.
    Assert.Contains("was not saved", rowText "obligations" "title" second.Model)
    Assert.Equal("Not saved: changed elsewhere", (Project.project second.Model |> Map.ofList)["storeHeadline"] |> function Chrona.Engine.View.Value(Chrona.Engine.View.Text t) -> t | other -> failwith $"%A{other}")

    second.Ui("keepStored", second.Model.Store.Conflicts.Head.Id, "")
    Assert.Empty(second.Model.Store.Conflicts)
    Assert.Equal("Your change was set aside. What is stored stays.", second.Model.Announcement)
    Assert.Equal(commits, github.State.History.Length)
    Assert.Equal("From the first device", second.Model.Ledger.Activities[id].Classification.Description)

[<Fact>]
let ``new time that overlaps what was stored since goes back into the form, to be changed and saved`` () =
    let github, first, second = twoDevices ()
    record first "09:00" "10:00" "Pairing"
    record second "09:30" "10:30" "Overlap"
    let case = second.Model.Store.Conflicts.Head
    let mine = match case.Divergences with [ Reconcile.OverlapsStored(mine, _) ] -> mine | other -> failwith $"%A{other}"

    second.Ui("redoChange", case.Id, mine.ActivityId)
    let draft = second.Model.Manual
    Assert.Equal<string list>([ "2026-10-07"; "09:30"; "2026-10-07"; "10:30"; "Overlap" ], [ draft.StartDate; draft.StartTime; draft.EndDate; draft.EndTime; draft.Classification.Description ])

    second.Ui("manualStartTime", "10:00")
    second.Ui("manualEndTime", "11:00")
    second.Ui("saveManual", "")
    Assert.Empty(second.Model.Store.Conflicts)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Overlap"; "Pairing"; "Setup" ], descriptions reader)

[<Fact>]
let ``a change refused only because the repository kept moving is tried again when the person asks`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    record device "08:00" "08:30" "Setup"

    // Another application commits to the shared repository before each of
    // the three attempts.
    device.Beaten 3
    record device "09:00" "10:00" "Pairing"
    let case = device.Model.Store.Conflicts.Head
    Assert.Equal<Reconcile.Divergence list>([ Reconcile.KeptChanging ], case.Divergences)
    Assert.Equal("CHRONA.CONCURRENCY.KEPT_CHANGING", rowText "conflicts" "code" device.Model)
    Assert.Equal<string list>([ "Setup" ], descriptions device)

    device.Ui("retryChange", case.Id, "")
    Assert.Empty(device.Model.Store.Conflicts)
    Assert.Equal(None, device.Model.Store.Problem)
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions device)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions reader)

// ---- Reviewing records edited outside Chrona (WI-0035: 41) ----------------------------------

/// A record edited on github.com, then a device that opens the records.
let private editedOutside (change: Activity.Activity -> Activity.Activity) =
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

let private described (text: string) (activity: Activity.Activity) =
    { activity with
        Classification = { activity.Classification with Description = text }
        Revision = 2 }

[<Fact>]
let ``a record edited outside Chrona is accepted by its person after the usual rules, and is trusted again`` () =
    let github, second, id, _, _ = editedOutside (described "Edited on github.com")
    Assert.Equal<string list>([ id ], second.Model.Store.Held |> List.map _.ActivityId)
    Assert.Contains("Edited on github.com", rowText "outsideEdits" "title" second.Model)
    Assert.Contains("changed outside Chrona", rowText "obligations" "title" second.Model)

    second.Ui("acceptOutsideEdit", id, "")
    Assert.Empty(second.Model.Store.Held)
    Assert.Empty(second.Model.Store.Integrity)
    Assert.Equal(None, second.Model.Store.Problem)
    Assert.Equal(3, second.Model.Ledger.Activities[id].Revision)

    // Its newest commit is Chrona's now: another device trusts it.
    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Empty(reader.Model.Store.Held)
    Assert.Empty(reader.Model.Store.Integrity)
    Assert.Equal("Edited on github.com", reader.Model.Ledger.Activities[id].Classification.Description)
    Assert.Equal(3, reader.Model.Ledger.Activities[id].Revision)

[<Fact>]
let ``an outside edit that claims a state only Chrona gives is never accepted, and stays held`` () =
    let github, second, id, _, _ =
        editedOutside (fun activity -> { described "Approved on github.com" activity with Review = Activity.Approved })

    let commits = github.State.History.Length
    second.Ui("acceptOutsideEdit", id, "")
    Assert.Equal<string list>([ "CHRONA.INTEGRITY.EXTERNAL_STATE_CLAIM" ], second.Model.Problems[OutsideEditForm] |> List.map code)
    Assert.Equal<string list>([ id ], second.Model.Store.Held |> List.map _.ActivityId)
    Assert.Equal(commits, github.State.History.Length)

[<Fact>]
let ``an outside edit changed again after it was reviewed is not accepted on the strength of the earlier review`` () =
    let github, second, id, original, write = editedOutside (described "First outside edit")
    write (described "Second outside edit" original)

    second.Ui("acceptOutsideEdit", id, "")

    match second.Model.Store.Conflicts with
    | [ { Divergences = [ Reconcile.ActivityChanged(mine, None) ] } ] -> Assert.Equal("First outside edit", mine.Classification.Description)
    | other -> failwith $"%A{other}"

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ id ], reader.Model.Store.Held |> List.map _.ActivityId)
    Assert.Equal("Second outside edit", reader.Model.Store.Held.Head.Classification.Description)
