/// Deep links in the engine (CHX-460, WI-0071): an address that needs sign-in
/// keeps its target through the GitHub round trip, a return target is never
/// another site, a place the person may not see is refused clearly, and no
/// address or link carries a sign-in code or token.
module Chrona.Tests.DeepLinkTests

open System
open Xunit
open Chrona.Domain
open Chrona.Engine.View
open Chrona.Engine.App
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update

let private start = DateTimeOffset(2026, 10, 8, 14, 10, 0, TimeSpan.FromHours -4.0)
let private ids = ref 0

let private ctx =
    { Now = start
      NewId = fun prefix -> $"{prefix}-{Threading.Interlocked.Increment ids}" }

let private step msg (model: Model, _) = update ctx msg model
let private ui name value = Ui(name, None, value, None)

let private view (model: Model) = Project.project model |> Map.ofList

let private flagOf key (model: Model) =
    match (view model)[key] with
    | Value(Flag f) -> f
    | other -> failwith $"{key} is {other}"

let private textOf key (model: Model) =
    match (view model)[key] with
    | Value(Text t) -> t
    | other -> failwith $"{key} is {other}"

let private replace location = Navigate(Limen.Routing.NavigationEffect.Replace location)

let private signInConfiguration =
    """{"environment":"test","environmentName":"test","identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"http://127.0.0.1:4321/web/index.html"}}"""

let private localConfiguration = """{"environment":"local","environmentName":"local"}"""

let private octocat =
    { ActorId = "github:583231"
      OrganizationId = "local"
      DisplayName = "octocat"
      Kind = SignedIn "github" }

let private callback = [ "code", "one-time-code"; "state", "opaque-state" ]

/// A page opened at an address of a deployment that signs in.
let private openedAt (fragment: string) (query: (string * string) list) =
    (Model.initial noOne InMemory start, [])
    |> step (Started(Support.testPage, fragment, query))
    |> step (EnvironmentDescribed "America/New_York")

let private target = "/reports?from=2026-10-01&to=2026-10-31&q=pairing%20review"

[<Fact>]
let ``a deep link that needs sign-in goes to sign-in with its target, and the GitHub round trip lands there`` () =
    // A cold open of a report, signed out.
    let model, _ = openedAt $"#{target}" [] |> step (ConfigurationRead(Some signInConfiguration))
    let model, effects = (model, []) |> step (IdentityChanged(SignedOutWith None))
    let signIn = "/sign-in?returnTo=%2Freports%3Ffrom%3D2026-10-01%26to%3D2026-10-31%26q%3Dpairing%2520review"
    Assert.Equal<Effect list>([ replace signIn ], effects)
    Assert.Equal(Places.SignIn(Some target), model.Place)
    Assert.True(flagOf "screenSignIn" model)

    // Signing in keeps the target in this tab before leaving for GitHub.
    let _, effects = (model, []) |> step (ui "signIn" "")
    Assert.Equal<Effect list>([ KeepReturn(Some target); SignIn ThisPage ], effects)

    // GitHub returns to the deployment's address, with no fragment: the page
    // reads the target this tab kept.
    let _, effects = update ctx (Started(Support.testPage, "", callback)) (Model.initial noOne InMemory start)
    Assert.Contains(ReadReturn, effects)
    let back, _ = openedAt "" callback
    Assert.True(back.Identity.ReadingReturn)
    let back, effects = (back, []) |> step (ReturnRead(Some target))
    // Read once: forgotten from the tab at once.
    Assert.Equal<Effect list>([ KeepReturn None ], effects)
    let back, _ = (back, []) |> step (ConfigurationRead(Some signInConfiguration))
    let landed, effects = (back, []) |> step (IdentityChanged(SignedInAs octocat))
    // A replace, so Back never returns to the sign-in page.
    Assert.Contains(replace target, effects)
    Assert.Equal(Places.Reports { Places.allReports with From = Some(DateOnly(2026, 10, 1)); To = Some(DateOnly(2026, 10, 31)); Text = Some "pairing review" }, landed.Place)
    Assert.Equal(Some target, landed.Router.Current)
    Assert.True(flagOf "screenReports" landed)
    Assert.Equal("pairing review", landed.Report.Text)
    Assert.Equal(None, landed.Identity.ReturnTo)

