/// Observation processing (WI-0050): requirements expansion 19 and scenario
/// tests 25-30 of section 43.
module Chrona.Tests.ObservationTests

open System
open Xunit
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Domain.Observations
open Chrona.Tests.Support

let private zone =
    match tryZone "America/New_York" with
    | Ok z -> z
    | Error e -> failwith $"{e}"

let private at (h: int) (m: int) = DateTimeOffset(2026, 10, 7, h, m, 0, TimeSpan.FromHours -4.0)
let private now = at 18 0

let private context: DecisionContext =
    { Ledger = { Performer = "ACTOR-1"; At = now; Source = "chrona-web"; Zone = zone; References = references; CorrelationId = None }
      Zone = zone }

let private observation id (h, m) minutes =
    { ObservationId = id
      SourceSystem = "github"
      OrganizationId = "ORG-1"
      ProjectId = "PRJ-1"
      ActorId = Some "ACTOR-1"
      WorkItemId = Some "GH-42"
      ExternalUrl = Some "https://example.test/pull/42"
      Timing = ObservedInterval(at h m, (at h m).AddMinutes(float minutes))
      Description = Some "Reviewed pull request 42"
      Evidence = [ "pull-request", "https://example.test/pull/42" ]
      ObservedAt = (at h m).AddMinutes(float minutes) }

let private ok result =
    match result with
    | Ok value -> value
    | Error problems -> failwith $"{problems}"

let private refused result =
    match result with
    | Ok _ -> failwith "expected a refusal"
    | Error problems -> problems

let private received (inbox: Inbox) (o: Observation) = record inbox (receive now inbox o)
let private candidate (inbox: Inbox) (o: Observation) = inbox.Candidates[candidateId o.SourceSystem o.ObservationId]
let private reviewed = proposal "ACT-DEV" "Client deliverable"

[<Fact>]
let ``scenario 25: an observation becomes a pending candidate, and acceptance records it with its source`` () =
    let o = observation "OBS-1" (9, 0) 45
    let inbox = received empty o
    let c = candidate inbox o
    Assert.Equal(Pending, c.Disposition)
    Assert.Equal(CandidateRecorded c.CandidateId, inbox.Receipts[("github", "OBS-1")].Outcome)
    // Review is the default: nothing reaches the ledger until someone decides.
    Assert.Equal<Candidate list>([ c ], awaitingReview inbox)

    let inbox, ledger = accept context "A1" c.CandidateId 1 (reviewed o) inbox Ledger.empty |> ok
    let a = ledger.Activities["A1"]
    Assert.Equal(Accepted [ "A1" ], (candidate inbox o).Disposition)
    Assert.Equal(45, a.Minutes)
    Assert.Equal(Imported "github", a.EntryMethod)
    Assert.Equal(PendingClassification, a.Billability)
    Assert.Equal(Some "GH-42", a.WorkItemRef)
    Assert.Equal(Some { SourceSystem = "github"; ObservationId = "OBS-1"; ExternalUrl = Some "https://example.test/pull/42"; IngestedAt = now }, a.Source)
    Assert.Equal<string list>([ "https://example.test/pull/42" ], a.Evidence |> List.map _.Url)
    // The import is audited like any other change (25).
    Assert.Equal("observation:github", (List.last ledger.Audit).Source)
    Assert.Empty(awaitingReview inbox)

[<Fact>]
let ``scenario 26: a candidate changed before acceptance is accepted with changes`` () =
    let o = observation "OBS-1" (9, 0) 45
    let inbox = received empty o
    let changed = { reviewed o with ProjectId = "PRJ-2"; Description = "Code review" }
    let inbox, ledger = accept context "A1" (candidate inbox o).CandidateId 1 changed inbox Ledger.empty |> ok
    Assert.Equal(AcceptedWithChanges [ "A1" ], (candidate inbox o).Disposition)
    Assert.Equal("PRJ-2", ledger.Activities["A1"].Classification.ProjectId)

[<Fact>]
let ``scenario 27: a rejected candidate stays, with its reason; it cannot be decided again`` () =
    let o = observation "OBS-1" (9, 0) 45
    let inbox = received empty o
    let id = (candidate inbox o).CandidateId
    Assert.Equal<Diagnostic list>([ MissingField "reason" ], reject context id 1 " " inbox |> refused)
    let inbox = reject context id 1 "Not billable work" inbox |> ok
    let c = candidate inbox o
    Assert.Equal(Rejected "Not billable work", c.Disposition)
    Assert.Equal<Disposition list>([ Pending; Rejected "Not billable work" ], c.Decisions |> List.map _.Disposition)
    Assert.Equal<Diagnostic list>([ IllegalTransition("Rejected", "decide") ], accept context "A1" id 2 (reviewed o) inbox Ledger.empty |> refused)
    Assert.Equal<Diagnostic list>([ RevisionConflict(1, 2) ], reject context id 1 "again" inbox |> refused)

