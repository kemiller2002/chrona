/// Chrona's domain lifecycles against their state model (WI-0079):
/// `.echelon/state-model.json` names, for each lifecycle, the domain union
/// whose cases are its states and the union (or functions) whose cases are
/// its commands, its legal transitions, and the codes a refusal may carry.
///
/// The sweep is generated from the model and the domain's own unions: every
/// state the union has, against every command the union has. A pair the
/// model lists must be performed with the result it names; every other pair
/// must be refused with one of the lifecycle's codes. A new union case fails
/// these tests until the model and the fixtures below name it. The timer
/// lifecycle is not modelled while the Phase H freeze holds.
module Chrona.Tests.StateModelTests

open System
open System.Text.Json
open Microsoft.FSharp.Reflection
open Xunit
open Chrona.Tests.Support
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity

// ---- the model -------------------------------------------------------------------

type private Transition = { From: string; Command: string; To: string }

type private Variant =
    { Name: string
      Unreachable: string list
      Transitions: Transition list }

type private Lifecycle =
    { Id: string
      StatesUnion: string
      States: string list
      CommandsUnion: string option
      CommandsModule: string option
      Commands: string list
      Creation: (string * string) option
      Variants: Variant list
      Refusals: string list }

type private Obligation = { Id: string; Lifecycle: string }

let private strings (element: JsonElement) =
    element.EnumerateArray() |> Seq.map _.GetString() |> List.ofSeq

let private optionalString (element: JsonElement) (name: string) =
    match element.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
    | _ -> None

let private document = lazy (JsonDocument.Parse(readRepoFile ".echelon/state-model.json"))

let private lifecycles =
    lazy
        (document.Value.RootElement.GetProperty("lifecycles").EnumerateArray()
         |> Seq.map (fun l ->
             let states = l.GetProperty "states"
             let commands = l.GetProperty "commands"

             { Id = l.GetProperty("id").GetString()
               StatesUnion = states.GetProperty("union").GetString()
               States = strings (states.GetProperty "cases")
               CommandsUnion = optionalString commands "union"
               CommandsModule = optionalString commands "module"
               Commands = strings (commands.GetProperty "cases")
               Creation =
                 match l.GetProperty "creation" with
                 | c when c.ValueKind = JsonValueKind.Object -> Some(c.GetProperty("command").GetString(), c.GetProperty("to").GetString())
                 | _ -> None
               Variants =
                 l.GetProperty("variants").EnumerateArray()
                 |> Seq.map (fun v ->
                     { Name = v.GetProperty("name").GetString()
                       Unreachable = strings (v.GetProperty "unreachable")
                       Transitions =
                         v.GetProperty("transitions").EnumerateArray()
                         |> Seq.map (fun t ->
                             { From = t.GetProperty("from").GetString()
                               Command = t.GetProperty("command").GetString()
                               To = t.GetProperty("to").GetString() })
                         |> List.ofSeq })
                 |> List.ofSeq
               Refusals = strings (l.GetProperty "refusals") })
         |> List.ofSeq)

let private obligations =
    lazy
        (document.Value.RootElement.GetProperty("obligations").EnumerateArray()
         |> Seq.map (fun o ->
             { Id = o.GetProperty("id").GetString()
               Lifecycle = o.GetProperty("lifecycle").GetString() })
         |> List.ofSeq)

/// The domain unions the model may name.
let private unions =
    [ typeof<RecordState>
      typeof<Ledger.Command>
      typeof<PeriodReview.PeriodState>
      typeof<PeriodReview.ReviewKind>
      typeof<Observations.Disposition>
      typeof<Reference.Status>
      typeof<Reference.Command> ]
    |> List.map (fun t -> t.FullName, t)
    |> Map.ofList

let private caseNames (union: Type) =
    FSharpType.GetUnionCases(union, true) |> Array.map _.Name |> List.ofArray

let private caseOf (value: obj) =
    (fst (FSharpValue.GetUnionFields(value, value.GetType(), true))).Name

// ---- performing a command in a state ------------------------------------------------

type private Outcome =
    | Moved of state: string
    | Refused of codes: string list

let private outcome (result: Result<string, Diagnostic list>) =
    match result with
    | Ok state -> Moved state
    | Error problems -> Refused(problems |> List.map code)

