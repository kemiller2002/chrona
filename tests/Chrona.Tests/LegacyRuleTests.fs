/// The legacy rules the expansion keeps ("existing ... rules remain"),
/// closed in the domain by WI-0043: gaps R1-R6 of DF-CHRONA-2026-0002.
module Chrona.Tests.LegacyRuleTests

open System
open Xunit
open Chrona.Tests.Support
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Domain.Ledger

module Entry = Chrona.Domain.ManualEntry
module Clock = Chrona.Domain.Timer
module Reviews = Chrona.Domain.Review

let private zone =
    match tryZone "America/New_York" with
    | Ok z -> z
    | Error e -> failwith $"{e}"

/// New York wall-clock instants in October 2026 (EDT, -04:00).
let private local (day: int) (h: int) (m: int) (s: int) = DateTimeOffset(2026, 10, day, h, m, s, TimeSpan.FromHours -4.0)
let private at h m = local 7 h m 0
let private context = { Performer = "ACTOR-1"; At = local 7 22 0 0; Source = "chrona-web"; Zone = zone; References = references; CorrelationId = None }

let private work =
    { ProjectId = "PRJ-1"; ClientId = None; EngagementId = None; ActivityTypeId = "ACT-DEV"; Tags = []; Description = "Work"; BusinessPurpose = "Delivery" }

let private activity id (h, m) minutes =
    let start = at h m

    { ActivityId = id
      OrganizationId = "ORG-1"
      ActorId = "ACTOR-1"
      Occurrence = occurrence zone start
      Timing = Interval(start, start.AddMinutes(float minutes))
      Minutes = minutes
      Classification = work
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
      Evidence = []
      Lineage = [] }

let private evidence id = { Id = id; Url = $"https://example.test/{id}"; Kind = "url"; Label = id; CapturedAt = at 8 0; Hash = None }

let private ok result =
    match result with
    | Ok value -> value
    | Error problems -> failwith $"{problems}"

let private refused result =
    match result with
    | Ok _ -> failwith "expected a refusal"
    | Error problems -> problems

let private run commands = commands |> List.fold (fun ledger command -> execute context ledger command |> ok) empty

// R1 -------------------------------------------------------------------------

[<Fact>]
let ``R1: a business purpose is required, on manual entry and on any recorded activity`` () =
    let entry: Entry.ManualEntry =
        { ActivityId = "A1"
          OrganizationId = "ORG-1"
          ActorId = "ACTOR-1"
          Zone = zone
          Timing = Entry.StartAndEnd(DateOnly(2026, 10, 7), TimeOnly(9, 0), TimeOnly(10, 0), None, None)
          Classification = { work with BusinessPurpose = "  " }
          Billability = Billable
          BillingReference = noBillingReference
          Reason = None
          WorkItemRef = None
          Evidence = [] }

    Assert.Equal<Diagnostic list>([ MissingField "businessPurpose" ], Entry.create { Now = at 21 0; HistoricalAfterDays = 7; References = references } [] entry |> refused)

    // A timer result is recorded through the same rule.
    let timerResult = { activity "T1" (9, 0) 60 with EntryMethod = Timer; Classification = { work with BusinessPurpose = "" } }
    Assert.Equal<Diagnostic list>([ MissingField "businessPurpose" ], execute context empty (Record timerResult) |> refused)

// R2 -------------------------------------------------------------------------

[<Fact>]
let ``R2: an amendment is revalidated by the creation rules`` () =
    let ledger = run [ Record(activity "A1" (9, 0) 60) ]
    let blanked = { Classification = Some { work with Description = ""; BusinessPurpose = "" }; Billability = None; BillingReference = None; Retime = None; Reason = "tidy" }
    Assert.Equal<Diagnostic list>([ MissingField "description"; MissingField "businessPurpose" ], execute context ledger (Amend("A1", 1, blanked)) |> refused)

    // Split children and merge results that are reclassified obey them too.
    let parts: SplitPart list =
        [ { ActivityId = "A1a"; Minutes = 30; Classification = Some { work with ProjectId = "" }; EvidenceIds = [] }
          { ActivityId = "A1b"; Minutes = 30; Classification = None; EvidenceIds = [] } ]

    Assert.Equal<Diagnostic list>([ MissingField "project" ], execute context ledger (Split("A1", 1, parts)) |> refused)

    let pair = run [ Record(activity "A1" (9, 0) 30); Record(activity "A2" (9, 30) 30) ]
    Assert.Equal<Diagnostic list>([ MissingField "activityType" ], execute context pair (Merge([ "A1", 1; "A2", 1 ], "M1", Some { work with ActivityTypeId = "" })) |> refused)

