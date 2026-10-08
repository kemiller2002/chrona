/// The time domain's activity record, manual entry, time zones and overlap
/// (WI-0020): requirements expansion 5, 6, 9, 11, 12, 26 and scenario tests
/// 1, 6, 7 and 8 of section 43.
module Chrona.Tests.TimeDomainTests

open System
open Xunit
open Chrona.Tests.Support
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Domain.ManualEntry

let private zone id =
    match tryZone id with
    | Ok z -> z
    | Error e -> failwith $"{e}"

let private newYork = zone "America/New_York"
let private tokyo = zone "Asia/Tokyo"
let private date (y, m, d) = DateOnly(y, m, d)
let private time (h, m) = TimeOnly(h, m)

/// 2026-10-07 17:00 New York (EDT, -04:00).
let private now = DateTimeOffset(2026, 10, 7, 21, 0, 0, TimeSpan.Zero)
let private context = { Now = now; HistoricalAfterDays = 7; References = references }

let private work =
    { ProjectId = "PRJ-1"
      ClientId = Some "CLI-1"
      EngagementId = None
      ActivityTypeId = "ACT-DEV"
      Tags = [ "backend" ]
      Description = "Implement overlap rules"
      BusinessPurpose = "Client deliverable" }

let private entry id timing =
    { ActivityId = id
      OrganizationId = "ORG-1"
      ActorId = "ACTOR-1"
      Zone = newYork
      Timing = timing
      Classification = work
      Billability = Billable
      BillingReference = noBillingReference
      Reason = None
      WorkItemRef = None
      Evidence = [] }

let private created result =
    match result with
    | Ok activity -> activity
    | Error problems -> failwith $"{problems}"

let private problems result =
    match result with
    | Ok _ -> failwith "expected a refusal"
    | Error problems -> problems

let private today = date (2026, 10, 7)

[<Fact>]
let ``scenario 1: a manual entry records exact minutes and its local context`` () =
    let activity = create context [] (entry "A1" (StartAndEnd(today, time (9, 0), time (10, 37), None, None))) |> created
    Assert.Equal(97, activity.Minutes)
    Assert.Equal(Interval(DateTimeOffset(2026, 10, 7, 13, 0, 0, TimeSpan.Zero), DateTimeOffset(2026, 10, 7, 14, 37, 0, TimeSpan.Zero)), activity.Timing)
    Assert.Equal({ Zone = "America/New_York"; OffsetMinutes = -240; LocalDate = today; LocalTime = time (9, 0) }, activity.Occurrence)
    // Separate state dimensions, starting where a new record starts.
    Assert.Equal(Recorded, activity.Record)
    Assert.Equal(Unsubmitted, activity.Review)
    Assert.Equal(Unpublished, activity.Publication)
    Assert.Equal(1, activity.Revision)
    Assert.Equal(Manual, activity.EntryMethod)

[<Fact>]
let ``start plus duration and duration-only entries derive the same exact minutes`` () =
    let a = create context [] (entry "A1" (StartAndDuration(today, time (9, 0), 45, None))) |> created
    Assert.Equal(Interval(DateTimeOffset(2026, 10, 7, 13, 0, 0, TimeSpan.Zero), DateTimeOffset(2026, 10, 7, 13, 45, 0, TimeSpan.Zero)), a.Timing)
    let b = create context [] (entry "A2" (DurationOnly(today, 45))) |> created
    Assert.Equal(45, b.Minutes)
    Assert.Equal(DurationOnDate 45, b.Timing)
    let nonBillable = create context [] { entry "A3" (DurationOnly(today, 30)) with Billability = NonBillable } |> created
    Assert.Equal(NotBillable, nonBillable.Publication)

