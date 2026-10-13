/// The store's organizations: members, rosters and reference data shared
/// across devices, one organization at a time, and the bootstrap
/// administrators the deployment lists (WI-0031, WI-0053).
[<Xunit.Collection("Store")>]
module Chrona.Tests.StoreOrganizationTests

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

// ---- Rosters, people and organizations (WI-0031) -------------------------------------

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