// R3 -------------------------------------------------------------------------

[<Fact>]
let ``R3: only contiguous sources with clock times merge`` () =
    let ledger =
        run
            [ Record(activity "A1" (9, 0) 30)
              Record(activity "A2" (9, 30) 30)
              Record(activity "A3" (11, 0) 30)
              Record { activity "D1" (13, 0) 30 with Timing = DurationOnDate 30 }
              Record { activity "D2" (13, 0) 30 with Timing = DurationOnDate 30 } ]

    let gap = IncompatibleMergeSources "sources are not contiguous"
    Assert.Equal<Diagnostic list>([ gap ], execute context ledger (Merge([ "A2", 1; "A3", 1 ], "M1", None)) |> refused)
    Assert.Equal<Diagnostic list>([ gap ], execute context ledger (Merge([ "D1", 1; "D2", 1 ], "M1", None)) |> refused)
    let merged = execute context ledger (Merge([ "A2", 1; "A1", 1 ], "M1", None)) |> ok
    Assert.Equal(Interval(at 9 0, at 10 0), merged.Activities["M1"].Timing)

// R4 -------------------------------------------------------------------------

[<Fact>]
let ``R4: evidence follows the record state`` () =
    let voided = run [ Record { activity "A1" (9, 0) 60 with Evidence = [ evidence "E1" ] }; Void("A1", 1, "duplicate") ]
    // A voided record may still gain evidence (it can be restored) ...
    let linked = execute context voided (LinkEvidence("A1", 2, evidence "E2")) |> ok
    Assert.Equal<string list>([ "E1"; "E2" ], linked.Activities["A1"].Evidence |> List.map _.Id)
    // ... but not lose it.
    Assert.Equal<Diagnostic list>([ IllegalTransition("Voided", "evidence-unlink") ], execute context voided (UnlinkEvidence("A1", 2, "E1")) |> refused)

    let parts: SplitPart list =
        [ { ActivityId = "A1a"; Minutes = 30; Classification = None; EvidenceIds = [] }
          { ActivityId = "A1b"; Minutes = 30; Classification = None; EvidenceIds = [] } ]

    let superseded = run [ Record(activity "A1" (9, 0) 60); Split("A1", 1, parts) ]
    Assert.Equal<Diagnostic list>([ IllegalTransition("Superseded", "evidence-link") ], execute context superseded (LinkEvidence("A1", 2, evidence "E3")) |> refused)
    Assert.Equal<Diagnostic list>([ IllegalTransition("Superseded", "evidence-unlink") ], execute context superseded (UnlinkEvidence("A1", 2, "E1")) |> refused)

// R5 -------------------------------------------------------------------------

let private stopped (segments: (DateTimeOffset * DateTimeOffset) list) =
    let timer: Clock.ActiveTimer =
        { TimerId = "T1"
          OrganizationId = "ORG-1"
          ActorId = "ACTOR-1"
          DeviceId = "laptop"
          ZoneId = zone.Id
          Segments = segments |> List.map (fun (s, f) -> ({ Start = s; Finish = Some f }: Clock.Segment))
          Classification = Some work }

    Clock.stop zone (segments |> List.map snd |> List.max) (Clock.Paused timer) |> ok |> snd

let private seconds (segments: (DateTimeOffset * DateTimeOffset) list) = segments |> List.sumBy (fun (s, f) -> (f - s).TotalSeconds)

[<Fact>]
let ``R5: the working total is rounded once to the nearest minute, however many pauses`` () =
    // Three runs of 20m40s are 62 minutes worked, not 3 x 20.
    let runs = [ local 7 9 0 0, local 7 9 20 40; local 7 9 30 0, local 7 9 50 40; local 7 10 0 0, local 7 10 20 40 ]
    let result = stopped runs
    Assert.Equal(62, result.TotalMinutes)
    Assert.Equal(62, result.Pieces |> List.sumBy _.Minutes)
    Assert.Equal(62, Clock.toActivities (at 11 0) (sprintf "A%d") Billable result |> ok |> List.sumBy _.Minutes)