[<Fact>]
let ``scenario 28: the same observation again is idempotent, and the same work under another id is a duplicate`` () =
    let o = observation "OBS-1" (9, 0) 45
    let inbox = received empty o

    match receive now inbox o with
    | AlreadyReceived receipt -> Assert.Equal(inbox.Receipts[("github", "OBS-1")], receipt)
    | other -> failwith $"{other}"

    let again = { o with ObservationId = "OBS-2" }
    let inbox = received inbox again
    Assert.Equal(Duplicate(candidateId "github" "OBS-1"), (candidate inbox again).Disposition)
    Assert.Equal(CandidateRecorded(candidateId "github" "OBS-2"), inbox.Receipts[("github", "OBS-2")].Outcome)
    Assert.Equal(1, (awaitingReview inbox).Length)
    // Different work from the same source is not a duplicate.
    let other = observation "OBS-3" (11, 0) 45
    Assert.Equal(Pending, (candidate (received inbox other) other).Disposition)

[<Fact>]
let ``scenario 29: a candidate stored without its receipt is repaired, never decided twice`` () =
    let o = observation "OBS-1" (9, 0) 45

    let inbox, receipt =
        match receive now empty o with
        | NewCandidate(c, r) -> { empty with Candidates = Map.ofList [ c.CandidateId, c ] }, r
        | other -> failwith $"{other}"

    // The receipt write failed after the candidate was stored.
    match receive (now.AddMinutes 5.0) inbox o with
    | ReceiptRepair repaired ->
        Assert.Equal(receipt.Outcome, repaired.Outcome)
        let fixedInbox = record inbox (ReceiptRepair repaired)
        Assert.Equal(1, fixedInbox.Candidates.Count)
        Assert.Equal<Decision list>((candidate inbox o).Decisions, (candidate fixedInbox o).Decisions)
    | other -> failwith $"{other}"

[<Fact>]
let ``scenario 30: an invalid payload is told apart from a valid observation that is rejected`` () =
    let receipt = invalid now "github" "OBS-9" [ "$.timing: expected an interval or a duration" ]
    let inbox = recordInvalid empty receipt
    Assert.Equal(InvalidObservation [ "$.timing: expected an interval or a duration" ], inbox.Receipts[("github", "OBS-9")].Outcome)
    Assert.Empty(inbox.Candidates)

    // A readable observation the domain cannot accept as it stands is kept
    // as a candidate needing attention, not discarded.
    let future = { observation "OBS-10" (9, 0) 45 with Timing = ObservedInterval(now, now.AddHours 1.0) }
    let inbox = received inbox future
    Assert.Equal(NeedsAttention [ FutureTime ], (candidate inbox future).Disposition)

[<Fact>]
let ``a trusted source is accepted at once; if its time cannot be recorded it needs attention`` () =
    let policy =
        { SourceSystem = "github"
          AutoAccept = true
          DefaultActivityTypeId = Some "ACT-DEV"
          DefaultBusinessPurpose = Some "Client deliverable" }

    let o = observation "OBS-1" (9, 0) 45
    let inbox, ledger, receipt = ingest context policy "A1" empty Ledger.empty o
    Assert.Equal(Accepted [ "A1" ], (candidate inbox o).Disposition)
    Assert.Equal(CandidateRecorded(candidateId "github" "OBS-1"), receipt.Outcome)

    // Overlapping time from the same source: recorded nowhere, kept for a person.
    let overlap = observation "OBS-2" (9, 30) 30
    let inbox, ledger, _ = ingest context policy "A2" inbox ledger overlap
    Assert.Equal(NeedsAttention [ OverlapsActivity "A1" ], (candidate inbox overlap).Disposition)
    Assert.False(ledger.Activities.ContainsKey "A2")

    // Without a policy the default is review.
    let other = observation "OBS-3" (13, 0) 30
    let inbox, _, _ = ingest context (reviewEverything "github") "A3" inbox ledger other
    Assert.Equal(Pending, (candidate inbox other).Disposition)

[<Fact>]
let ``accepted imports pass the ordinary rules: an unknown project is refused and the candidate waits`` () =
    let o = { observation "OBS-1" (9, 0) 45 with ProjectId = "PRJ-NONE" }
    let inbox = received empty o
    Assert.Equal<Diagnostic list>([ UnknownReference("project", "PRJ-NONE") ], accept context "A1" (candidate inbox o).CandidateId 1 (reviewed o) inbox Ledger.empty |> refused)
    Assert.Equal(Pending, (candidate inbox o).Disposition)
