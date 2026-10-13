/// Producers' inboxes (WI-0038, requirement 18), source policies and receipt
/// integrity (WI-0074).
[<Xunit.Collection("Store")>]
module Chrona.Tests.StoreInboxTests

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

// ---- Producers' inboxes (WI-0038, requirement 18) -------------------------------------

[<Fact>]
let ``observations a producer files become candidates with receipts when the records open, and leave the inbox`` () =
    let github, _ = withInbox ()
    let commits = github.State.History.Length

    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()

    let paths = storedPaths github
    // One commit for each file: a candidate, its receipt and the file's
    // removal land together; a payload that is not an observation gets its
    // receipt, and stays for its producer to see.
    Assert.Equal(commits + 4, github.State.History.Length)

    for id in [ "obs-1"; "obs-2" ] do
        Assert.Contains(orgPath $"records/chrona.candidate/open/CAND-github-{id}.json", paths)
        Assert.Contains(orgPath $"records/chrona.receipt/github/{id}.json", paths)
        Assert.DoesNotContain(orgPath $"inbox/github/{id}.json", paths)

    for id in [ "broken"; "elsewhere" ] do
        Assert.Contains(orgPath $"records/chrona.receipt/github/{id}.json", paths)
        Assert.Contains(orgPath $"inbox/github/{id}.json", paths)

    Assert.Equal<string list>([ "CAND-github-obs-1"; "CAND-github-obs-2" ], device.Model.Candidates |> List.map _.CandidateId |> List.sort)
    Assert.Equal(2, device.Model.Inbox.Received)
    Assert.Equal<string list>([ "broken"; "elsewhere" ], device.Model.Inbox.Invalid |> List.map (fun (_, id, _) -> id))
    Assert.Contains("org_other", device.Model.Inbox.Invalid |> List.collect (fun (_, _, reasons) -> reasons) |> String.concat " ")
    Assert.False(device.Model.Inbox.Running)

    // Read again, nothing is received twice: the refused files are shown
    // with their receipts' reasons, and nothing is written.
    device.Ui("readInboxes", "")
    Assert.Equal(commits + 4, github.State.History.Length)
    Assert.Equal(2, device.Model.Inbox.Invalid.Length)
    Assert.Equal(2, device.Model.Candidates.Length)

[<Fact>]
let ``a candidate accepted becomes the person's time and leaves the open folder; one rejected keeps its reason`` () =
    let github, _ = withInbox ()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()

    device.Send(LocationMoved "#/candidates/CAND-github-obs-1")
    // The draft opens with what the observation proposes.
    Assert.Equal("Reviewed obs-1", device.Model.CandidateDraft.Description)
    device.Ui("candidateActivityType", activityType device.Model)
    device.Ui("candidatePurpose", "Code review")
    device.Ui("acceptCandidate", "")
    Assert.Empty(device.Model.Store.Pending)
    Assert.Empty(device.Model.Store.Conflicts)

    device.Send(LocationMoved "#/candidates/CAND-github-obs-2")
    device.Ui("candidateReason", "Pairing, already recorded")
    device.Ui("rejectCandidate", "")
    Assert.Empty(device.Model.Store.Pending)

    let paths = storedPaths github
    Assert.DoesNotContain(orgPath "records/chrona.candidate/open/CAND-github-obs-1.json", paths)
    Assert.Contains(orgPath "records/chrona.candidate/decided/2026/10/CAND-github-obs-1.json", paths)
    Assert.Contains(orgPath "records/chrona.candidate/decided/2026/10/CAND-github-obs-2.json", paths)

    // Another device reads the decisions and the time.
    let other = Device(github, RepositoryVisibility.Private, "production")
    other.Open()
    let imported = other.Model.Ledger.Activities |> Map.toList |> List.map snd |> List.filter (fun a -> a.EntryMethod = Activity.Imported "github")
    let accepted = imported |> List.exactlyOne
    Assert.Equal(45, accepted.Minutes)
    Assert.Equal(Some "obs-1", accepted.Source |> Option.map _.ObservationId)

    match other.Model.Candidates |> List.sortBy _.CandidateId with
    | [ one; two ] ->
        Assert.Equal(Observations.Accepted [ accepted.ActivityId ], one.Disposition)
        Assert.Equal(Observations.Rejected "Pairing, already recorded", two.Disposition)
    | others -> failwith $"%A{others}"

