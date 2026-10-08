/// The timer (requirements expansion 10): one active timer per actor,
/// pause and resume, elapsed time derived only from persisted segment
/// timestamps, explicit recovery, automatic split at business-day
/// boundaries, review of unusually long timers, and detection of concurrent
/// timers started on different devices.
///
/// Pure: every command carries the instant it happened at; the timer is
/// plain data the edge persists (local storage, then the authoritative
/// store) and hands back on startup.
module Chrona.Domain.Timer

open System
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity

/// A run of uninterrupted timing. `Finish = None` is the running segment.
type Segment =
    { Start: DateTimeOffset
      Finish: DateTimeOffset option }

type ActiveTimer =
    { TimerId: string
      OrganizationId: string
      ActorId: string
      DeviceId: string
      ZoneId: string
      Segments: Segment list
      Classification: Classification option }

type TimerState =
    | Idle
    | Running of ActiveTimer
    | Paused of ActiveTimer

/// Above this, a stopped timer's time is held for review rather than
/// assumed worked (10.4).
[<Literal>]
let LongRunningMinutes = 720

/// Whole seconds of timing up to `now`; a running segment counts to `now`.
let private elapsedSeconds (now: DateTimeOffset) (timer: ActiveTimer) =
    timer.Segments
    |> List.sumBy (fun s -> max 0.0 ((defaultArg s.Finish now) - s.Start).TotalSeconds)

/// Elapsed whole minutes, from the persisted timestamps alone (10.1).
let elapsedMinutes (now: DateTimeOffset) (timer: ActiveTimer) = int (elapsedSeconds now timer / 60.0)

let start (timerId: string) (organizationId: string) (actorId: string) (deviceId: string) (zone: Zone) (classification: Classification option) (at: DateTimeOffset) (state: TimerState) =
    match state with
    | Running _
    | Paused _ -> Error TimerAlreadyActive
    | Idle ->
        Ok(
            Running
                { TimerId = timerId
                  OrganizationId = organizationId
                  ActorId = actorId
                  DeviceId = deviceId
                  ZoneId = zone.Id
                  Segments = [ { Start = at; Finish = None } ]
                  Classification = classification }
        )

let private closeOpen (at: DateTimeOffset) (timer: ActiveTimer) =
    { timer with
        Segments = timer.Segments |> List.map (fun s -> if s.Finish.IsNone then { s with Finish = Some(max at s.Start) } else s) }

let pause (at: DateTimeOffset) (state: TimerState) =
    match state with
    | Running timer -> Ok(Paused(closeOpen at timer))
    | Paused _ -> Error TimerPaused
    | Idle -> Error NoActiveTimer

let resume (at: DateTimeOffset) (state: TimerState) =
    match state with
    | Paused timer -> Ok(Running { timer with Segments = timer.Segments @ [ { Start = at; Finish = None } ] })
    | Running _ -> Error TimerNotPaused
    | Idle -> Error NoActiveTimer

let classify (classification: Classification) (state: TimerState) =
    match state with
    | Running timer -> Ok(Running { timer with Classification = Some classification })
    | Paused timer -> Ok(Paused { timer with Classification = Some classification })
    | Idle -> Error NoActiveTimer

/// What a persisted timer means on startup (10.2): it is recovered
/// explicitly with its elapsed time, never silently discarded.
type Recovery =
    | NothingToRecover
    | RecoveredRunning of timer: ActiveTimer * elapsedMinutes: int
    | RecoveredPaused of timer: ActiveTimer * elapsedMinutes: int

let recover (now: DateTimeOffset) (persisted: TimerState) =
    match persisted with
    | Idle -> NothingToRecover
    | Running timer -> RecoveredRunning(timer, elapsedMinutes now timer)
    | Paused timer -> RecoveredPaused(timer, elapsedMinutes now timer)

/// One contiguous piece of timed work within one business day.
type TimedPiece =
    { Occurrence: Occurrence
      Start: DateTimeOffset
      Finish: DateTimeOffset
      Minutes: int }

/// The result of stopping: pieces split at business-day boundaries (10.3),
/// and the obligations that keep them from being recorded unreviewed.
type Stopped =
    { Timer: ActiveTimer
      Pieces: TimedPiece list
      TotalMinutes: int
      /// The timer had no classification yet: an unclassified-time obligation.
      NeedsClassification: bool
      /// Unusually long: review before recording (10.4).
      NeedsReview: Diagnostic option }

/// Whole minutes for a number of working seconds: the nearest minute, half a
/// minute rounding up (legacy "rounded to the nearest whole minute"). Less
/// than thirty seconds is therefore nothing (DF-CHRONA-2026-0002 R5).
let private nearestMinutes (seconds: float) = int (Math.Floor((seconds + 30.0) / 60.0))

/// A segment clipped to one business day, at full precision.
type private Clip =
    { Date: DateOnly
      DayStart: DateTimeOffset
      DayEnd: DateTimeOffset
      Start: DateTimeOffset
      Finish: DateTimeOffset }

