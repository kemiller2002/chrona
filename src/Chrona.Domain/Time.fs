/// Time-zone and calendar semantics (requirements expansion 11).
///
/// Instants are stored as UTC `DateTimeOffset`s, so elapsed time is exact
/// across DST transitions. Each activity also keeps its occurrence context
/// (IANA zone, UTC offset at the time, local date and local start), so a
/// later device time-zone change never rewrites history. Zone rules arrive
/// as a `TimeZoneInfo` argument: this module never reads the clock or the
/// machine's zone.
module Chrona.Domain.Time

open System
open Chrona.Domain.Diagnostics

/// Where and when, in the respondent's own terms, an activity happened.
type Occurrence =
    { /// IANA time-zone id, e.g. "America/New_York".
      Zone: string
      /// UTC offset in effect at the instant, in minutes.
      OffsetMinutes: int
      LocalDate: DateOnly
      LocalTime: TimeOnly }

/// A named zone and its rules.
[<NoComparison>]
type Zone =
    { Id: string
      Rules: TimeZoneInfo }

/// Resolves an IANA id against the platform's zone database (an edge
/// concern; the domain only consumes the resulting `Zone`).
let tryZone (id: string) : Result<Zone, Diagnostic> =
    match TimeZoneInfo.TryFindSystemTimeZoneById id with
    | true, rules ->
        match Option.ofObj rules with
        | Some rules -> Ok { Id = id; Rules = rules }
        | None -> Error(InvalidTimeZone id)
    | _ -> Error(InvalidTimeZone id)

let occurrence (zone: Zone) (instant: DateTimeOffset) : Occurrence =
    let local = TimeZoneInfo.ConvertTime(instant, zone.Rules)

    { Zone = zone.Id
      OffsetMinutes = int local.Offset.TotalMinutes
      LocalDate = DateOnly.FromDateTime local.DateTime
      LocalTime = TimeOnly.FromDateTime local.DateTime }

/// The instant a local wall-clock time names. A time skipped by a DST
/// transition is invalid; a time that occurs twice is ambiguous unless the
/// caller states which offset it meant. Nothing is guessed.
let resolveLocal (zone: Zone) (offsetMinutes: int option) (date: DateOnly) (time: TimeOnly) : Result<DateTimeOffset, Diagnostic> =
    let local = date.ToDateTime(time, DateTimeKind.Unspecified)

    if zone.Rules.IsInvalidTime local then
        Error InvalidLocalTime
    elif zone.Rules.IsAmbiguousTime local then
        let offsets = zone.Rules.GetAmbiguousTimeOffsets local |> Array.map (fun o -> int o.TotalMinutes)

        match offsetMinutes with
        | Some chosen when Array.contains chosen offsets -> Ok(DateTimeOffset(local, TimeSpan.FromMinutes(float chosen)))
        | _ -> Error AmbiguousLocalTime
    else
        let offset = zone.Rules.GetUtcOffset local

        match offsetMinutes with
        | Some chosen when chosen <> int offset.TotalMinutes -> Error InvalidLocalTime
        | _ -> Ok(DateTimeOffset(local, offset))

/// The first instant of a local date and the first instant of the next one.
/// A DST day is 23 or 25 hours long; these bounds are exact.
let dayBounds (zone: Zone) (date: DateOnly) : DateTimeOffset * DateTimeOffset =
    let startOf (d: DateOnly) =
        // Midnight can itself be skipped in a few zones; the day then starts
        // at the first valid minute.
        let rec first (time: TimeOnly) =
            match resolveLocal zone None d time with
            | Ok instant -> instant
            | Error AmbiguousLocalTime ->
                let local = d.ToDateTime(time, DateTimeKind.Unspecified)
                let earliest = zone.Rules.GetAmbiguousTimeOffsets local |> Array.max
                DateTimeOffset(local, earliest)
            | Error _ -> first (time.AddMinutes 1.0)

        first TimeOnly.MinValue

    startOf date, startOf (date.AddDays 1)

/// Whole minutes between two instants, or a diagnostic when the interval is
/// empty, reversed or not a whole number of minutes.
let minutesBetween (start: DateTimeOffset) (finish: DateTimeOffset) : Result<int, Diagnostic> =
    let span = finish - start

    if span <= TimeSpan.Zero then Error(if span = TimeSpan.Zero then DurationNotPositive else EndBeforeStart)
    elif span.Ticks % TimeSpan.TicksPerMinute <> 0L then Error DurationNotWholeMinutes
    else Ok(int span.TotalMinutes)