[<Fact>]
let ``scenario 6: across a DST change elapsed minutes are exact`` () =
    // 2026-11-01: New York falls back at 02:00 EDT to 01:00 EST.
    let fallBack = date (2026, 11, 1)
    let later = { context with Now = DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero) }
    let a = create later [] (entry "A1" (StartAndEnd(fallBack, time (0, 30), time (3, 0), None, None))) |> created
    // 00:30 EDT to 03:00 EST is three and a half real hours, not two and a half.
    Assert.Equal(210, a.Minutes)
    // 01:30 happens twice: ambiguous unless the offset is stated.
    Assert.Equal<Diagnostic list>([ AmbiguousLocalTime ], create later [] (entry "A2" (StartAndDuration(fallBack, time (1, 30), 10, None))) |> problems)
    let second = create later [] (entry "A3" (StartAndDuration(fallBack, time (1, 30), 10, Some -300))) |> created
    Assert.Equal(-300, second.Occurrence.OffsetMinutes)
    // 2026-03-08: 02:30 does not exist in New York.
    let springForward = date (2026, 3, 8)
    Assert.Equal<Diagnostic list>([ InvalidLocalTime ], create later [] { entry "A4" (StartAndDuration(springForward, time (2, 30), 10, None)) with Reason = Some "late entry" } |> problems)
    // The day itself is 25 hours long, so 25 hours fit and 25h01m does not.
    Assert.True(Result.isOk (create later [] (entry "A5" (DurationOnly(fallBack, 1500)))))
    Assert.Equal<Diagnostic list>([ CrossesBusinessDay ], create later [] (entry "A6" (DurationOnly(fallBack, 1501))) |> problems)

[<Fact>]
let ``scenario 7: a later time-zone change never rewrites a recorded activity`` () =
    let a = create context [] (entry "A1" (StartAndEnd(today, time (9, 0), time (10, 0), None, None))) |> created
    // The same instant seen from Tokyo is a different local date and time...
    let seenFromTokyo = occurrence tokyo (DateTimeOffset(2026, 10, 7, 13, 0, 0, TimeSpan.Zero))
    Assert.Equal(date (2026, 10, 7), seenFromTokyo.LocalDate)
    Assert.Equal(time (22, 0), seenFromTokyo.LocalTime)
    // ...but the activity keeps the context it was recorded in.
    Assert.Equal("America/New_York", a.Occurrence.Zone)
    Assert.Equal(time (9, 0), a.Occurrence.LocalTime)

[<Fact>]
let ``scenario 8: overlap is rejected within an actor and organization only`` () =
    let first = create context [] (entry "A1" (StartAndEnd(today, time (9, 0), time (10, 0), None, None))) |> created
    let overlapping = entry "A2" (StartAndEnd(today, time (9, 30), time (10, 30), None, None))
    Assert.Equal<Diagnostic list>([ OverlapsActivity "A1" ], create context [ first ] overlapping |> problems)
    // Back to back is not overlap.
    Assert.True(Result.isOk (create context [ first ] (entry "A3" (StartAndEnd(today, time (10, 0), time (11, 0), None, None)))))
    // Another actor, or another organization, is another scope.
    Assert.True(Result.isOk (create context [ first ] { overlapping with ActorId = "ACTOR-2" }))
    Assert.True(Result.isOk (create context [ first ] { overlapping with OrganizationId = "ORG-2" }))
    // Voided and superseded time does not count; submitted and approved time does.
    Assert.True(Result.isOk (create context [ { first with Record = Voided "duplicate" } ] overlapping))
    Assert.True(Result.isOk (create context [ { first with Record = Superseded [ "A9" ] } ] overlapping))
    Assert.False(Result.isOk (create context [ { first with Review = Approved } ] overlapping))

[<Fact>]
let ``duration-only entries respect the day's capacity`` () =
    let full = create context [] (entry "A1" (DurationOnly(today, 1400))) |> created
    Assert.Equal<Diagnostic list>([ DailyCapacityExceeded("2026-10-07", 1441) ], create context [ full ] (entry "A2" (DurationOnly(today, 41))) |> problems)

