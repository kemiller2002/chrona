/// Reports on stored data, across people (WI-0039) and across organizations
/// (WI-0075).
[<Xunit.Collection("Store")>]
module Chrona.Tests.StoreReportTests

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

// ---- Reports on stored data, across people (WI-0039) ----------------------------------

[<Fact>]
let ``a report reads every month of its range from the stored records, not only the months already open`` () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "08:00" "08:30" "Setup"
    recordOn first "2026-08-03" "August work"

    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()
    Assert.DoesNotContain((2026, 8), second.Model.Store.Months)

    // A link to the summer's report opens cold and reads August's folder.
    second.Send(LocationMoved "#/reports?from=2026-07-01&to=2026-08-31")
    Assert.Contains((2026, 8), second.Model.Store.Months)
    Assert.Empty(second.Model.Store.Reading)
    Assert.Equal<string list>([ "August work" ], reportTitles second.Model)
    // The index is current, so July, which holds no time, is not read.
    Assert.True(second.Model.Store.IndexCurrent)
    Assert.DoesNotContain((2026, 7), second.Model.Store.Months)

[<Fact>]
let ``someone who may see the organization's time reports on everyone's, read from each person's months`` () =
    let github = InMemoryStore()
    let config = configuration "production"
    let founder = Device(github, RepositoryVisibility.Private, "production")
    founder.Open()
    record founder "08:00" "08:30" "Founder's own"
    founder.Ui("memberId", "1001")
    founder.Ui("memberName", "hubot")
    founder.Ui("memberAccess", "ownTime")
    founder.Ui("admitMember", "")

    let colleague = Device(github, RepositoryVisibility.Private, "production", hubot, config)
    colleague.Open()
    recordOn colleague "2026-08-04" "Hubot in August"
    recordOn colleague "2026-10-06" "Hubot in October"

    // The administrator, on another device, opens a link to everyone's time.
    let lead = Device(github, RepositoryVisibility.Private, "production")
    lead.Open()
    Assert.DoesNotContain("Hubot in October", reportTitles lead.Model)
    lead.Send(LocationMoved "#/reports?from=2026-08-01&to=2026-10-31&people=everyone&group=person")
    Assert.Equal(None, lead.Model.RouteProblem)
    Assert.Contains(("github:1001", (2026, 8)), lead.Model.Store.Others)
    Assert.Contains(("github:1001", (2026, 10)), lead.Model.Store.Others)
    Assert.Empty(lead.Model.Store.OthersReading)
    Assert.Equal<string list>([ "Founder's own"; "Hubot in August"; "Hubot in October" ], reportTitles lead.Model)
    Assert.Contains("github:1001", UpdateReports.exportText lead.Model)
    Assert.Contains("Everyone in the organization", viewText "printSubtitle" lead.Model)

    // One person's time among everyone's.
    lead.Ui("reportPerson", "github:1001")
    Assert.Equal<string list>([ "Hubot in August"; "Hubot in October" ], reportTitles lead.Model)
    Assert.Equal(Some "github:1001", (match lead.Model.Place with Places.Reports query -> query.Person | _ -> None))

    // Someone the organization does not have is a clear not-found.
    lead.Send(LocationMoved "#/reports?people=everyone&person=github%3A9999")
    Assert.Equal(Some(RecordMissing("person", "github:9999")), lead.Model.RouteProblem)

    // The colleague may see only their own time: everyone's is not permitted,
    // and their own report never includes anyone else's.
    colleague.Send(LocationMoved "#/reports?from=2026-08-01&to=2026-10-31&people=everyone")
    Assert.Equal(Some(AddressProblem(Limen.Routing.RouteError.NotPermitted Places.Names.Reports)), colleague.Model.RouteProblem)
    Assert.Empty(colleague.Model.Store.Others)
    colleague.Send(LocationMoved "#/reports?from=2026-08-01&to=2026-10-31")
    Assert.Equal<string list>([ "Hubot in August"; "Hubot in October" ], reportTitles colleague.Model)

// ---- Reports across organizations (WI-0075) ---------------------------------------------

