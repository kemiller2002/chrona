/// Observation inboxes (WI-0038, requirement 18, expansion 19): the
/// receiver-owned contract a producer writes, where it files it, what the
/// store makes of each file, and the candidate and receipt records.
module Chrona.Tests.IntakeTests

open System
open Xunit
open Arca
open Chrona.Domain
open Chrona.Domain.Observations
open Chrona.Integration
open Chrona.Application

let private at = DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.FromHours -4.0)
/// When the store reads the inbox: after the work was done.
let private now = at.AddHours 2.0

let private observation: TimeObservationV1.TimeObservation =
    { ObservationId = "obs-1"
      SourceSystem = "github"
      OrganizationId = "org_acme"
      ProjectId = "PRJ-1"
      ActorId = Some "github:7"
      WorkItemId = Some "PR-42"
      ExternalUrl = Some "https://example.test/pull/42"
      Timing = TimeObservationV1.Interval(at, at.AddMinutes 45.0)
      Description = Some "Reviewed pull request 42"
      Evidence = [ { Kind = "pull-request"; Reference = "https://example.test/pull/42" } ]
      ObservedAt = at.AddMinutes 45.0 }

let private ok result =
    match result with
    | Ok value -> value
    | Error problem -> failwith $"%A{problem}"

let private invalid (read: Inbox.Read) =
    match read with
    | Inbox.Invalid reasons -> reasons
    | Inbox.V1 found -> failwith $"expected it refused, read %A{found}"

/// The payload's text with one field replaced (or removed, for None).
let private withField (name: string) (value: string option) =
    let node = Text.Json.Nodes.JsonNode.Parse(TimeObservationV1.serialize observation).AsObject()

    match value with
    | Some raw -> node[name] <- Text.Json.Nodes.JsonNode.Parse raw
    | None -> node.Remove name |> ignore

    node.ToJsonString()

// ---- the contract (requirement 18) -------------------------------------------------

[<Fact>]
let ``version 1 reads back exactly what was written, in either timing`` () =
    let text = TimeObservationV1.serialize observation
    Assert.Equal(Inbox.V1 observation, Inbox.read "github" "obs-1" text)

    let duration =
        { observation with
            Timing = TimeObservationV1.Duration 30
            ActorId = None
            WorkItemId = None
            ExternalUrl = None
            Description = None
            Evidence = [] }

    Assert.Equal(Inbox.V1 duration, Inbox.read "github" "obs-1" (TimeObservationV1.serialize duration))
    // Its fields come first in the contract's order, with absent ones null.
    Assert.StartsWith("""{"contract":"chrona.time-observation","version":1,"observationId":"obs-1",""", text)
    Assert.Contains("\"actorId\":null", TimeObservationV1.serialize duration)

[<Fact>]
let ``a payload that is not version 1 of the contract is refused, never guessed`` () =
    Assert.NotEmpty(invalid (Inbox.read "github" "obs-1" "not json"))
    Assert.NotEmpty(invalid (Inbox.read "github" "obs-1" "[]"))
    Assert.Contains("version 2", invalid (Inbox.read "github" "obs-1" (withField "version" (Some "2"))) |> String.concat " ")
    Assert.Contains("contract", invalid (Inbox.read "github" "obs-1" (withField "contract" (Some "\"other\""))) |> String.concat " ")

[<Fact>]
let ``every problem with a payload is reported, and an unknown field is refused rather than dropped`` () =
    let node = Text.Json.Nodes.JsonNode.Parse(TimeObservationV1.serialize observation).AsObject()
    node["projectId"] <- Text.Json.Nodes.JsonValue.Create ""
    node["observedAt"] <- Text.Json.Nodes.JsonValue.Create "yesterday"
    node["billable"] <- Text.Json.Nodes.JsonValue.Create true
    let reasons = invalid (Inbox.read "github" "obs-1" (node.ToJsonString()))
    Assert.True(reasons.Length >= 3, $"%A{reasons}")
    Assert.Contains(reasons, fun reason -> reason.Contains "projectId")
    Assert.Contains(reasons, fun reason -> reason.Contains "observedAt")
    Assert.Contains(reasons, fun reason -> reason.Contains "billable")
    // An optional field is stated, as null when there is none.
    Assert.Contains(invalid (Inbox.read "github" "obs-1" (withField "actorId" None)), fun reason -> reason.Contains "actorId")
    // An instant states its offset.
    Assert.NotEmpty(invalid (Inbox.read "github" "obs-1" (withField "observedAt" (Some "\"2026-10-07T09:45:00\""))))