[<Fact>]
let ``future, historical, cross-day and incomplete entries are refused with every reason`` () =
    Assert.Equal<Diagnostic list>([ FutureTime ], create context [] (entry "A1" (StartAndEnd(today, time (16, 0), time (18, 0), None, None))) |> problems)
    Assert.Equal<Diagnostic list>([ FutureTime ], create context [] (entry "A1" (DurationOnly(today.AddDays 1, 30))) |> problems)
    let old = entry "A2" (DurationOnly(date (2026, 9, 1), 60))
    Assert.Equal<Diagnostic list>([ ReasonRequiredForHistoricalEntry ], create context [] old |> problems)
    Assert.True(Result.isOk (create context [] { old with Reason = Some "Reconstructed from calendar" }))
    Assert.Equal<Diagnostic list>([ CrossesBusinessDay ], create context [] (entry "A3" (StartAndDuration(date (2026, 10, 6), time (23, 0), 120, None))) |> problems)
    Assert.Equal<Diagnostic list>([ EndBeforeStart ], create context [] (entry "A4" (StartAndEnd(today, time (10, 0), time (9, 0), None, None))) |> problems)
    Assert.Equal<Diagnostic list>([ DurationNotPositive ], create context [] (entry "A5" (DurationOnly(today, 0))) |> problems)
    Assert.Equal<Diagnostic list>([ DurationNotWholeMinutes ], create context [] (entry "A6" (StartAndEnd(today, TimeOnly(9, 0, 30), time (10, 0), None, None))) |> problems)
    let blankWork = { work with ProjectId = ""; Description = " " }
    Assert.Equal<Diagnostic list>([ MissingField "project"; MissingField "description" ], create context [] { entry "A7" (DurationOnly(today, 30)) with Classification = blankWork } |> problems)

[<Fact>]
let ``copying a prior entry makes a new identity, never a revision`` () =
    let source = create context [] (entry "A1" (DurationOnly(today, 30))) |> created
    let draft = copyAsDraft "A2" newYork today 15 source
    Assert.Equal("A2", draft.ActivityId)
    Assert.Equal(source.Classification, draft.Classification)
    let copy = create context [ source ] draft |> created
    Assert.Equal(1, copy.Revision)
    Assert.Equal(1, source.Revision)

[<Fact>]
let ``diagnostic codes are stable and distinct`` () =
    let samples =
        [ DurationNotPositive; DurationNotWholeMinutes; EndBeforeStart; CrossesBusinessDay; FutureTime
          ReasonRequiredForHistoricalEntry; AmbiguousLocalTime; InvalidLocalTime; InvalidTimeZone "x"; MissingField "x"
          OrganizationMismatch; ActorMismatch; OverlapsActivity "x"; DailyCapacityExceeded("d", 1); RevisionConflict(1, 2)
          IllegalTransition("a", "b"); UnknownActivity "x"; SplitDurationMismatch(1, 2); EvidenceAssignmentInvalid; IncompatibleMergeSources "x"
          PublicationStateConflict "x"; TimerAlreadyActive; NoActiveTimer; TimerNotPaused; TimerPaused
          LongRunningTimerNeedsReview 1; ApprovalNotEnabled; StaleApproval "x"; NotBillableActivity "x"; AlreadyPublished "x"
          NotApproved "x"; BillingPolicyNotFound ]

    let codes = samples |> List.map code
    Assert.Equal(codes.Length, List.distinct codes |> List.length)
    Assert.All(codes, fun c -> Assert.Matches("^CHRONA\\.[A-Z]+\\.[A-Z_]+$", c))
    Assert.Equal("CHRONA.OVERLAP.OVERLAPS_ACTIVITY", code (OverlapsActivity "A1"))
    match tryZone "Not/AZone" with
    | Error problem -> Assert.Equal(InvalidTimeZone "Not/AZone", problem)
    | Ok _ -> failwith "an unknown zone resolved"