let private clips (zone: Zone) (timer: ActiveTimer) =
    timer.Segments
    |> List.sortBy _.Start
    |> List.collect (fun segment ->
        let finish = defaultArg segment.Finish segment.Start

        let rec cut (from: DateTimeOffset) =
            if from >= finish then
                []
            else
                let date = (occurrence zone from).LocalDate
                let dayStart, dayEnd = dayBounds zone date
                let until = min finish dayEnd

                { Date = date
                  DayStart = dayStart
                  DayEnd = dayEnd
                  Start = from
                  Finish = until }
                :: cut until

        cut segment.Start)

/// The recorded pieces. The working total is rounded once, to the nearest
/// minute, and shared out by cumulative rounding, so the pieces always add
/// up to exactly that total however many pauses and midnights there were.
/// Each piece is then placed as a whole-minute interval as close as possible
/// to when the work happened: never overlapping another piece of the same
/// timer and never crossing its business day (10.3).
let private pieces (zone: Zone) (timer: ActiveTimer) =
    let all = clips zone timer

    let minutes =
        all
        |> List.scan (fun total clip -> total + (clip.Finish - clip.Start).TotalSeconds) 0.0
        |> List.map nearestMinutes
        |> List.pairwise
        |> List.map (fun (before, after) -> after - before)

    let place (day: (Clip * int) list) =
        // Forward: from when each run began, after the previous piece.
        let forward =
            day
            |> List.scan
                (fun (_, finish: DateTimeOffset) (clip: Clip, m: int) ->
                    let start = max clip.Start finish
                    start, start.AddMinutes(float m))
                (DateTimeOffset.MinValue, (fst day.Head).DayStart)
            |> List.tail

        // Backward: pulled inside the business day where rounding pushed a
        // piece past its end.
        let dayEnd = (fst day.Head).DayEnd

        List.foldBack
            (fun ((clip: Clip, m: int), (_, finish: DateTimeOffset)) (limit: DateTimeOffset, placed: TimedPiece list) ->
                let finish = min finish limit
                let start = finish.AddMinutes(-(float m))

                start,
                { Occurrence = { occurrence zone start with LocalDate = clip.Date }
                  Start = start
                  Finish = finish
                  Minutes = m }
                :: placed)
            (List.zip day forward)
            (dayEnd, [])
        |> snd

    List.zip all minutes
    |> List.filter (fun (_, m) -> m > 0)
    |> List.groupBy (fun (clip, _) -> clip.Date)
    |> List.collect (snd >> place)

let stop (zone: Zone) (at: DateTimeOffset) (state: TimerState) =
    match state with
    | Idle -> Error NoActiveTimer
    | Running timer
    | Paused timer ->
        let closed = closeOpen at timer
        let parts = pieces zone closed
        let total = parts |> List.sumBy _.Minutes

        Ok(
            Idle,
            { Timer = closed
              Pieces = parts
              TotalMinutes = total
              NeedsClassification = closed.Classification.IsNone
              NeedsReview = if total > LongRunningMinutes then Some(LongRunningTimerNeedsReview total) else None }
        )

/// Activities for a stopped timer once it is classified (and reviewed when
/// required). Each piece keeps the timer id as lineage, so a cross-midnight
/// split stays traceable to the one timer (10.3).
let toActivities (now: DateTimeOffset) (newId: int -> string) (billability: Billability) (stopped: Stopped) =
    match stopped.Timer.Classification, stopped.NeedsReview with
    | None, _ -> Error [ MissingField "classification" ]
    | Some _, Some review -> Error [ review ]
    | Some classification, None ->
        Ok(
            stopped.Pieces
            |> List.mapi (fun index piece ->
                { ActivityId = newId index
                  OrganizationId = stopped.Timer.OrganizationId
                  ActorId = stopped.Timer.ActorId
                  Occurrence = piece.Occurrence
                  Timing = Interval(piece.Start, piece.Finish)
                  Minutes = piece.Minutes
                  Classification = classification
                  EntryMethod = Timer
                  Billability = billability
                  BillingReference = noBillingReference
                  Record = Recorded
                  Review = Unsubmitted
                  Publication = if billability = NonBillable then NotBillable else Unpublished
                  Revision = 1
                  CreatedAt = now
                  LastChangedAt = now
                  Reason = None
                  WorkItemRef = None
                  ExternalRef = None
                  Evidence = []
                  Lineage = [ stopped.Timer.TimerId ]
                  Source = None })
        )

/// Timers for the same actor whose timing overlaps, typically started on
/// two devices while offline (10.5). Each pair is a reconciliation
/// obligation; neither is recorded over the other silently.
let concurrent (now: DateTimeOffset) (timers: ActiveTimer list) =
    let spans (t: ActiveTimer) = t.Segments |> List.map (fun s -> s.Start, defaultArg s.Finish now)
    let overlaps a b = spans a |> List.exists (fun (s1, e1) -> spans b |> List.exists (fun (s2, e2) -> s1 < e2 && s2 < e1))

    [ for i, a in List.indexed timers do
          for b in List.skip (i + 1) timers do
              if a.ActorId = b.ActorId && a.OrganizationId = b.OrganizationId && overlaps a b then
                  yield a.TimerId, b.TimerId ]