let private zone =
    match tryZone "America/New_York" with
    | Ok z -> z
    | Error e -> failwith $"{e}"

let private day = DateOnly(2026, 10, 7)
let private at (h: int) (m: int) = DateTimeOffset(2026, 10, 7, h + 4, m, 0, TimeSpan.Zero)

let private ledgerContext: Ledger.CommandContext =
    { Performer = "ACTOR-1"
      At = DateTimeOffset(2026, 10, 7, 22, 0, 0, TimeSpan.Zero)
      Source = "chrona-web"
      Zone = zone
      References = references
      CorrelationId = None }

let private evidence id =
    { Id = id; Url = $"https://example.test/{id}"; Kind = "commit"; Label = id; CapturedAt = at 8 0; Hash = None; Source = None; Notes = None }

let private activity id (startH, startM) minutes =
    let start = at startH startM

    { ActivityId = id
      OrganizationId = "ORG-1"
      ActorId = "ACTOR-1"
      Occurrence = occurrence zone start
      Timing = Interval(start, start.AddMinutes(float minutes))
      Minutes = minutes
      Classification =
        { ProjectId = "PRJ-1"; ClientId = None; EngagementId = None; ActivityTypeId = "ACT-DEV"; Tags = []; Description = "Work"; BusinessPurpose = "Delivery" }
      EntryMethod = Manual
      Billability = Billable
      BillingReference = noBillingReference
      Record = Recorded
      Review = Unsubmitted
      Publication = Unpublished
      Revision = 1
      CreatedAt = at 8 0
      LastChangedAt = at 8 0
      Reason = None
      WorkItemRef = None
      ExternalRef = None
      Evidence = [ evidence "E1" ]
      Lineage = []
      Source = None }

let private ok result =
    match result with
    | Ok value -> value
    | Error problems -> failwith $"{problems}"

let private run (ledger: Ledger.Ledger) (command: Ledger.Command) = Ledger.execute ledgerContext ledger command |> ok

/// Activity A1 in the given state; B1 follows it directly, for a merge.
let private activityIn (state: string) =
    let recorded = [ Ledger.Record(activity "A1" (9, 0) 60); Ledger.Record(activity "B1" (10, 0) 60) ] |> List.fold run Ledger.empty

    match state with
    | "Recorded" -> recorded
    | "Voided" -> run recorded (Ledger.Void("A1", 1, "entered twice"))
    | "Superseded" ->
        let part id minutes : Ledger.SplitPart = { ActivityId = id; Minutes = minutes; Classification = None; EvidenceIds = [] }
        run recorded (Ledger.Split("A1", 1, [ part "S1" 30; part "S2" 30 ]))
    | other -> failwith $"No fixture puts an activity in state {other}: add one."

let private activityCommand (command: string) (ledger: Ledger.Ledger) : Ledger.Command =
    let revision id = ledger.Activities[id].Revision

    match command with
    | "Record" -> Ledger.Record(activity "A1" (9, 0) 60)
    | "Amend" ->
        Ledger.Amend(
            "A1",
            revision "A1",
            { Classification = Some { (activity "A1" (9, 0) 60).Classification with Description = "Reworded" }
              Billability = None
              BillingReference = None
              Retime = None
              Reason = "reworded" }
        )
    | "Void" -> Ledger.Void("A1", revision "A1", "entered twice")
    | "Restore" -> Ledger.Restore("A1", revision "A1")
    | "Split" ->
        let part id minutes : Ledger.SplitPart = { ActivityId = id; Minutes = minutes; Classification = None; EvidenceIds = [] }
        Ledger.Split("A1", revision "A1", [ part "N1" 30; part "N2" 30 ])
    | "Merge" -> Ledger.Merge([ "A1", revision "A1"; "B1", revision "B1" ], "M1", None)
    | "LinkEvidence" -> Ledger.LinkEvidence("A1", revision "A1", evidence "E9")
    | "UnlinkEvidence" -> Ledger.UnlinkEvidence("A1", revision "A1", "E1")
    | other -> failwith $"No fixture makes the activity command {other}: add one."

