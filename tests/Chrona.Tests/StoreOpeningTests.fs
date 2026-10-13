/// The store on Arca (WI-0032), opening and committing: a repository set up
/// on first sign-in, records committed and read back on another device,
/// conflicts decided on what is stored, what is read checked, and the GitHub
/// backend's transport through the bridge (2.4 to 2.7, 21, 22, 39, 41).
[<Xunit.Collection("Store")>]
module Chrona.Tests.StoreOpeningTests

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
let ``scenario 41: production records are never started in a public repository`` () =
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
let ``scenario 34: independent changes from two devices both land, and each device is shown the other's`` () =
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
let ``scenario 31: a commit whose outcome GitHub did not report is reconciled, not sent again`` () =
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
        StoreGitHub.gitHub
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