[<Fact>]
let ``the target is resumed only once it is read, whichever arrives first`` () =
    let back, _ = openedAt "" callback |> step (ConfigurationRead(Some signInConfiguration))
    // Signed in before the tab's target was read: nothing moves yet.
    let signedIn, effects = (back, []) |> step (IdentityChanged(SignedInAs octocat))
    Assert.DoesNotContain(effects, (function Navigate _ -> true | _ -> false))
    let landed, effects = (signedIn, []) |> step (ReturnRead(Some "/track"))
    Assert.Contains(replace "/track", effects)
    Assert.Equal(Places.Track, landed.Place)

[<Fact>]
let ``a failed sign-in keeps the target for the next attempt`` () =
    let back, _ = openedAt "" callback |> step (ReturnRead(Some "/track")) |> step (ConfigurationRead(Some signInConfiguration))
    let refused, effects = (back, []) |> step (IdentityChanged(SignedOutWith(Some "state_invalid")))
    Assert.Contains(replace "/sign-in?returnTo=%2Ftrack", effects)
    let _, effects = (refused, []) |> step (ui "signIn" "")
    Assert.Equal<Effect list>([ KeepReturn(Some "/track"); SignIn ThisPage ], effects)

[<Fact>]
let ``a return target is only ever one of Chrona's own places`` () =
    for kept in [ "//evil.example/track"; "https://evil.example/"; "/sign-in?returnTo=%2Ftrack"; "/nowhere"; "track"; "/day/2026-10-08\\.." ] do
        let back, _ = openedAt "" callback |> step (ReturnRead(Some kept)) |> step (ConfigurationRead(Some signInConfiguration))
        Assert.Equal(None, back.Identity.ReturnTo)
        let landed, _ = (back, []) |> step (IdentityChanged(SignedInAs octocat))
        Assert.Equal(Some "/", landed.Router.Current)
        Assert.Equal(Places.Today, landed.Place)

[<Fact>]
let ``a sign-in address in a deployment without sign-in goes straight to its target`` () =
    let model, effects = openedAt "#/sign-in?returnTo=%2Ftrack" [] |> step (ConfigurationRead(Some localConfiguration))
    Assert.Contains(replace "/track", effects)
    Assert.Equal(Places.Track, model.Place)

[<Fact>]
let ``no address or copied link carries a sign-in code, state or token`` () =
    let back, _ =
        openedAt "" callback
        |> step (ReturnRead None)
        |> step (ConfigurationRead(Some signInConfiguration))
        |> step (IdentityChanged(SignedInAs octocat))

    let _, effects = (back, []) |> step (ui "copyLink" "")

    match effects with
    | [ CopyText(LinkCopy, link) ] ->
        Assert.Equal("http://127.0.0.1:4321/web/index.html#/day/2026-10-08", link)
        Assert.DoesNotContain("code", link)
        Assert.DoesNotContain("state", link)
    | other -> failwith $"{other}"

    Assert.DoesNotContain("code", back.Router.Current |> Option.defaultValue "")

// ---- not permitted ------------------------------------------------------------------

/// Working locally, with a project, an activity type and one entry, as the
/// organization's founder.
let private working () =
    (Model.initial Chrona.Application.App.localSession InMemory start, [])
    |> step (Started(Support.testPage, "#/", []))
    |> step (EnvironmentDescribed "America/New_York")
    |> step (ConfigurationRead(Some localConfiguration))
    |> step (ui "newProjectName" "HelixNote")
    |> step (ui "addProject" "")
    |> step (ui "newActivityTypeName" "Research")
    |> step (ui "addActivityType" "")
    |> fst

let private withAccess (grant: Set<Access.Capability>) (model: Model) =
    let me = model.Session.ActorId
    let membership = model.Roster.Members[me]
    { model with Roster = { model.Roster with Members = model.Roster.Members.Add(me, { membership with Capabilities = grant }) } }