[<Fact>]
let ``accepting time in a submitted period is refused until it is reopened, and the candidate waits`` () =
    let github, first = withInbox ()
    first.Ui("submitPeriod", "")
    Assert.Empty(first.Model.Store.Pending)

    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    device.Send(LocationMoved "#/candidates/CAND-github-obs-1")
    device.Ui("candidateActivityType", activityType device.Model)
    device.Ui("candidatePurpose", "Code review")
    device.Ui("acceptCandidate", "")

    Assert.Equal<string list>([ "CHRONA.REVIEW.SUBMITTED_PERIOD" ], device.Model.Problems[CandidateForm] |> List.map code)
    Assert.True(storedPaths github |> List.contains (orgPath "records/chrona.candidate/open/CAND-github-obs-1.json"))

// ---- Source policies and receipt integrity (WI-0074) ------------------------------------

/// The production configuration, with source policies for the organization.
let private withPolicies (policies: string) =
    """{"environment":"production","environmentName":"production","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":"deployments"},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://chrona.test/"},"organizations":[{"id":"org_acme","displayName":"Acme Consulting","slug":"acme","timeZone":"America/New_York","administrators":["583231"],"sourcePolicies":POLICIES}]}"""
        .Replace("POLICIES", policies)
    |> Deployment.parse

[<Fact>]
let ``a source policy is read from the deployment, and one that cannot be applied is refused`` () =
    let policies = withPolicies """[{"sourceSystem":"github","autoAccept":true,"activityTypeId":"TYP-1","businessPurpose":"Code review"},{"sourceSystem":"jira"}]""" |> ok
    let organization = policies.Organizations.Head
    Assert.Equal(2, organization.SourcePolicies.Length)
    Assert.True((Deployment.sourcePolicy organization "github").AutoAccept)
    Assert.False((Deployment.sourcePolicy organization "jira").AutoAccept)
    // A source not named is reviewed by a person.
    Assert.Equal(Observations.reviewEverything "gitlab", Deployment.sourcePolicy organization "gitlab")

    for refused in
        [ """[{"sourceSystem":"github","autoAccept":true}]"""
          """[{"sourceSystem":"github","autoAccept":true,"activityTypeId":"TYP-1"}]"""
          """[{"sourceSystem":"github"},{"sourceSystem":"github"}]"""
          """[{"sourceSystem":"git hub"}]"""
          """[{"sourceSystem":"github","autoAccept":"yes"}]"""
          """[{"sourceSystem":"github","trusted":true}]"""
          """{"sourceSystem":"github"}""" ] do
        match withPolicies refused with
        | Error(InvalidDeploymentConfig _) -> ()
        | other -> failwith $"{refused} gave %A{other}"