let private performActivity (_: string) (state: string) (command: string) =
    let ledger = activityIn state

    Ledger.execute ledgerContext ledger (activityCommand command ledger)
    |> Result.map (fun next -> caseOf (box next.Activities["A1"].Record))
    |> outcome

let private createActivity () =
    Ledger.execute ledgerContext Ledger.empty (Ledger.Record(activity "C1" (12, 0) 30))
    |> Result.map (fun next -> caseOf (box next.Activities["C1"].Record))
    |> outcome

let private periodConfig (variant: string) =
    { Periods.defaultConfig "America/New_York" with
        SubmissionExpected = true
        ApprovalRequired = (variant = "approval required") }

let private reviewContext d : Ledger.CommandContext =
    { ledgerContext with At = DateTimeOffset(2026, 10, d, 22, 0, 0, TimeSpan.Zero) }

/// The person's period on 7 October in the given state, with the ledger and
/// review steps that put it there.
let private periodIn (variant: string) (state: string) =
    let config = periodConfig variant
    let period = Periods.containing config day
    let ledger = run Ledger.empty (Ledger.Record { activity "A1" (9, 0) 60 with Evidence = [] })

    let submit (ledger, reviews) =
        let next, review = PeriodReview.submit (reviewContext 8) config reviews "SUB-1" period ledger |> ok
        next, reviews @ [ review ]

    let decide approve (ledger, reviews) =
        let next, review =
            if approve then
                PeriodReview.approve (reviewContext 9) config reviews "SUB-1" None ledger |> ok
            else
                PeriodReview.reject (reviewContext 9) config reviews "SUB-1" "fix the description" ledger |> ok

        next, reviews @ [ review ]

    let start = ledger, ([]: PeriodReview.PeriodReview list)

    let ledger, reviews =
        match state, config.ApprovalRequired with
        | "Open", _ -> start
        | "AwaitingApproval", true -> submit start
        | "Closed", true -> submit start |> decide true
        | "Closed", false -> submit start
        | "Returned", true -> submit start |> decide false
        | other, _ -> failwith $"No fixture puts a period in state {other} under {variant}: add one."

    config, period, ledger, reviews

let private performPeriod (variant: string) (state: string) (command: string) =
    let config, period, ledger, reviews = periodIn variant state
    let context = reviewContext 10

    let result =
        match command with
        | "Submission" -> PeriodReview.submit context config reviews "SUB-2" period ledger
        | "Approval" -> PeriodReview.approve context config reviews "SUB-1" None ledger
        | "Rejection" -> PeriodReview.reject context config reviews "SUB-1" "fix it" ledger
        | "Reopening" -> PeriodReview.reopen context config reviews "ACTOR-1" period "correct it" ledger
        | other -> failwith $"No fixture makes the review step {other}: add one."

    result
    |> Result.map (fun (_, review) -> caseOf (box (PeriodReview.stateOf config.ApprovalRequired (reviews @ [ review ]) "ACTOR-1" period)))
    |> outcome

let private decisionContext: Observations.DecisionContext = { Ledger = ledgerContext; Zone = zone }

let private observation: Observations.Observation =
    { ObservationId = "O1"
      SourceSystem = "github"
      OrganizationId = "ORG-1"
      ProjectId = "PRJ-1"
      ActorId = Some "ACTOR-1"
      WorkItemId = Some "GH-42"
      ExternalUrl = Some "https://example.test/pull/42"
      Timing = Observations.ObservedInterval(at 9 0, at 9 45)
      Description = Some "Reviewed pull request 42"
      Evidence = [ "pull-request", "https://example.test/pull/42" ]
      ObservedAt = at 9 45 }

let private candidateId = Observations.candidateId observation.SourceSystem observation.ObservationId

let private received () =
    Observations.record Observations.empty (Observations.receive (at 18 0) Observations.empty observation)

