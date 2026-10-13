/// The day review and the month.
module Chrona.Engine.App.ProjectReview

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Engine.View
open Chrona.Engine.App.Model
open Chrona.Engine.App.ProjectWords
open Chrona.Engine.App.ProjectShell
open Chrona.Engine.App.ProjectToday
open Chrona.Engine.App.ProjectTrack
open Chrona.Engine.App.ProjectMore
open Chrona.Engine.App.ProjectConflicts
open Chrona.Engine.App.ProjectCandidates
open Chrona.Engine.App.ProjectObligations
open Chrona.Engine.App.ProjectDetail

let internal reviewView (model: Model) =
    let date = selectedDate model
    let shown = dayActivities model date
    let counted = shown |> List.filter consumesTime
    let byMethod m = counted |> List.filter (fun a -> a.EntryMethod = m)
    let summary (list: Activity list) = $"{list.Length} · {Format.minutes (list |> List.sumBy _.Minutes)}"
    let attested = model.Attestations |> List.filter (fun a -> a.LocalDate = date)
    let changed = staleAttestations model |> List.tryFind (fst >> (=) date) |> Option.map snd |> Option.defaultValue []
    let historical = counted |> List.filter (fun a -> a.EntryMethod = Manual && date < Model.today model)

    let checks =
        [ "descriptions", "Every activity says what was done", (counted |> List.forall (fun a -> a.Classification.Description <> "")), "Complete", "Missing"
          "purposes", "Every activity has a business purpose", (counted |> List.forall (fun a -> a.Classification.BusinessPurpose <> "")), "Complete", "Missing"
          "overlap", "No overlapping time", true, "Clear", "Overlap"
          "explained", "Manual entries for an earlier day are explained", (historical |> List.forall (fun a -> a.Reason.IsSome)), "Included", "Missing"
          "attested", "Nothing changed since the last attestation", changed.IsEmpty, (if attested.IsEmpty then "Not yet attested" else "Unchanged"), "Changed"
          "saved", "Every change has been stored", model.Store.Problem.IsNone && model.Store.Pending.IsEmpty && model.Store.Conflicts.IsEmpty, (match model.Store.Kind with InMemory -> "In this tab" | Durable _ -> "Saved"), "Attention" ]
        |> List.map (fun (id, title, ok, good, bad) ->
            [ t "id" id; t "title" title; t "status" (if ok then good else bad); t "tone" (if ok then "ok" else "attention") ])

    [ flag "screenReview" (shows model (function Places.ReviewToday | Places.Review _ -> true | _ -> false))
      text "reviewTitle" (Format.longDate date)
      text "reviewTotal" (Format.minutes (counted |> List.sumBy _.Minutes))
      text "reviewCount" (plural counted.Length "included entry" "included entries")
      text "reviewTimer" (summary (byMethod EntryMethod.Timer))
      text "reviewManual" (summary (byMethod Manual))
      text "reviewEvidence" (percent (counted |> List.filter (fun a -> not a.Evidence.IsEmpty) |> List.length) counted.Length)
      text "reviewCorrections" (counted |> List.filter (fun a -> a.Revision > 1) |> List.length |> string)
      items "reviewChecks" checks
      flag "hasAttestations" (not attested.IsEmpty)
      items
          "attestations"
          (attested
           |> List.rev
           |> List.mapi (fun index a -> [ t "id" $"{index}"; t "statement" a.Statement; t "at" $"Attested {localStamp model a.At} · {activities a.Covered.Length}" ]))
      flag "changedSinceAttestation" (not changed.IsEmpty)
      items
          "changedActivities"
          (changed
           |> List.map (fun id ->
               [ t "id" id
                 t "title" (model.Ledger.Activities.TryFind id |> Option.map _.Classification.Description |> Option.defaultValue id) ]))
      text "attestStatement" model.AttestStatement
      flag "hasAttestProblems" (model.Problems.ContainsKey AttestForm)
      items "attestProblems" (problemItems model AttestForm) ]