[<Fact>]
let ``R5: under thirty seconds records nothing; thirty seconds is a minute`` () =
    let short = stopped [ local 7 9 0 0, local 7 9 0 29 ]
    Assert.Equal(0, short.TotalMinutes)
    Assert.Empty(short.Pieces)
    Assert.Empty(Clock.toActivities (at 11 0) (sprintf "A%d") Billable short |> ok)
    Assert.Equal(1, (stopped [ local 7 9 0 0, local 7 9 0 30 ]).TotalMinutes)

[<Fact>]
let ``R5: seconds across midnight are placed inside each business day`` () =
    // 23:59:40 to 00:00:50 is 70 seconds: one minute, on either side.
    let result = stopped [ local 7 23 59 40, local 8 0 0 50 ]
    Assert.Equal(1, result.TotalMinutes)
    let piece = List.exactlyOne result.Pieces
    let dayStart, dayEnd = dayBounds zone piece.Occurrence.LocalDate
    Assert.True(piece.Start >= dayStart && piece.Finish <= dayEnd)

/// A property over many generated timers: the pieces add up to the rounded
/// working total, each is a whole-minute interval inside its business day,
/// none overlaps another, and each lies within a minute of real work.
[<Fact>]
let ``R5: generated timers always yield valid, exact, non-overlapping pieces`` () =
    let random = Random 20261008

    for _ in 1..500 do
        let start = local 7 (random.Next(0, 24)) (random.Next(0, 60)) (random.Next(0, 60))

        let segments =
            List.init (random.Next(1, 6)) id
            |> List.scan
                (fun (_, finish: DateTimeOffset) _ ->
                    let s = finish.AddSeconds(float (random.Next(0, 3600)))
                    s, s.AddSeconds(float (random.Next(1, 7200))))
                (start, start)
            |> List.tail

        let result = stopped segments
        Assert.Equal(int (Math.Floor((seconds segments + 30.0) / 60.0)), result.TotalMinutes)
        Assert.Equal(result.TotalMinutes, result.Pieces |> List.sumBy _.Minutes)

        for piece in result.Pieces do
            Assert.True(piece.Minutes > 0)
            Assert.Equal(float piece.Minutes, (piece.Finish - piece.Start).TotalMinutes)
            let dayStart, dayEnd = dayBounds zone piece.Occurrence.LocalDate
            Assert.True(piece.Start >= dayStart && piece.Finish <= dayEnd, $"{piece} leaves its day")
            Assert.True(piece.Start >= (fst segments.Head).AddMinutes -1.0, $"{piece} starts before the timer")
            Assert.True(piece.Finish <= (snd (List.last segments)).AddMinutes 1.0, $"{piece} ends after the timer")

        for a, b in List.pairwise (result.Pieces |> List.sortBy _.Start) do
            Assert.True(a.Finish <= b.Start, $"{a} overlaps {b}")

// R6 -------------------------------------------------------------------------

[<Fact>]
let ``R6: an attestation carries a statement, and attesting again keeps both`` () =
    let workflow = Reviews.start (run [ Record(activity "A1" (9, 0) 60) ])
    let day = DateOnly(2026, 10, 7)
    Assert.Equal<Diagnostic list>([ MissingField "statement" ], Reviews.attest context day " " workflow |> refused)
    let once, first = Reviews.attest context day "Complete and accurate." workflow |> ok
    let twice, _ = Reviews.attest context day "Rechecked after lunch." once |> ok
    Assert.Equal("Complete and accurate.", first.Statement)
    Assert.Equal<string list>([ "Complete and accurate."; "Rechecked after lunch." ], twice.Attestations |> List.map _.Statement)

// Evidence references (24) ----------------------------------------------------

[<Fact>]
let ``evidence needs a label and a kind, and a link must be a web address`` () =
    let ledger = run [ Record(activity "A1" (9, 0) 60) ]
    let bad = { evidence "E1" with Label = " "; Kind = ""; Url = "javascript:alert(1)" }

    Assert.Equal<Diagnostic list>(
        [ MissingField "evidenceLabel"; MissingField "evidenceKind"; InvalidEvidenceUrl "javascript:alert(1)" ],
        execute context ledger (LinkEvidence("A1", 1, bad)) |> refused
    )

    // A link is optional; a reference without one is still evidence.
    Assert.True(Result.isOk (execute context ledger (LinkEvidence("A1", 1, { evidence "E2" with Url = "" }))))
    Assert.True(Result.isOk (execute context ledger (LinkEvidence("A1", 1, { evidence "E3" with Url = "http://intranet.example/doc" }))))
    Assert.Equal("CHRONA.EVIDENCE.INVALID_URL", code (InvalidEvidenceUrl "x"))
