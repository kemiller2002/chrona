/// Principals, membership and capability-based authorization (WI-0030):
/// requirements expansion 3 and the authorization diagnostics of 26.
module Chrona.Tests.AccessTests

open System
open Xunit
open Chrona.Domain
open Chrona.Domain.Access
open Chrona.Domain.Diagnostics
open Chrona.Engine.View
open Chrona.Engine.App
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update

let private person id = { PrincipalId = id; Kind = Human; DisplayName = id }
let private agent id = { PrincipalId = id; Kind = Agent; DisplayName = id }

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private codes =
    function
    | Ok _ -> []
    | Error diagnostics -> diagnostics |> List.map code

let private refusal =
    function
    | Ok _ -> "ok"
    | Error diagnostic -> code diagnostic

let private acme = founded "org_acme" (person "github:1")

// ---- Membership and capabilities (3) ----------------------------------------------

[<Fact>]
let ``the founder administers the organization; others are refused by capability, membership or organization`` () =
    for capability in allCapabilities do
        Assert.Equal("ok", authorize acme "org_acme" "github:1" capability |> refusal)

    Assert.Equal("CHRONA.AUTH.NOT_A_MEMBER", authorize acme "org_acme" "github:2" RecordOwnTime |> refusal)
    Assert.Equal("CHRONA.AUTH.ORGANIZATION_MISMATCH", authorize acme "org_other" "github:1" RecordOwnTime |> refusal)

    let roster = acme |> execute "github:1" (Admit(person "github:2", Grants.ownTime)) |> ok
    Assert.Equal("ok", authorize roster "org_acme" "github:2" RecordOwnTime |> refusal)
    Assert.Equal("CHRONA.AUTH.UNAUTHORIZED_CAPABILITY", authorize roster "org_acme" "github:2" ManageProjects |> refusal)
    Assert.Equal(UnauthorizedCapability "ApproveTime" |> Error, authorize roster "org_acme" "github:2" ApproveTime)

[<Fact>]
let ``checks are by capability, never by role: templates only fill in capability sets`` () =
    Assert.True(Grants.ownTime.IsSubsetOf Grants.reviewer)
    Assert.True(Grants.reviewer.IsSubsetOf Grants.administrator)
    Assert.Equal(15, Grants.administrator.Count)

    // A reviewer who is then given ManageTags can manage tags, and nothing else of an administrator's.
    let roster =
        acme
        |> execute "github:1" (Admit(person "github:3", Grants.reviewer))
        |> Result.bind (execute "github:1" (Grant("github:3", ManageTags)))
        |> ok

    Assert.True(Access.permits roster "github:3" ManageTags)
    Assert.True(Access.permits roster "github:3" ApproveTime)
    Assert.False(Access.permits roster "github:3" ManageProjects)

[<Fact>]
let ``agents, services and integrations never vouch: attestation, approval and rejection are a person's`` () =
    Assert.Equal<Set<Capability>>(Grants.administrator - personOnly, Grants.forKind Agent Grants.administrator)

    Assert.Equal<string list>(
        [ "CHRONA.AUTH.CAPABILITY_NOT_FOR_KIND" ],
        acme |> execute "github:1" (Admit(agent "anthropic/claude-code", set [ RecordOwnTime; ApproveTime ])) |> codes
    )

    let roster = acme |> execute "github:1" (Admit(agent "anthropic/claude-code", set [ RecordOwnTime ])) |> ok
    Assert.Equal<string list>([ "CHRONA.AUTH.CAPABILITY_NOT_FOR_KIND" ], roster |> execute "github:1" (Grant("anthropic/claude-code", AttestOwnDay)) |> codes)

    // An agent founding an organization administers it without the person-only capabilities.
    let agents = founded "org_bots" (agent "svc:1")
    Assert.False(Access.permits agents "svc:1" ApproveTime)
    Assert.True(Access.permits agents "svc:1" ManageOrganizationSettings)

