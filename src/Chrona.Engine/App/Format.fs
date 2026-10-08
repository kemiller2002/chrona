/// How Chrona writes times, durations and dates for people: the legacy
/// application's conventions (`1h 46m`, `9:18`, `Thursday, October 8`),
/// invariant and deterministic. Pure.
module Chrona.Engine.App.Format

open System
open System.Globalization

let private invariant = CultureInfo.InvariantCulture

/// `0m`, `52m`, `1h 46m`, `3h`.
let minutes (total: int) =
    let h, m = total / 60, total % 60

    match h, m with
    | 0, m -> $"{m}m"
    | h, 0 -> $"{h}h"
    | h, m -> $"{h}h {m:D2}m"

/// Decimal hours to one place: `4.3h`.
let decimalHours (total: int) =
    (float total / 60.0).ToString("0.0", invariant) + "h"

/// A wall-clock time, unambiguous: `9:18 AM`, `2:05 PM`.
let clock (time: TimeOnly) = time.ToString("h:mm tt", invariant)

/// `Thursday, October 8`.
let longDate (date: DateOnly) = date.ToString("dddd, MMMM d", invariant)

/// `Thursday, October 8, 2026`.
let fullDate (date: DateOnly) = date.ToString("dddd, MMMM d, yyyy", invariant)

/// The ISO date an `<input type="date">` uses: `2026-10-08`.
let isoDate (date: DateOnly) = date.ToString("yyyy-MM-dd", invariant)

let parseIsoDate (text: string) =
    match DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", invariant, DateTimeStyles.None) with
    | true, date -> Some date
    | _ -> None

/// The `HH:mm` an `<input type="time">` sends (seconds, if present, must be
/// zero: entry is in whole minutes).
let parseTime (text: string) =
    match TimeOnly.TryParseExact(text.Trim(), [| "HH:mm"; "HH:mm:ss" |], invariant, DateTimeStyles.None) with
    | true, time when time.Second = 0 -> Some time
    | _ -> None

/// A running timer's display: `00:32:18`.
let elapsed (span: TimeSpan) =
    let span = if span < TimeSpan.Zero then TimeSpan.Zero else span
    let hours = int span.TotalHours
    $"{hours:D2}:{span.Minutes:D2}:{span.Seconds:D2}"

/// The same, spoken: `32 minutes and 18 seconds`.
let elapsedSpoken (span: TimeSpan) =
    let span = if span < TimeSpan.Zero then TimeSpan.Zero else span
    let unit n (one: string) = if n = 1 then $"1 {one}" else $"{n} {one}s"

    [ if int span.TotalHours > 0 then unit (int span.TotalHours) "hour"
      if span.Minutes > 0 then unit span.Minutes "minute"
      unit span.Seconds "second" ]
    |> function
        | [ only ] -> only
        | parts -> String.Join(", ", List.take (parts.Length - 1) parts) + " and " + List.last parts

/// Two-letter initials for an identity mark: `Local session` -> `LS`.
let initials (name: string) =
    name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
    |> Array.truncate 2
    |> Array.map (fun word -> Char.ToUpperInvariant word[0] |> string)
    |> String.concat ""