[<Fact>]
let ``a payload is read only where it says it belongs, and only up to its size limit`` () =
    let text = TimeObservationV1.serialize observation
    Assert.Contains("filed as jira/obs-1", invalid (Inbox.read "jira" "obs-1" text) |> String.concat " ")
    Assert.Contains("filed as github/obs-2", invalid (Inbox.read "github" "obs-2" text) |> String.concat " ")
    let large = withField "description" (Some("\"" + String('x', Inbox.MaxBytes) + "\""))
    Assert.Contains("larger than", invalid (Inbox.read "github" "obs-1" large) |> String.concat " ")

[<Fact>]
let ``an inbox path is built only from safe segments, and read back only from them`` () =
    Assert.Equal(Ok "inbox/github/obs-1.json", Inbox.path "github" "obs-1")
    Assert.True(Inbox.path "../records" "x" |> Result.isError)
    Assert.True(Inbox.path "github" "a/b" |> Result.isError)
    Assert.True(Inbox.path "github" ".hidden" |> Result.isError)
    Assert.True(Inbox.path "github" (String('a', 101)) |> Result.isError)
    Assert.Equal(Some("github", "obs-1"), Inbox.ofPath "inbox/github/obs-1.json")
    Assert.Equal(None, Inbox.ofPath "inbox/github/obs-1.txt")
    Assert.Equal(None, Inbox.ofPath "inbox/github/deeper/obs-1.json")
    Assert.Equal(None, Inbox.ofPath "records/github/obs-1.json")

// ---- what the store makes of a file -------------------------------------------------

[<Fact>]
let ``an observation for the organization becomes a pending candidate with its receipt`` () =
    match Intake.decide now "org_acme" Observations.empty "github" "obs-1" (TimeObservationV1.serialize observation) with
    | Intake.Received(NewCandidate(candidate, receipt)) ->
        Assert.Equal("CAND-github-obs-1", candidate.CandidateId)
        Assert.Equal(Pending, candidate.Disposition)
        Assert.Equal(ObservedInterval(at, at.AddMinutes 45.0), candidate.Observation.Timing)
        Assert.Equal<(string * string) list>([ "pull-request", "https://example.test/pull/42" ], candidate.Observation.Evidence)
        Assert.Equal(CandidateRecorded candidate.CandidateId, receipt.Outcome)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``an observation for another organization, or a payload that is not one, gets a receipt saying why`` () =
    match Intake.decide now "org_other" Observations.empty "github" "obs-1" (TimeObservationV1.serialize observation) with
    | Intake.Refused receipt ->
        Assert.Equal(("github", "obs-1"), (receipt.SourceSystem, receipt.ObservationId))
        Assert.Contains("org_acme", Intake.reasonsOf receipt |> Option.defaultValue [] |> String.concat " ")
    | other -> failwith $"%A{other}"

    match Intake.decide now "org_acme" Observations.empty "github" "obs-1" "{}" with
    | Intake.Refused receipt -> Assert.True((Intake.reasonsOf receipt).IsSome)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a candidate already stored without its receipt is repaired, and the same work again is a duplicate`` () =
    let first =
        match Intake.decide now "org_acme" Observations.empty "github" "obs-1" (TimeObservationV1.serialize observation) with
        | Intake.Received(NewCandidate(candidate, _)) -> candidate
        | other -> failwith $"%A{other}"

    let known = { Observations.empty with Candidates = Map.ofList [ first.CandidateId, first ] }

    match Intake.decide now "org_acme" known "github" "obs-1" (TimeObservationV1.serialize observation) with
    | Intake.Received(ReceiptRepair receipt) -> Assert.Equal(CandidateRecorded first.CandidateId, receipt.Outcome)
    | other -> failwith $"%A{other}"

    let again = { observation with ObservationId = "obs-2" }

    match Intake.decide now "org_acme" known "github" "obs-2" (TimeObservationV1.serialize again) with
    | Intake.Received(NewCandidate(candidate, _)) -> Assert.Equal(Duplicate first.CandidateId, candidate.Disposition)
    | other -> failwith $"%A{other}"

// ---- the records -----------------------------------------------------------------------