let internal monthView (model: Model) =
    let first = selectedMonth model
    let last = first.AddMonths(1).AddDays -1

    let inMonth =
        model.Ledger.Activities
        |> Map.toList
        |> List.map snd
        |> List.filter (fun a -> a.ActorId = model.Session.ActorId && a.Occurrence.LocalDate >= first && a.Occurrence.LocalDate <= last)

    let counted = inMonth |> List.filter consumesTime
    let total = counted |> List.sumBy _.Minutes
    let sumOf m = counted |> List.filter (fun a -> a.EntryMethod = m) |> List.sumBy _.Minutes

    let byType =
        counted
        |> List.groupBy _.Classification.ActivityTypeId
        |> List.map (fun (id, list) -> id, list |> List.sumBy _.Minutes)
        |> List.sortByDescending snd
        |> List.map (fun (id, minutes) ->
            [ t "id" id
              t "name" (referenceName model Reference.ActivityType id)
              t "total" (Format.minutes minutes)
              n "share" (if total = 0 then 0.0 else Math.Round(100.0 * float minutes / float total))
              t "shareText" (percent minutes total) ])

    let days =
        counted
        |> List.groupBy _.Occurrence.LocalDate
        |> List.sortBy fst
        |> List.map (fun (date, list) ->
            [ t "id" (Format.isoDate date)
              t "label" (Format.longDate date)
              t "total" (Format.minutes (list |> List.sumBy _.Minutes))
              t "count" (plural list.Length "entry" "entries") ])

    let reading = List.contains (first.Year, first.Month) model.Store.Reading

    let history =
        model.Store.History
        |> List.filter (fun total -> total.ActorId = model.Session.ActorId)
        |> List.map (fun total ->
            let month = DateOnly(total.Year, total.Month, 1)

            [ t "id" $"{total.Year:D4}-{total.Month:D2}"
              t "label" (month.ToString("MMMM yyyy", Globalization.CultureInfo.InvariantCulture))
              t "total" (Format.minutes total.Minutes)
              t "count" (plural total.Activities "entry" "entries")
              t "approved" (if total.ApprovedMinutes = 0 then "" else $"{Format.minutes total.ApprovedMinutes} approved")
              t "current" (if month = first then "true" else "false") ])

    [ flag "screenMonth" (shows model (function Places.ThisMonth | Places.Month _ -> true | _ -> false))
      text "monthTitle" (first.ToString("MMMM yyyy", Globalization.CultureInfo.InvariantCulture))
      text "monthIso" $"{first.Year:D4}-{first.Month:D2}"
      text "monthTotal" (Format.minutes total)
      text "monthBilled" (Format.minutes (counted |> List.sumBy (fun a -> Billing.billableMinutes (billing model) a.Minutes)))
      text "monthDecimal" (Format.decimalHours total)
      text "monthActiveDays" (string days.Length)
      text "monthTimerManual" $"{Format.minutes (sumOf EntryMethod.Timer)} / {Format.minutes (sumOf Manual)}"
      text "monthCorrections" (counted |> List.filter (fun a -> a.Revision > 1) |> List.length |> string)
      text "monthVoided" (inMonth |> List.filter (fun a -> match a.Record with Voided _ -> true | _ -> false) |> List.length |> string)
      text "monthEvidence" (percent (counted |> List.filter (fun a -> not a.Evidence.IsEmpty) |> List.length) counted.Length)
      flag "monthEmpty" (counted.IsEmpty && not reading)
      flag "monthReading" reading
      items "monthByType" byType
      items "monthDays" days
      // Every month that holds the person's time, from the activity index
      // (derived, rebuildable): one read, not one per month (38, 40).
      flag "hasMonthHistory" (not history.IsEmpty)
      items "monthHistory" history
      text "monthIndex" model.Store.Index ]
