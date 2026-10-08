/// Bootstrap administrators (WI-0053): only accounts the deployment's
/// configuration lists set an organization up or become its first
/// administrators; nothing is granted by opening an organization first
/// (requirements expansion 3).
module Chrona.Tests.GovernanceTests

open Xunit
open Arca
open Chrona.Domain
open Chrona.Domain.Governance

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private organization (administrators: string list) : Deployment.OrganizationConfig =
    { Id = "org_acme"
      DisplayName = "Acme Consulting"
      Slug = "acme"
      TimeZone = "UTC"
      Location = None
      Administrators = administrators }

let private nobody: Access.Roster = { OrganizationId = "org_acme"; Members = Map.empty }
let private person id : Access.Principal = { PrincipalId = id; Kind = Access.Human; DisplayName = id }

let private rosterOf (members: (string * Set<Access.Capability>) list) : Access.Roster =
    { OrganizationId = "org_acme"
      Members =
        members
        |> List.map (fun (id, capabilities) -> id, ({ Principal = person id; Capabilities = capabilities; Revision = 1 }: Access.Membership))
        |> Map.ofList }

[<Fact>]
let ``only a listed account sets a new organization up`` () =
    let listed = organization [ "583231" ]
    Assert.Equal(Found, decide EnvironmentKind.Production listed true nobody "github:583231")
    Assert.Equal(Refused "Only Acme Consulting's listed administrators can set it up.", decide EnvironmentKind.Production listed true nobody "github:1001")
    // An id that only looks like a listed one is not listed.
    Assert.True(match decide EnvironmentKind.Production listed true nobody "gitlab:583231" with Refused _ -> true | _ -> false)

[<Fact>]
let ``with no administrators listed, nothing is set up, except in an explicitly local environment`` () =
    let unlisted = organization []

    match decide EnvironmentKind.Production unlisted true nobody "github:583231" with
    | Refused reason ->
        Assert.Contains("has no administrators in this deployment's configuration", reason)
        Assert.Contains("Production organizations are set up only by the GitHub accounts the configuration lists", reason)
    | other -> failwith $"%A{other}"

    Assert.True(match decide EnvironmentKind.Staging unlisted true nobody "github:583231" with Refused _ -> true | _ -> false)
    Assert.Equal(Found, decide EnvironmentKind.Local unlisted true nobody "github:583231")
    // Listing anyone ends first-opener founding even locally.
    Assert.True(match decide EnvironmentKind.Local (organization [ "1" ]) true nobody "github:583231" with Refused _ -> true | _ -> false)

[<Fact>]
let ``an organization with no listed administrator is held, never granted, until a listed account confirms`` () =
    let listed = organization [ "583231" ]
    let unlistedAdmin = rosterOf [ "github:1001", Access.Grants.administrator ]

    Assert.Equal(NeedsConfirmation false, decide EnvironmentKind.Production listed false unlistedAdmin "github:1001")
    Assert.Equal(NeedsConfirmation true, decide EnvironmentKind.Production listed false unlistedAdmin "github:583231")
    Assert.Equal(NeedsConfirmation true, decide EnvironmentKind.Production listed false nobody "github:583231")
    // A listed account that is only a member is not an administrator from the list.
    Assert.Equal(NeedsConfirmation true, decide EnvironmentKind.Production listed false (rosterOf [ "github:583231", Access.Grants.ownTime ]) "github:583231")
    // With a listed administrator, everyone works as the roster says.
    Assert.Equal(Proceed, decide EnvironmentKind.Production listed false (rosterOf [ "github:583231", Access.Grants.administrator ]) "github:1001")

[<Fact>]
let ``the configuration lists administrators as GitHub account numbers`` () =
    let text (administrators: string) =
        $$"""{"environment":"production","environmentName":"production","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":""},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23li","redirectUri":"https://chrona.test/"},"organizations":[{"id":"org_acme","displayName":"Acme","slug":"acme","timeZone":"UTC","administrators":{{administrators}}}]}"""

    let config = Deployment.parse (text """["583231","1001"]""") |> ok
    Assert.Equal<string list>([ "583231"; "1001" ], config.Organizations.Head.Administrators)

    for bad in [ """["octocat"]"""; """["583231","583231"]"""; """[583231]"""; "\"583231\"" ] do
        Assert.True(Deployment.parse (text bad) |> Result.isError, bad)