/// The candidate with the given disposition: dispositions are data the
/// store reads back, so each is set directly on the received candidate.
let private candidateIn (state: string) =
    let inbox = received ()
    let candidate = inbox.Candidates[candidateId]

    let disposition =
        match state with
        | "Pending" -> Observations.Pending
        | "Accepted" -> Observations.Accepted [ "ACT-X" ]
        | "AcceptedWithChanges" -> Observations.AcceptedWithChanges [ "ACT-X" ]
        | "Rejected" -> Observations.Rejected "not ours"
        | "Duplicate" -> Observations.Duplicate "CAND-github-O0"
        | "NeedsAttention" -> Observations.NeedsAttention [ MissingField "activityType" ]
        | other -> failwith $"No fixture puts a candidate in state {other}: add one."

    { inbox with Candidates = inbox.Candidates.Add(candidateId, { candidate with Disposition = disposition }) }

let private performCandidate (_: string) (state: string) (command: string) =
    let inbox = candidateIn state
    let revision = inbox.Candidates[candidateId].Revision
    let disposition (inbox: Observations.Inbox) = caseOf (box inbox.Candidates[candidateId].Disposition)

    match command with
    | "accept" ->
        Observations.accept decisionContext "ACT-1" candidateId revision (Observations.proposal "ACT-DEV" "Client deliverable" observation) inbox Ledger.empty
        |> Result.map (fst >> disposition)
        |> outcome
    | "reject" -> Observations.reject decisionContext candidateId revision "not ours" inbox |> Result.map disposition |> outcome
    | other -> failwith $"No fixture makes the candidate command {other}: add one."

let private createCandidate () =
    Moved(caseOf (box (received ()).Candidates[candidateId].Disposition))

let private item = Reference.Project, "PRJ-1"

let private referenceIn (state: string) =
    match state with
    | "Active" -> references
    | "Archived" -> Reference.execute references (Reference.Archive(fst item, snd item, references.Items[item].Revision)) |> ok
    | other -> failwith $"No fixture puts a reference item in state {other}: add one."

let private performReference (_: string) (state: string) (command: string) =
    let catalogue = referenceIn state
    let revision = catalogue.Items[item].Revision
    let kind, id = item

    let command =
        match command with
        | "Add" -> Reference.Add(kind, id, "Again")
        | "Rename" -> Reference.Rename(kind, id, revision, "Renamed")
        | "Archive" -> Reference.Archive(kind, id, revision)
        | "Reactivate" -> Reference.Reactivate(kind, id, revision)
        | "Mirror" -> Reference.Mirror("summa", kind, id, "Mirrored", Reference.Active)
        | other -> failwith $"No fixture makes the reference command {other}: add one."

    Reference.execute catalogue command
    |> Result.map (fun next -> caseOf (box next.Items[item].Status))
    |> outcome

let private createReference () =
    Reference.execute references (Reference.Add(Reference.Project, "PRJ-NEW", "New"))
    |> Result.map (fun next -> caseOf (box next.Items[(Reference.Project, "PRJ-NEW")].Status))
    |> outcome

/// How each lifecycle's commands are performed, and how it is created.
let private performers =
    Map.ofList
        [ "activity-record", (performActivity, createActivity)
          "period-review", (performPeriod, (fun () -> failwith "a period is not created"))
          "observation-candidate", (performCandidate, createCandidate)
          "reference-item", (performReference, createReference) ]

let private performerOf (lifecycle: Lifecycle) =
    match performers.TryFind lifecycle.Id with
    | Some performer -> performer
    | None -> failwith $"No performer for the lifecycle {lifecycle.Id}: add one."

// ---- the tests ------------------------------------------------------------------------

[<Fact>]
let ``the model names exactly the domain's states and commands`` () =
    for lifecycle in lifecycles.Value do
        let states =
            match unions.TryFind lifecycle.StatesUnion with
            | Some union -> union
            | None -> failwith $"{lifecycle.Id}: {lifecycle.StatesUnion} is not a domain union the tests know"

        Assert.Equal<Set<string>>(set (caseNames states), set lifecycle.States)

        match lifecycle.CommandsUnion, lifecycle.CommandsModule with
        | Some name, _ ->
            match unions.TryFind name with
            | Some union -> Assert.Equal<Set<string>>(set (caseNames union), set lifecycle.Commands)
            | None -> failwith $"{lifecycle.Id}: {name} is not a domain union the tests know"
        | None, Some moduleName ->
            // No command union: each command is a public function of the module.
            let moduleType = typeof<Observations.Inbox>.Assembly.GetType(moduleName, true)

            for command in lifecycle.Commands do
                Assert.True(moduleType.GetMethod(command) <> null, $"{lifecycle.Id}: {moduleName} has no function {command}")
        | None, None -> failwith $"{lifecycle.Id}: its commands name neither a union nor a module"

        for variant in lifecycle.Variants do
            for transition in variant.Transitions do
                Assert.Contains(transition.From, lifecycle.States)
                Assert.Contains(transition.To, lifecycle.States)
                Assert.Contains(transition.Command, lifecycle.Commands)

            for state in variant.Unreachable do
                Assert.Contains(state, lifecycle.States)

    // The timer lifecycle waits for the Phase H freeze.
    Assert.DoesNotContain(lifecycles.Value, fun l -> l.Id.Contains "timer")