let private candidate =
    match Intake.decide now "org_acme" Observations.empty "github" "obs-1" (TimeObservationV1.serialize observation) with
    | Intake.Received(NewCandidate(candidate, _)) -> candidate
    | other -> failwith $"%A{other}"

let private decode (text: string) =
    (Record.decode Record.DefaultMaxBytes text |> ok).Body

[<Fact>]
let ``a candidate is kept open until it is decided, then in the month it was decided`` () =
    Assert.Equal("records/chrona.candidate/open/CAND-github-obs-1.json", CandidateRecord.path candidate |> ok |> RelativePath.render)
    Assert.Equal(candidate, CandidateRecord.ofBody (decode (CandidateRecord.encode candidate |> ok)) |> ok)

    let rejected =
        { candidate with
            Disposition = Rejected "Not mine"
            Revision = 2
            Decisions = candidate.Decisions @ [ { By = "github:7"; At = DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero); Disposition = Rejected "Not mine" } ] }

    Assert.Equal("records/chrona.candidate/decided/2026/11/CAND-github-obs-1.json", CandidateRecord.path rejected |> ok |> RelativePath.render)
    Assert.Equal(rejected, CandidateRecord.ofBody (decode (CandidateRecord.encode rejected |> ok)) |> ok)

    // Problems that need a person's attention keep their stable codes.
    let attention = { candidate with Disposition = NeedsAttention [ Diagnostics.FutureTime ] }
    let back = CandidateRecord.ofBody (decode (CandidateRecord.encode attention |> ok)) |> ok

    match back.Disposition with
    | NeedsAttention [ problem ] -> Assert.Equal(Diagnostics.code Diagnostics.FutureTime, Diagnostics.code problem)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a receipt is filed by source and observation, and reads back as written`` () =
    let received = { SourceSystem = "github"; ObservationId = "obs-1"; Outcome = CandidateRecorded "CAND-github-obs-1"; At = at }
    let refused = { received with Outcome = InvalidObservation [ "'projectId' is missing" ] }
    Assert.Equal("records/chrona.receipt/github/obs-1.json", ReceiptRecord.path received |> ok |> RelativePath.render)
    Assert.Equal("records/chrona.receipt/github", ReceiptRecord.sourceFolder "github" |> ok |> RelativePath.render)
    Assert.Equal(received, ReceiptRecord.ofBody (decode (ReceiptRecord.encode received |> ok)) |> ok)
    Assert.Equal(refused, ReceiptRecord.ofBody (decode (ReceiptRecord.encode refused |> ok)) |> ok)

[<Fact>]
let ``receiving writes the candidate, its receipt and the file's removal as one change`` () =
    let file = RelativePath.parse "inbox/github/obs-1.json" |> ok
    let receipt = { SourceSystem = "github"; ObservationId = "obs-1"; Outcome = CandidateRecorded candidate.CandidateId; At = at }

    let changes =
        Stored.changes
            Stored.empty
            { Stored.nothing with
                Candidates = [ candidate ]
                Receipts = [ receipt ]
                Consumed = [ file, Revision "r1" ] }
        |> ok

    let paths = changes |> List.map (Change.path >> RelativePath.render)

    Assert.Equal<string list>(
        [ "records/chrona.candidate/open/CAND-github-obs-1.json"; "records/chrona.receipt/github/obs-1.json"; "inbox/github/obs-1.json" ],
        paths
    )

    // Read back, the candidate is open and stored at its revision.
    let stored =
        Stored.load
            [ { Path = CandidateRecord.path candidate |> ok
                Content = CandidateRecord.encode candidate |> ok
                Revision = Revision "r2" } ]

    Assert.Equal(candidate, stored.Candidates[candidate.CandidateId].Candidate)

    // Deciding it moves it from the open folder to its month.
    let decided = { candidate with Disposition = Rejected "Not mine"; Revision = 2 }
    let moved = Stored.changes stored { Stored.nothing with Candidates = [ decided ] } |> ok

    match moved with
    | [ Change.Delete(from, Revision "r2"); Change.Create(target, _) ] ->
        Assert.Equal("records/chrona.candidate/open/CAND-github-obs-1.json", RelativePath.render from)
        Assert.StartsWith("records/chrona.candidate/decided/", RelativePath.render target)
    | other -> failwith $"%A{other}"