[<Fact>]
let ``only someone who manages the organization changes its roster, and it is never left without one`` () =
    let roster = acme |> execute "github:1" (Admit(person "github:2", Grants.ownTime)) |> ok

    Assert.Equal<string list>([ "CHRONA.AUTH.UNAUTHORIZED_CAPABILITY" ], roster |> execute "github:2" (Grant("github:2", ManageProjects)) |> codes)
    Assert.Equal<string list>([ "CHRONA.AUTH.NOT_A_MEMBER" ], roster |> execute "github:9" (Remove "github:2") |> codes)
    Assert.Equal<string list>([ "CHRONA.AUTH.ALREADY_A_MEMBER" ], roster |> execute "github:1" (Admit(person "github:2", Set.empty)) |> codes)

    // The only administrator can neither drop the capability nor leave.
    Assert.Equal<string list>([ "CHRONA.AUTH.LAST_ADMINISTRATOR" ], roster |> execute "github:1" (Revoke("github:1", ManageOrganizationSettings)) |> codes)
    Assert.Equal<string list>([ "CHRONA.AUTH.LAST_ADMINISTRATOR" ], roster |> execute "github:1" (Remove "github:1") |> codes)

    // With a second administrator, the first may step down.
    let handedOver =
        roster
        |> execute "github:1" (Grant("github:2", ManageOrganizationSettings))
        |> Result.bind (execute "github:1" (Remove "github:1"))
        |> ok

    Assert.Equal<string list>([ "github:2" ], handedOver.Members |> Map.keys |> List.ofSeq)

[<Fact>]
let ``a person may belong to several organizations, each with its own roster`` () =
    let globex = founded "org_globex" (person "github:2") |> execute "github:2" (Admit(person "github:1", Grants.ownTime)) |> ok
    Assert.Equal<string list>([ "org_acme"; "org_globex" ], organizationsOf [ acme; globex ] "github:1")
    Assert.True(Access.permits acme "github:1" ManageProjects)
    Assert.False(Access.permits globex "github:1" ManageProjects)

// ---- The engine checks every command (3) -----------------------------------------------

let private start = DateTimeOffset(2026, 10, 8, 14, 10, 0, TimeSpan.FromHours -4.0)
let private ids = ref 0

let private ctx =
    { Now = start
      NewId = fun prefix -> $"{prefix}-{Threading.Interlocked.Increment ids}" }

let private send msg (model: Model, _) = update ctx msg model
let private ui name value = Ui(name, None, value, None)

let private session =
    { ActorId = "github:1"
      OrganizationId = "local"
      DisplayName = "octocat"
      Kind = SignedIn "github" }

/// Working, with a project and an activity type, as the organization's founder.
let private working () =
    (Model.initial session InMemory start, [])
    |> send (Started(Support.testPage, "", []))
    |> send (EnvironmentDescribed "America/New_York")
    |> send (ConfigurationRead(Some """{"environment":"local","environmentName":"local"}"""))
    |> send (ui "newProjectName" "HelixNote")
    |> send (ui "addProject" "")
    |> send (ui "newActivityTypeName" "Research")
    |> send (ui "addActivityType" "")
    |> fst

/// The same person, now holding only these capabilities.
let private holding (capabilities: Capability list) (model: Model) =
    let roster = model.Roster

    { model with
        Roster =
            { roster with
                Members = roster.Members.Add(session.ActorId, { roster.Members[session.ActorId] with Capabilities = set capabilities }) } }

let private problemCodes (key: string) (model: Model) =
    match Project.project model |> Map.ofList |> Map.find key with
    | Items rows -> rows |> List.map (fun row -> row |> List.find (fst >> (=) "code") |> snd)
    | other -> failwith $"{other}"

