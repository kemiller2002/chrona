/// The timer (WI-0022): requirements expansion 10 and scenario tests 2-5
/// and 9 of section 43.
module Chrona.Tests.TimerTests

open System
open Xunit
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Domain.Timer

let private zone =
    match tryZone "America/New_York" with
    | Ok z -> z
    | Error e -> failwith $"{e}"

/// Local New York wall-clock instants in October 2026 (EDT, -04:00).
let private local (day: int) (h: int) (m: int) = DateTimeOffset(2026, 10, day, h, m, 0, TimeSpan.FromHours -4.0)

let private work =
    Some { ProjectId = "PRJ-1"; ClientId = None; EngagementId = None; ActivityTypeId = "ACT-DEV"; Tags = []; Description = "Work"; BusinessPurpose = "Delivery" }

let private ok result =
    match result with
    | Ok value -> value
    | Error problem -> failwith $"{problem}"

let private started at = start "T1" "ORG-1" "ACTOR-1" "laptop" zone work at Idle |> ok

[<Fact>]
let ``scenario 2: a timer records exact minutes as an interval`` () =
    let _, stopped = started (local 7 9 0) |> stop zone (local 7 10 15) |> ok
    Assert.Equal(75, stopped.TotalMinutes)
    let activities = toActivities (local 7 10 15) (sprintf "A%d") Billable stopped |> ok
    let a = List.exactlyOne activities
    Assert.Equal(Interval(local 7 9 0, local 7 10 15), a.Timing)
    Assert.Equal(Timer, a.EntryMethod)
    Assert.Equal<string list>([ "T1" ], a.Lineage)

[<Fact>]
let ``only one timer may be active, and commands must fit the state`` () =
    let running = started (local 7 9 0)
    Assert.Equal(Error TimerAlreadyActive, start "T2" "ORG-1" "ACTOR-1" "phone" zone work (local 7 9 5) running)
    Assert.Equal(Error TimerNotPaused, resume (local 7 9 5) running)
    let paused = pause (local 7 9 10) running |> ok
    Assert.Equal(Error TimerPaused, pause (local 7 9 11) paused)
    Assert.Equal(Error TimerAlreadyActive, start "T2" "ORG-1" "ACTOR-1" "phone" zone work (local 7 9 5) paused)
    Assert.Equal(Error NoActiveTimer, stop zone (local 7 9 0) Idle |> Result.map snd)

[<Fact>]
let ``scenario 3: paused time is not worked time, and each worked run is its own interval`` () =
    let state =
        started (local 7 9 0)
        |> pause (local 7 9 30) |> ok
        |> resume (local 7 10 0) |> ok

    Assert.Equal(45, elapsedMinutes (local 7 10 15) (match state with Running t -> t | _ -> failwith "running"))
    let _, stopped = stop zone (local 7 10 20) state |> ok
    Assert.Equal(50, stopped.TotalMinutes)
    let activities = toActivities (local 7 10 20) (sprintf "A%d") Billable stopped |> ok
    Assert.Equal<Timing list>([ Interval(local 7 9 0, local 7 9 30); Interval(local 7 10 0, local 7 10 20) ], activities |> List.map _.Timing)

[<Fact>]
let ``scenario 4: a persisted timer is recovered explicitly with its elapsed time`` () =
    // The edge persisted the state and the browser restarted two hours later.
    let persisted = started (local 7 9 0)
    match recover (local 7 11 0) persisted with
    | RecoveredRunning(timer, minutes) ->
        Assert.Equal("T1", timer.TimerId)
        Assert.Equal(120, minutes)
    | other -> failwith $"{other}"

    let paused = persisted |> pause (local 7 9 30) |> ok
    Assert.Equal(RecoveredPaused((match paused with Paused t -> t | _ -> failwith ""), 30), recover (local 7 23 0) paused)
    Assert.Equal(NothingToRecover, recover (local 7 23 0) Idle)

[<Fact>]
let ``scenario 5: a timer across midnight is split at the business-day boundary`` () =
    let _, stopped = started (local 7 23 0) |> stop zone (local 8 1 30) |> ok
    Assert.Equal(150, stopped.TotalMinutes)
    let activities = toActivities (local 8 1 30) (sprintf "A%d") Billable stopped |> ok
    Assert.Equal<DateOnly list>([ DateOnly(2026, 10, 7); DateOnly(2026, 10, 8) ], activities |> List.map (fun a -> a.Occurrence.LocalDate))
    Assert.Equal<int list>([ 60; 90 ], activities |> List.map _.Minutes)
    Assert.All(activities, fun a -> Assert.Equal<string list>([ "T1" ], a.Lineage))

[<Fact>]
let ``a timer across a DST change counts real minutes`` () =
    // 00:30 EDT to 01:30 EST on the fall-back day is two real hours.
    let startAt = DateTimeOffset(2026, 11, 1, 0, 30, 0, TimeSpan.FromHours -4.0)
    let stopAt = DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours -5.0)
    let _, stopped = start "T1" "ORG-1" "ACTOR-1" "laptop" zone work startAt Idle |> ok |> stop zone stopAt |> ok
    Assert.Equal(120, stopped.TotalMinutes)

[<Fact>]
let ``an unclassified or unusually long timer is held, not recorded`` () =
    let _, unclassified = start "T1" "ORG-1" "ACTOR-1" "laptop" zone None (local 7 9 0) Idle |> ok |> stop zone (local 7 9 30) |> ok
    Assert.True(unclassified.NeedsClassification)
    Assert.Equal(Error [ MissingField "classification" ], toActivities (local 7 9 30) string Billable unclassified)
    let classified = start "T1" "ORG-1" "ACTOR-1" "laptop" zone None (local 7 9 0) Idle |> ok |> classify work.Value |> ok
    Assert.False((stop zone (local 7 9 30) classified |> ok |> snd).NeedsClassification)

    let _, long = started (local 7 6 0) |> stop zone (local 7 19 0) |> ok
    Assert.Equal(Some(LongRunningTimerNeedsReview 780), long.NeedsReview)
    Assert.Equal(Error [ LongRunningTimerNeedsReview 780 ], toActivities (local 7 19 0) string Billable long)

[<Fact>]
let ``scenario 9: overlapping timers from two devices are a reconciliation obligation`` () =
    let laptop = match started (local 7 9 0) with Running t -> t | _ -> failwith ""
    let phone = { laptop with TimerId = "T2"; DeviceId = "phone"; Segments = [ { Start = local 7 9 30; Finish = None } ] }
    let other = { laptop with TimerId = "T3"; ActorId = "ACTOR-2" }
    let later = { laptop with TimerId = "T4"; Segments = [ { Start = local 7 12 0; Finish = Some(local 7 13 0) } ] }
    Assert.Equal<(string * string) list>([ "T1", "T2" ], concurrent (local 7 10 0) [ laptop; phone; other; later ])