[<Fact>]
let ``a trusted source's observation becomes the named member's time in the same commit as its candidate and receipt`` () =
    let github = InMemoryStore()
    let founder = Device(github, RepositoryVisibility.Private, "production")
    founder.Open()
    // Time recorded first, from 08:00 to 08:30 on 7 October.
    record founder "08:00" "08:30" "Setup"
    let projectId = project founder.Model
    let activityTypeId = activityType founder.Model
    let serialize = Chrona.Integration.TimeObservationV1.serialize
    fileInInbox github "github" "obs-1" (serialize (observedWork "obs-1" projectId 13))
    // Overlaps the time already recorded: it cannot be recorded as it is.
    fileInInbox github "github" "obs-overlap" (serialize { observedWork "obs-overlap" projectId 8 with Timing = Chrona.Integration.TimeObservationV1.Interval(DateTimeOffset(2026, 10, 7, 8, 15, 0, TimeSpan.FromHours -4.0), DateTimeOffset(2026, 10, 7, 8, 45, 0, TimeSpan.FromHours -4.0)) })
    // Names no one: a person decides whose time it is.
    fileInInbox github "github" "obs-nobody" (serialize { observedWork "obs-nobody" projectId 15 with ActorId = None })
    // Another source, not trusted.
    fileInInbox github "jira" "PROJ-7" (serialize { observedWork "PROJ-7" projectId 17 with SourceSystem = "jira" })
    let commits = github.State.History.Length

    let config =
        withPolicies $"""[{{"sourceSystem":"github","autoAccept":true,"activityTypeId":"{activityTypeId}","businessPurpose":"Code review"}}]"""
        |> ok

    let device = Device(github, RepositoryVisibility.Private, "production", octocat, config, Browser())
    device.Open()

    // One commit per file.
    Assert.Equal(commits + 4, github.State.History.Length)
    let paths = storedPaths github

    // Accepted: the activity, the decided candidate and the receipt landed
    // together, and the file left the inbox.
    let accepted = device.Model.Candidates |> List.find (fun c -> c.CandidateId = "CAND-github-obs-1")

    match accepted.Disposition with
    | Observations.Accepted [ id ] ->
        let activity = device.Model.Ledger.Activities[id]
        Assert.Equal(octocat.ActorId, activity.ActorId)
        Assert.Equal(activityTypeId, activity.Classification.ActivityTypeId)
        Assert.Equal("Code review", activity.Classification.BusinessPurpose)
        Assert.Equal(Chrona.Domain.Activity.Imported "github", activity.EntryMethod)
        Assert.Equal(45, activity.Minutes)
        Assert.Contains(paths, fun path -> path.StartsWith(orgPath "records/chrona.activity/github_3a583231/2026/10/") && path.EndsWith $"{id}.json")
    | other -> failwith $"%A{other}"

    Assert.Equal("chrona", (List.last accepted.Decisions).By)
    Assert.Contains(orgPath "records/chrona.candidate/decided/2026/10/CAND-github-obs-1.json", paths)
    Assert.Contains(orgPath "records/chrona.receipt/github/obs-1.json", paths)
    Assert.DoesNotContain(orgPath "inbox/github/obs-1.json", paths)

    // The overlap needs a person, with the reason; nothing is lost.
    let overlap = device.Model.Candidates |> List.find (fun c -> c.CandidateId = "CAND-github-obs-overlap")

    match overlap.Disposition with
    | Observations.NeedsAttention problems -> Assert.NotEmpty(problems)
    | other -> failwith $"%A{other}"

    Assert.Contains(orgPath "records/chrona.candidate/open/CAND-github-obs-overlap.json", paths)

    // Naming no one, or from an untrusted source: waiting for review.
    for id in [ "CAND-github-obs-nobody"; "CAND-jira-PROJ-7" ] do
        Assert.Equal(Observations.Pending, (device.Model.Candidates |> List.find (fun c -> c.CandidateId = id)).Disposition)

    // Another device reads the accepted time as the person's own.
    let other = Device(github, RepositoryVisibility.Private, "production")
    other.Open()
    Assert.Contains("Reviewed obs-1", descriptions other)

[<Fact>]
let ``a receipt whose candidate no folder holds is shown where records needing attention are`` () =
    let github, _ = withInbox ()
    // A receipt written outside Chrona, naming a candidate that was never stored.
    let ghost: Observations.Receipt =
        { SourceSystem = "github"
          ObservationId = "ghost"
          Outcome = Observations.CandidateRecorded "CAND-github-ghost"
          At = DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero) }

    github.WriteExternally(
        (folderOf (configuration "production")).Location,
        orgPath (RelativePath.render (ReceiptRecord.path ghost |> ok)),
        Some(ReceiptRecord.encode ghost |> ok)
    )

    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    // Only the ghost: the other receipts' candidates are stored.
    Assert.Equal<(string * string * string) list>([ "github", "ghost", "CAND-github-ghost" ], device.Model.Inbox.Orphaned)
    Assert.True(viewFlag "hasInboxOrphaned" device.Model)
    let titles =
        match (Project.project device.Model |> Map.ofList)["obligations"] with
        | Chrona.Engine.View.Items rows ->
            rows
            |> List.collect (
                List.choose (function
                    | "title", Chrona.Engine.View.Text value -> Some value
                    | _ -> None)
            )
        | other -> failwith $"%A{other}"

    Assert.Contains(titles, fun title -> title.Contains "is not stored")

    // Read again, it is not read again, and it is still shown.
    device.Ui("readInboxes", "")
    Assert.Single(device.Model.Inbox.Orphaned) |> ignore
