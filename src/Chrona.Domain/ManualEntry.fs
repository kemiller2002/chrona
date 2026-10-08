/// Manual entry (requirements expansion 9, 11): start and end, start and
/// duration, or a duration on a date; exact whole minutes; never in the
/// future; within one business day; historical entries explained; overlap
/// rules applied. Every problem is reported, not just the first.
module Chrona.Domain.ManualEntry

open System
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity

type ManualTiming =
    /// Local start and end on one business date. An explicit offset
    /// disambiguates a time that occurs twice at a DST fall-back.
    | StartAndEnd of date: DateOnly * start: TimeOnly * finish: TimeOnly * startOffset: int option * finishOffset: int option
    | StartAndDuration of date: DateOnly * start: TimeOnly * minutes: int * startOffset: int option
    | DurationOnly of date: DateOnly * minutes: int

[<NoComparison>]
type ManualEntry =
    { ActivityId: string
      OrganizationId: string
      ActorId: string
      Zone: Zone
      Timing: ManualTiming
      Classification: Classification
      Billability: Billability
      BillingReference: BillingReference
      Reason: string option
      WorkItemRef: string option
      Evidence: Evidence list }

/// What the edge supplies: the current instant and the organization's
/// historical-entry window.
type EntryContext =
    { Now: DateTimeOffset
      /// The organization's reference data: new work names active items.
      References: Reference.Catalogue
      /// Entries for business dates more than this many days before today
      /// need an explanatory reason.
      HistoricalAfterDays: int }

let private blank (text: string) = String.IsNullOrWhiteSpace text


/// The resolved interval (if any), the minutes and the business date.
let private timing (zone: Zone) (t: ManualTiming) : Result<Timing * int * DateOnly, Diagnostic list> =
    let wholeMinutes (time: TimeOnly) = time.Ticks % TimeSpan.TicksPerMinute = 0L

    let within (date: DateOnly) (start: DateTimeOffset) (finish: DateTimeOffset) =
        let _, nextDay = dayBounds zone date
        if finish > nextDay then Error [ CrossesBusinessDay ] else Ok()

    match t with
    | StartAndEnd(date, start, finish, startOffset, finishOffset) ->
        if not (wholeMinutes start && wholeMinutes finish) then
            Error [ DurationNotWholeMinutes ]
        else
            match resolveLocal zone startOffset date start, resolveLocal zone finishOffset date finish with
            | Ok s, Ok f ->
                match minutesBetween s f with
                | Ok minutes -> within date s f |> Result.map (fun () -> Interval(s, f), minutes, date)
                | Error d -> Error [ d ]
            | a, b -> Error([ a; b ] |> List.choose (function Error d -> Some d | Ok _ -> None) |> List.distinct)
    | StartAndDuration(date, start, minutes, startOffset) ->
        if minutes <= 0 then
            Error [ DurationNotPositive ]
        elif not (wholeMinutes start) then
            Error [ DurationNotWholeMinutes ]
        else
            match resolveLocal zone startOffset date start with
            | Ok s ->
                let f = s.AddMinutes(float minutes)
                within date s f |> Result.map (fun () -> Interval(s, f), minutes, date)
            | Error d -> Error [ d ]
    | DurationOnly(date, minutes) ->
        let start, finish = dayBounds zone date

        if minutes <= 0 then Error [ DurationNotPositive ]
        elif minutes > int (finish - start).TotalMinutes then Error [ CrossesBusinessDay ]
        else Ok(DurationOnDate minutes, minutes, date)

/// Validates a manual entry against the actor's existing activities and
/// creates the authoritative record (revision 1).
let create (context: EntryContext) (existing: Activity list) (entry: ManualEntry) : Result<Activity, Diagnostic list> =
    let today = (occurrence entry.Zone context.Now).LocalDate

    match timing entry.Zone entry.Timing with
    | Error problems -> Error(problems @ classificationProblems entry.Classification)
    | Ok(timing, minutes, date) ->
        let future =
            match timing with
            | Interval(_, finish) when finish > context.Now -> [ FutureTime ]
            | DurationOnDate _ when date > today -> [ FutureTime ]
            | _ -> []

        let historical =
            if date < today.AddDays(-context.HistoricalAfterDays) && (entry.Reason |> Option.forall blank) then
                [ ReasonRequiredForHistoricalEntry ]
            else
                []

        let start =
            match timing with
            | Interval(s, _) -> s
            | DurationOnDate _ -> fst (dayBounds entry.Zone date)

        let activity =
            { ActivityId = entry.ActivityId
              OrganizationId = entry.OrganizationId
              ActorId = entry.ActorId
              Occurrence = { occurrence entry.Zone start with LocalDate = date }
              Timing = timing
              Minutes = minutes
              Classification = entry.Classification
              EntryMethod = Manual
              Billability = entry.Billability
              BillingReference = entry.BillingReference
              Record = Recorded
              Review = Unsubmitted
              Publication = if entry.Billability = NonBillable then NotBillable else Unpublished
              Revision = 1
              CreatedAt = context.Now
              LastChangedAt = context.Now
              Reason = entry.Reason |> Option.filter (blank >> not)
              WorkItemRef = entry.WorkItemRef
              ExternalRef = None
              Evidence = entry.Evidence
              Lineage = [] }

        match
            future
            @ historical
            @ classificationProblems entry.Classification
            @ Reference.assignmentProblems context.References [] entry.Classification
            @ Overlap.check entry.Zone existing activity
        with
        | [] -> Ok activity
        | problems -> Error problems

/// Copies a prior entry's classification into a new draft with a new
/// identity (9): the copy is a new activity, never a revision of the source.
let copyAsDraft (newId: string) (zone: Zone) (date: DateOnly) (minutes: int) (source: Activity) : ManualEntry =
    { ActivityId = newId
      OrganizationId = source.OrganizationId
      ActorId = source.ActorId
      Zone = zone
      Timing = DurationOnly(date, minutes)
      Classification = source.Classification
      Billability = source.Billability
      BillingReference = source.BillingReference
      Reason = None
      WorkItemRef = source.WorkItemRef
      Evidence = [] }