[<Fact>]
let ``a command needs its capability; a refusal changes nothing and stores nothing`` () =
    let model = working () |> holding (List.ofSeq Grants.ownTime)
    let before = model.References

    let refused, effects = (model, []) |> send (ui "newTagName" "Backend") |> send (ui "addTag" "")
    Assert.Empty(effects)
    Assert.Equal(before, refused.References)
    Assert.Equal<Scalar list>([ Text "CHRONA.AUTH.UNAUTHORIZED_CAPABILITY" ], problemCodes "referenceProblems" refused)
    Assert.Equal("You do not have permission to manage tags in this organization.", Project.describe refused (UnauthorizedCapability "ManageTags"))

    // Settings and exports are refused where they were asked for.
    let settings, _ = (model, []) |> send (ui "periodCadence" "monthly")
    Assert.Equal(Periods.Weekly, settings.PeriodConfig.Cadence)
    Assert.Equal<Scalar list>([ Text "CHRONA.AUTH.UNAUTHORIZED_CAPABILITY" ], problemCodes "periodProblems" settings)

    let noExport, effects = (holding [ ViewOwnTime ] model, []) |> send (ui "copyExport" "")
    Assert.Empty(effects)
    Assert.Equal<Scalar list>([ Text "CHRONA.AUTH.UNAUTHORIZED_CAPABILITY" ], problemCodes "exportProblems" noExport)

    // Keeping one's own time is allowed.
    let timer, _ = (model, []) |> send (ui "timerActivityType" (Reference.selectable Reference.ActivityType model.References).Head.Id) |> send (ui "timerProject" (Reference.selectable Reference.Project model.References).Head.Id) |> send (ui "startTimer" "")
    Assert.True(match timer.Timer with Timer.Running _ -> true | _ -> false)

    // Without RecordOwnTime the timer does not start.
    let stopped, _ = (holding [ ViewOwnTime ] model, []) |> send (ui "startTimer" "")
    Assert.Equal(Timer.Idle, stopped.Timer)
    Assert.Equal<Scalar list>([ Text "CHRONA.AUTH.UNAUTHORIZED_CAPABILITY" ], problemCodes "timerProblems" stopped)

[<Fact>]
let ``what the person may do is shown, and without ViewOwnTime their time is not`` () =
    let model = working ()
    let view = Project.project model |> Map.ofList

    match view["accessCapabilities"] with
    | Items rows -> Assert.Equal(15, rows.Length)
    | other -> failwith $"{other}"

    let limited = model |> holding [ RecordOwnTime ]

    match (Project.project limited |> Map.ofList)["accessCapabilities"] with
    | Items [ row ] -> Assert.Contains(("name", Text "record your time"), row)
    | other -> failwith $"{other}"

    // Recorded time exists, but the person may not see it.
    let recorded, _ =
        (model, [])
        |> send (ui "timerActivityType" (Reference.selectable Reference.ActivityType model.References).Head.Id)
        |> send (ui "timerProject" (Reference.selectable Reference.Project model.References).Head.Id)
        |> send (ui "startTimer" "")

    Assert.True(match (Project.project recorded |> Map.ofList)["showTimerChip"] with Value(Flag shown) -> shown | _ -> false)
    Assert.False(match (Project.project (recorded |> holding [ RecordOwnTime ]) |> Map.ofList)["showTimerChip"] with Value(Flag shown) -> shown | _ -> true)

[<Fact>]
let ``every command the page can send that changes something names the capability it needs`` () =
    let changing =
        [ "startTimer"; "pauseTimer"; "resumeTimer"; "stopTimer"; "saveCompletion"; "saveManual"
          "addProject"; "addActivityType"; "addTag"; "referenceActive"
          "saveAmend"; "voidActivity"; "restoreActivity"; "saveSplit"; "attachEvidence"; "unlinkEvidence"; "saveMerge"
          "attestDay"; "periodCadence"; "periodWeekStart"; "copyExport"; "printReport" ]

    for name in changing do
        Assert.True((requirement name None).IsSome, $"{name} names no capability")

    Assert.Equal(Some(ManageTags, ReferenceForm), requirement "referenceActive" (Some "tag:TAG-1"))
    Assert.Equal(Some(ManageActivityTypes, ReferenceForm), requirement "referenceActive" (Some "activityType:TYP-1"))
    Assert.Equal(None, requirement "manualDescription" None)

