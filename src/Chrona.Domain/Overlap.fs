/// Overlap and capacity rules (requirements expansion 12).
///
/// Scoped by organization and actor. Only records that consume time count:
/// voided and superseded records do not; submitted and approved ones do.
/// Intervals are half-open, so back-to-back entries (one ends at 10:00, the
/// next starts at 10:00) do not overlap. Entries without clock times are
/// checked against the business day's real length (23 or 25 hours on a DST
/// day).
module Chrona.Domain.Overlap

open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity

let private sameScope (a: Activity) (b: Activity) =
    a.OrganizationId = b.OrganizationId && a.ActorId = b.ActorId

let private intersects (s1, e1) (s2, e2) = s1 < e2 && s2 < e1

let private peersOf (existing: Activity list) (candidate: Activity) =
    existing
    |> List.filter (fun other -> other.ActivityId <> candidate.ActivityId && sameScope other candidate && consumesTime other)

/// The ids of the activities in `existing` whose clock times overlap
/// `candidate`'s, in order: same organization and actor, time-consuming
/// records only, half-open intervals. Needs no zone rules.
let overlapping (existing: Activity list) (candidate: Activity) : string list =
    match interval candidate with
    | None -> []
    | Some span ->
        peersOf existing candidate
        |> List.filter (fun other -> interval other |> Option.exists (intersects span))
        |> List.map _.ActivityId
        |> List.sort

/// Everything that stops `candidate` from being recorded alongside
/// `existing`, in a stable order. `zone` is the candidate's business zone.
let check (zone: Zone) (existing: Activity list) (candidate: Activity) : Diagnostic list =
    let peers = peersOf existing candidate

    let overlaps = overlapping existing candidate |> List.map OverlapsActivity

    let capacity =
        let date = candidate.Occurrence.LocalDate
        let start, finish = dayBounds zone date
        let dayMinutes = int (finish - start).TotalMinutes

        let total =
            candidate.Minutes
            + (peers |> List.filter (fun other -> other.Occurrence.LocalDate = date) |> List.sumBy _.Minutes)

        if total > dayMinutes then [ DailyCapacityExceeded(date.ToString "yyyy-MM-dd", total) ] else []

    overlaps @ capacity