[<Fact>]
let ``a report across organizations reads each organization the person belongs to, as they may see it there`` () =
    let github = InMemoryStore()

    let config =
        """{"environment":"production","environmentName":"production","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":"deployments"},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://chrona.test/"},"organizations":[{"id":"org_acme","displayName":"Acme Consulting","slug":"acme","timeZone":"America/New_York","administrators":["583231"]},{"id":"org_eu","displayName":"Acme Europe","slug":"acme-eu","timeZone":"Europe/Berlin","administrators":["583231"]},{"id":"org_jp","displayName":"Acme Japan","slug":"acme-jp","timeZone":"Asia/Tokyo","administrators":["1001"]}]}"""
        |> Deployment.parse
        |> ok

    // Octocat keeps time in Acme Consulting and in Acme Europe, where he
    // administers and admits hubot.
    let octo = Device(github, RepositoryVisibility.Private, "production", octocat, config)
    octo.Open()
    record octo "09:00" "10:00" "Acme work"
    octo.Ui("chooseOrganization", "org_eu")
    record octo "11:00" "12:00" "Europe work"
    octo.Ui("memberId", "1001")
    octo.Ui("memberName", "hubot")
    octo.Ui("memberAccess", "ownTime")
    octo.Ui("admitMember", "")

    // Hubot keeps time in Acme Europe, and founds Acme Japan, which
    // octocat does not belong to.
    let bot = Device(github, RepositoryVisibility.Private, "production", hubot, config)
    bot.Open()
    bot.Ui("chooseOrganization", "org_eu")
    record bot "13:00" "14:00" "Hubot in Europe"
    bot.Ui("chooseOrganization", "org_jp")
    record bot "15:00" "16:00" "Japan work"

    // Octocat, in Acme Consulting, opens a link to everyone's time in all
    // his organizations, grouped by organization.
    let lead = Device(github, RepositoryVisibility.Private, "production", octocat, config)
    lead.Open()
    lead.Send(LocationMoved "#/reports?from=2026-10-01&to=2026-10-31&people=everyone&organizations=all&group=organization")
    Assert.Equal(None, lead.Model.RouteProblem)
    Assert.Empty(lead.Model.ElsewhereReading)
    Assert.Equal<string list>([ "Acme work"; "Europe work"; "Hubot in Europe" ], reportTitles lead.Model)

    // Acme Japan is read, but he is not a member: nothing of it is here, and
    // the report says so.
    Assert.False(lead.Model.Elsewhere["org_jp"].Member)
    Assert.True(viewFlag "hasReportOrganizationsNote" lead.Model)
    Assert.Contains("not a member of Acme Japan", viewText "reportOrganizationsNote" lead.Model)

    // Grouped by organization, named as the deployment names them.
    let groups =
        match (Project.project lead.Model |> Map.ofList)["reportGroups"] with
        | Chrona.Engine.View.Items rows ->
            rows
            |> List.choose (
                List.tryPick (function
                    | "name", Chrona.Engine.View.Text name -> Some name
                    | _ -> None)
            )
        | other -> failwith $"%A{other}"

    Assert.Equal<string list>([ "Acme Consulting"; "Acme Europe" ], groups)

    // The export names both organizations, and holds Acme Europe's time.
    let export = UpdateReports.exportText lead.Model
    Assert.Contains("# organization: org_acme;org_eu", export)
    Assert.Contains("# filter.organizations: org_acme;org_eu", export)
    Assert.Contains("Hubot in Europe", export)

    // Only his own time, in every organization.
    lead.Ui("reportPeople", "mine")
    Assert.Equal<string list>([ "Acme work"; "Europe work" ], reportTitles lead.Model)

    // This organization only, as before.
    lead.Ui("reportOrganizations", "this")
    Assert.Equal<string list>([ "Acme work" ], reportTitles lead.Model)

[<Fact>]
let ``across organizations, someone who sees only their own time in one sees only theirs from it`` () =
    let github = InMemoryStore()

    let config =
        """{"environment":"production","environmentName":"production","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":"deployments"},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://chrona.test/"},"organizations":[{"id":"org_acme","displayName":"Acme Consulting","slug":"acme","timeZone":"America/New_York","administrators":["583231"]},{"id":"org_eu","displayName":"Acme Europe","slug":"acme-eu","timeZone":"Europe/Berlin","administrators":["1001"]}]}"""
        |> Deployment.parse
        |> ok

    // Hubot founds Acme Europe, records his time there, and admits octocat
    // to keep his own time only.
    let bot = Device(github, RepositoryVisibility.Private, "production", hubot, config)
    bot.Open()
    bot.Ui("chooseOrganization", "org_eu")
    record bot "13:00" "14:00" "Hubot in Europe"
    bot.Ui("memberId", "583231")
    bot.Ui("memberName", "octocat")
    bot.Ui("memberAccess", "ownTime")
    bot.Ui("admitMember", "")

    let octo = Device(github, RepositoryVisibility.Private, "production", octocat, config)
    octo.Open()
    record octo "09:00" "10:00" "Acme work"
    octo.Ui("chooseOrganization", "org_eu")
    record octo "11:00" "12:00" "Europe work"
    octo.Ui("chooseOrganization", "org_acme")

    octo.Send(LocationMoved "#/reports?from=2026-10-01&to=2026-10-31&people=everyone&organizations=all")
    Assert.True(octo.Model.Elsewhere["org_eu"].Member)
    Assert.False(octo.Model.Elsewhere["org_eu"].SeesOrganization)
    // His own time from Acme Europe; never hubot's.
    Assert.Equal<string list>([ "Acme work"; "Europe work" ], reportTitles octo.Model)
    Assert.Contains("only your own time", viewText "reportOrganizationsNote" octo.Model)