[<Fact>]
let ``the administrators' parts of More are refused clearly to everyone else`` () =
    let member' = withAccess Access.Grants.ownTime (working ())

    for address in [ "#/more/people"; "#/more/index" ] do
        let refused, effects = (member', []) |> step (LocationMoved address)
        Assert.Equal<Effect list>([], effects)
        Assert.True(flagOf "screenProblem" refused, address)
        Assert.Equal("Not permitted", textOf "problemTitle" refused)
        Assert.Equal("not-permitted", textOf "problemKind" refused)
        Assert.False(flagOf "screenMore" refused)

    // Their other parts, and every part for an administrator, open.
    let references, _ = (member', []) |> step (LocationMoved "#/more/references")
    Assert.True(flagOf "screenMore" references)
    let administrator, _ = (working (), []) |> step (LocationMoved "#/more/people")
    Assert.True(flagOf "screenMore" administrator)

[<Fact>]
let ``someone else's activity is refused, not shown`` () =
    let model = working ()
    let projectId = (Reference.selectable Reference.Project model.References |> List.head).Id
    let typeId = (Reference.selectable Reference.ActivityType model.References |> List.head).Id

    let recorded, _ =
        (model, [])
        |> step (ui "manualActivityType" typeId)
        |> step (ui "manualProject" projectId)
        |> step (ui "manualStartDate" "2026-10-08")
        |> step (ui "manualStartTime" "09:00")
        |> step (ui "manualEndTime" "10:00")
        |> step (ui "manualDescription" "Pairing")
        |> step (ui "manualPurpose" "Delivery")
        |> step (ui "saveManual" "")

    let id, activity = recorded.Ledger.Activities |> Map.toList |> List.head
    let theirs = { recorded with Ledger = { recorded.Ledger with Activities = recorded.Ledger.Activities.Add(id, { activity with ActorId = "github:42" }) } }
    let refused, _ = (theirs, []) |> step (LocationMoved $"#/entries/{id}?on=2026-10-08")
    Assert.True(flagOf "screenProblem" refused)
    Assert.Equal("Not permitted", textOf "problemTitle" refused)
    Assert.False(flagOf "screenActivity" refused)
    // Their own opens.
    let mine, _ = (recorded, []) |> step (LocationMoved $"#/entries/{id}?on=2026-10-08")
    Assert.True(flagOf "screenActivity" mine)
    Assert.Equal("Pairing", textOf "detailTitle" mine)

/// A candidate an observation made (WI-0038), of this person or of someone else.
let private observedBy (actorId: string option) (id: string) : Observations.Candidate =
    let at = DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.FromHours -4.0)

    { CandidateId = id
      Observation =
        { ObservationId = id
          SourceSystem = "github"
          OrganizationId = "local"
          ProjectId = "PRJ-1"
          ActorId = actorId
          WorkItemId = None
          ExternalUrl = None
          Timing = Observations.ObservedDuration 30
          Description = Some "Reviewed a pull request"
          Evidence = []
          ObservedAt = at }
      ReceivedAt = at
      Disposition = Observations.Pending
      Revision = 1
      Decisions = [] }

[<Fact>]
let ``a candidate's link opens it for its person, is refused to anyone else, and one that does not exist is not found`` () =
    let model = working ()

    let observed =
        { model with
            Candidates =
                [ observedBy (Some model.Session.ActorId) "CAND-mine"
                  observedBy None "CAND-unassigned"
                  observedBy (Some "github:42") "CAND-theirs" ] }

    for id in [ "CAND-mine"; "CAND-unassigned" ] do
        let opened, _ = (observed, []) |> step (LocationMoved $"#/candidates/{id}")
        Assert.True(flagOf "screenCandidate" opened, id)
        Assert.Equal("Reviewed a pull request", textOf "candidateTitle" opened)
        Assert.Equal("Reviewed a pull request", opened.CandidateDraft.Description)
        Assert.True(flagOf "candidateDecidable" opened)

    let refused, _ = (observed, []) |> step (LocationMoved "#/candidates/CAND-theirs")
    Assert.True(flagOf "screenProblem" refused)
    Assert.Equal("Not permitted", textOf "problemTitle" refused)
    Assert.False(flagOf "screenCandidate" refused)

    let missing, _ = (observed, []) |> step (LocationMoved "#/candidates/CAND-nowhere")
    Assert.True(flagOf "screenProblem" missing)
    Assert.Equal("not-found", textOf "problemKind" missing)

    // The list shows only what the person may decide, awaiting first.
    let listed, _ = (observed, []) |> step (LocationMoved "#/candidates")
    Assert.True(flagOf "screenCandidates" listed)

    let ids =
        match view listed |> Map.find "candidateList" with
        | Chrona.Engine.View.Items rows -> rows |> List.map (fun row -> row |> List.pick (function "id", Chrona.Engine.View.Text id -> Some id | _ -> None))
        | other -> failwith $"%A{other}"

    Assert.Equal<string list>([ "CAND-mine"; "CAND-unassigned" ], ids)

// ---- the organization in the address -------------------------------------------------

let private twoOrganizations =
    """{"environment":"test","environmentName":"test","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":""},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"http://127.0.0.1:4321/web/index.html"},"organizations":[{"id":"org_a","displayName":"A","slug":"a","timeZone":"UTC","administrators":["583231"]},{"id":"org_b","displayName":"B","slug":"b","timeZone":"UTC","administrators":["583231"]}]}"""

let private oneOrganization =
    """{"environment":"test","environmentName":"test","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":""},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"http://127.0.0.1:4321/web/index.html"},"organizations":[{"id":"org_a","displayName":"A","slug":"a","timeZone":"UTC","administrators":["583231"]}]}"""

/// A page opened at an address, signed in to a deployment.
let private signedInAt (configuration: string) (fragment: string) =
    openedAt fragment [] |> step (ConfigurationRead(Some configuration)) |> step (IdentityChanged(SignedInAs octocat))

let private opensFor (effects: Effect list) =
    effects |> List.choose (function OpenStore(_, session, _) -> Some session.OrganizationId | _ -> None)

[<Fact>]
let ``where a deployment serves several organizations, the address names the one worked in`` () =
    let model, effects = signedInAt twoOrganizations "#/track"
    // The first organization, named in the address, corrected in place.
    Assert.Equal("org_a", model.Session.OrganizationId)
    Assert.Contains(replace "/track?org=org_a", effects)
    // Once the records are open, going somewhere names it too.
    let opened, _ =
        (model, [])
        |> step (
            StoreOpened
                { Name = "acme/chrona-data"
                  Cached = None
                  Activities = []
                  References = []
                  Attestations = []
                  Members = [ { Principal = principalOf model.Session; Capabilities = Access.Grants.administrator; Revision = 1 } ]
                  Held = []
                  Audit = []
                  Problems = []
                  Months = []
                  History = []
                  Index = ""
                  Periods = None
                  Reviews = []
                  Reviewing = []
                  Candidates = [] }
        )

    let _, effects = (opened, []) |> step (ui "goMore" "")
    Assert.Contains(Navigate(Limen.Routing.NavigationEffect.Push "/more?org=org_a"), effects)
    // Choosing the other organization goes to its home, named by it.
    let chosen, effects = (opened, []) |> step (ui "chooseOrganization" "org_b")
    Assert.Equal("org_b", chosen.Session.OrganizationId)
    Assert.Contains(Navigate(Limen.Routing.NavigationEffect.Push "/?org=org_b"), effects)

[<Fact>]
let ``a link into another organization opens that organization`` () =
    // Signing in at a link opens the organization it names, not the first.
    let model, effects = signedInAt twoOrganizations "#/day/2026-10-08?org=org_b"
    Assert.Equal("org_b", model.Session.OrganizationId)
    Assert.Equal<string list>([ "org_b" ], opensFor effects)
    Assert.Equal(Some "/day/2026-10-08?org=org_b", model.Router.Current)
    // A link followed while working in another: the page moves there.
    let moved, effects = (model, []) |> step (LocationMoved "#/track?org=org_a")
    Assert.Equal("org_a", moved.Session.OrganizationId)
    Assert.Equal<string list>([ "org_a" ], opensFor effects)
    Assert.Equal(Places.Track, moved.Place)

[<Fact>]
let ``an organization the deployment does not serve is not found`` () =
    let model, _ = signedInAt twoOrganizations "#/track?org=org_none"
    Assert.True(flagOf "screenProblem" model)
    Assert.Equal("Nothing in your records is the organization with the id \"org_none\". It may have been removed, or the link may be wrong.", textOf "problemDetail" model)
    // Where it serves one, the address names none: another is not found, its own is dropped.
    let single, _ = signedInAt oneOrganization "#/track?org=org_b"
    Assert.True(flagOf "screenProblem" single)
    let own, effects = signedInAt oneOrganization "#/track?org=org_a"
    Assert.Contains(replace "/track", effects)
    Assert.Equal(Places.Track, own.Place)

[<Fact>]
let ``the organization goes through sign-in inside the return target`` () =
    let model, _ = openedAt "#/track?org=org_b" [] |> step (ConfigurationRead(Some twoOrganizations))
    let model, effects = (model, []) |> step (IdentityChanged(SignedOutWith None))
    Assert.Contains(replace "/sign-in?returnTo=%2Ftrack%3Forg%3Dorg_b", effects)
    let _, effects = (model, []) |> step (ui "signIn" "")
    Assert.Contains(KeepReturn(Some "/track?org=org_b"), effects)