// ---- Stored rosters and the deployment's organizations (WI-0031) -----------------------

[<Fact>]
let ``a membership is stored as a closed record and reads back exactly`` () =
    let roster =
        acme
        |> execute "github:1" (Admit(agent "anthropic/claude-code", set [ RecordOwnTime; AmendOwnTime ]))
        |> Result.bind (execute "github:1" (Grant("anthropic/claude-code", ExportTime)))
        |> ok

    let membership = roster.Members["anthropic/claude-code"]
    Assert.Equal(2, membership.Revision)

    let text = MemberRecord.encode membership |> ok
    let record = Arca.Record.decode Arca.Record.DefaultMaxBytes text |> ok
    Assert.Equal(membership, MemberRecord.ofBody record.Body |> ok)
    Assert.Equal("records/chrona.member/anthropic_2fclaude-code.json", MemberRecord.path "anthropic/claude-code" |> ok |> Arca.RelativePath.render)

    // A capability this version does not know, or a person-only capability
    // held by an agent, is refused rather than dropped.
    let tampered (from: string) (into: string) =
        let edited = Arca.Record.decode Arca.Record.DefaultMaxBytes (text.Replace(from, into)) |> ok
        MemberRecord.ofBody edited.Body |> Result.isError

    Assert.True(tampered "\"ExportTime\"" "\"DeleteEverything\"")
    Assert.True(tampered "\"ExportTime\"" "\"ApproveTime\"")

[<Fact>]
let ``access changes are worked out as grants and revocations, applied all or none`` () =
    let commands = changesTo "github:2" Grants.ownTime Grants.reviewer
    Assert.Equal(4, commands.Length)
    Assert.True(commands |> List.forall (function Grant _ -> true | _ -> false))

    let roster = acme |> execute "github:1" (Admit(person "github:2", Grants.ownTime)) |> ok
    let promoted = roster |> executeAll "github:1" commands |> ok
    Assert.Equal<Set<Capability>>(Grants.reviewer, promoted.Members["github:2"].Capabilities)

    // One refused change refuses them all.
    let demoteFounder = changesTo "github:1" Grants.administrator Grants.ownTime
    Assert.Equal<string list>([ "CHRONA.AUTH.LAST_ADMINISTRATOR" ], roster |> executeAll "github:1" demoteFounder |> codes)

[<Fact>]
let ``a deployment lists the organizations it serves, the first the default`` () =
    let text (organizations: string) =
        $$"""{"environment":"test","environmentName":"test","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":""},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23li","redirectUri":"https://chrona.test/"},"organizations":{{organizations}}}"""

    let two =
        """[{"id":"org_a","displayName":"A","slug":"a","timeZone":"UTC"},{"id":"org_b","displayName":"B","slug":"b","timeZone":"UTC","location":{"owner":"acme-b","repository":"chrona-b","branch":"main","basePath":""}}]"""

    let config = Deployment.parse (text two) |> ok
    Assert.Equal("org_a", Deployment.organizationId config)
    Assert.Equal(Some "B", Deployment.organization config "org_b" |> Option.map _.DisplayName)

    let refused organizations = Deployment.parse (text organizations) |> Result.mapError code |> function Error c -> c | Ok _ -> "ok"
    Assert.Equal("CHRONA.STORAGE.INVALID_CONFIGURATION", refused "[]")
    Assert.Equal("CHRONA.STORAGE.INVALID_CONFIGURATION", refused """[{"id":"org_a","displayName":"A","slug":"a","timeZone":"UTC"},{"id":"org_a","displayName":"A2","slug":"a2","timeZone":"UTC"}]""")
    Assert.Equal("CHRONA.STORAGE.INVALID_SLUG", refused """[{"id":"org_a","displayName":"A","slug":"Not A Slug","timeZone":"UTC"}]""")