[<Fact>]
let ``every transition in the model is performed by the domain, with the result it names`` () =
    let failures =
        [ for lifecycle in lifecycles.Value do
              let perform, _ = performerOf lifecycle

              for variant in lifecycle.Variants do
                  for t in variant.Transitions do
                      match perform variant.Name t.From t.Command with
                      | Moved state when state = t.To -> ()
                      | other -> $"{lifecycle.Id} ({variant.Name}): {t.From} --{t.Command}--> expected {t.To}, got %A{other}" ]

    Assert.Empty(failures)

[<Fact>]
let ``every other pair of state and command is refused with one of the lifecycle's stable codes`` () =
    // Every pair the model leaves out, from the domain's own state and
    // command cases.
    let pairs =
        [ for lifecycle in lifecycles.Value do
              for variant in lifecycle.Variants do
                  let legal = variant.Transitions |> List.map (fun t -> t.From, t.Command) |> Set.ofList

                  for state in lifecycle.States |> List.filter (fun s -> not (List.contains s variant.Unreachable)) do
                      for command in lifecycle.Commands |> List.filter (fun c -> not (legal.Contains(state, c))) do
                          lifecycle, variant.Name, state, command ]

    let failures =
        pairs
        |> List.choose (fun (lifecycle, variant, state, command) ->
            let perform, _ = performerOf lifecycle

            match perform variant state command with
            | Refused codes when not codes.IsEmpty && codes |> List.forall (fun c -> List.contains c lifecycle.Refusals) -> None
            | other -> Some $"{lifecycle.Id} ({variant}): {state} --{command}--> expected a refusal in %A{lifecycle.Refusals}, got %A{other}")

    Assert.NotEmpty(pairs)
    Assert.Empty(failures)

[<Fact>]
let ``what the model creates, the domain creates in the state it names`` () =
    for lifecycle in lifecycles.Value do
        match lifecycle.Creation with
        | Some(_, state) ->
            let _, create = performerOf lifecycle
            Assert.Equal(Moved state, create ())
        | None -> ()

[<Fact>]
let ``every obligation in the model is raised by the domain`` () =
    for obligation in obligations.Value do
        Assert.Contains(lifecycles.Value, fun l -> l.Id = obligation.Lifecycle)

        match obligation.Id with
        | "published-time-changed" ->
            let ledger = run Ledger.empty (Ledger.Record(activity "A1" (9, 0) 60))
            let published = { ledger with Activities = ledger.Activities.Add("A1", { ledger.Activities["A1"] with Publication = Published }) }
            let changed = run published (activityCommand "Amend" published)
            Assert.Equal(AdjustmentRequired, changed.Activities["A1"].Publication)
        | "reviewed-time-changed" ->
            let ledger = run Ledger.empty (Ledger.Record(activity "A1" (9, 0) 60))
            let submitted = { ledger with Activities = ledger.Activities.Add("A1", { ledger.Activities["A1"] with Review = Submitted }) }
            let changed = run submitted (activityCommand "Amend" submitted)
            Assert.Equal(Reopened, changed.Activities["A1"].Review)
        | "held-period-restricts-changes" ->
            let config, _, _, reviews = periodIn "approval required" "AwaitingApproval"

            Assert.Equal<string list>(
                [ "CHRONA.REVIEW.SUBMITTED_PERIOD" ],
                PeriodReview.restrictions config reviews [ "ACTOR-1", day ] |> List.map code
            )
        | other -> failwith $"No check for the obligation {other}: add one."
