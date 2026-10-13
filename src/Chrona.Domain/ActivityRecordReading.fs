/// Reading an activity record back (requirements expansion 5, 22): every
/// field of the closed, versioned body validated, and anything this version
/// does not define refused. Pure.
module Chrona.Domain.ActivityRecordReading

open System
open System.Text
open Arca
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Domain.Codec
open Chrona.Domain.ActivityRecord

let private oneOf (name: string) (choices: (string * 'a) list) (found: string) : Decoded<'a> =
    match choices |> List.tryFind (fun (wire, _) -> wire = found) with
    | Some(_, value) -> Ok value
    | None -> Error $"'{found}' is not a {name}"

let private occurrenceOf value =
    field "occurrence" value
    |> Result.bind (fun o ->
        closed [ "localDate"; "localTime"; "offsetMinutes"; "zone" ] o
        |> Result.bind (fun () ->
            match text "zone" o, integer "offsetMinutes" o, date "localDate" o, time "localTime" o with
            | Ok zone, Ok offset, Ok localDate, Ok localTime ->
                Ok
                    { Zone = zone
                      OffsetMinutes = offset
                      LocalDate = localDate
                      LocalTime = localTime }
            | Error e, _, _, _
            | _, Error e, _, _
            | _, _, Error e, _
            | _, _, _, Error e -> Error e))

let private timingOf value =
    field "timing" value
    |> Result.bind (fun t ->
        text "kind" t
        |> Result.bind (function
            | "interval" ->
                closed [ "finish"; "kind"; "start" ] t
                |> Result.bind (fun () -> instant "start" t)
                |> Result.bind (fun start -> instant "finish" t |> Result.map (fun finish -> Interval(start, finish)))
            | "duration" ->
                closed [ "kind"; "minutes" ] t
                |> Result.bind (fun () -> integer "minutes" t)
                |> Result.map DurationOnDate
            | other -> Error $"'{other}' is not a timing"))

let private entryOf value =
    field "entryMethod" value
    |> Result.bind (fun e ->
        closed [ "kind"; "sourceSystem" ] e
        |> Result.bind (fun () -> text "kind" e)
        |> Result.bind (fun kind ->
            optionalText "sourceSystem" e
            |> Result.bind (fun system ->
                match kind, system with
                | "manual", None -> Ok Manual
                | "timer", None -> Ok Timer
                | "imported", Some system -> Ok(Imported system)
                | other, _ -> Error $"'{other}' with this source system is not an entry method")))

let private billingOf value =
    field "billingReference" value
    |> Result.bind (fun b ->
        closed [ "billingClass"; "contractReference"; "rateReference" ] b
        |> Result.bind (fun () ->
            match optionalText "rateReference" b, optionalText "billingClass" b, optionalText "contractReference" b with
            | Ok rate, Ok billingClass, Ok contract ->
                Ok
                    { RateReference = rate
                      BillingClass = billingClass
                      ContractReference = contract }
            | Error e, _, _
            | _, Error e, _
            | _, _, Error e -> Error e))

let private recordStateOf value =
    field "record" value
    |> Result.bind (fun r ->
        closed [ "by"; "reason"; "state" ] r
        |> Result.bind (fun () ->
            match text "state" r, optionalText "reason" r, texts "by" r with
            | Ok "recorded", Ok None, Ok [] -> Ok Recorded
            | Ok "voided", Ok(Some reason), Ok [] -> Ok(Voided reason)
            | Ok "superseded", Ok None, Ok(_ :: _ as by) -> Ok(Superseded by)
            | Ok state, Ok _, Ok _ -> Error $"'{state}' with this reason and successors is not a record state"
            | Error e, _, _
            | _, Error e, _
            | _, _, Error e -> Error e))

let private reviewOf value =
    field "review" value
    |> Result.bind (fun r ->
        closed [ "reason"; "state" ] r
        |> Result.bind (fun () ->
            match text "state" r, optionalText "reason" r with
            | Ok "unsubmitted", Ok None -> Ok Unsubmitted
            | Ok "submitted", Ok None -> Ok Submitted
            | Ok "approved", Ok None -> Ok Approved
            | Ok "reopened", Ok None -> Ok Reopened
            | Ok "rejected", Ok(Some reason) -> Ok(Rejected reason)
            | Ok state, Ok _ -> Error $"'{state}' with this reason is not a review state"
            | Error e, _
            | _, Error e -> Error e))

let private evidenceOf (value: Json) =
    closed [ "capturedAt"; "hash"; "id"; "kind"; "label"; "notes"; "source"; "url" ] value
    |> Result.bind (fun () ->
        match text "id" value, text "url" value, text "kind" value, text "label" value, instant "capturedAt" value, optionalText "hash" value with
        | Ok id, Ok url, Ok kind, Ok label, Ok captured, Ok hash ->
            match optionalText "source" value, optionalText "notes" value with
            | Ok source, Ok notes ->
                Ok
                    { Id = id
                      Url = url
                      Kind = kind
                      Label = label
                      CapturedAt = captured
                      Hash = hash
                      Source = source
                      Notes = notes }
            | Error e, _
            | _, Error e -> Error e
        | Error e, _, _, _, _, _
        | _, Error e, _, _, _, _
        | _, _, Error e, _, _, _
        | _, _, _, Error e, _, _
        | _, _, _, _, Error e, _
        | _, _, _, _, _, Error e -> Error e)

let private sourceOf (value: Json) =
    closed [ "externalUrl"; "ingestedAt"; "observationId"; "sourceSystem" ] value
    |> Result.bind (fun () ->
        match text "sourceSystem" value, text "observationId" value, optionalText "externalUrl" value, instant "ingestedAt" value with
        | Ok system, Ok observation, Ok url, Ok ingested ->
            Ok
                { SourceSystem = system
                  ObservationId = observation
                  ExternalUrl = url
                  IngestedAt = ingested }
        | Error e, _, _, _
        | _, Error e, _, _
        | _, _, Error e, _
        | _, _, _, Error e -> Error e)

let private fields =
    [ "activityId"
      "actorId"
      "billability"
      "billingReference"
      "classification"
      "createdAt"
      "entryMethod"
      "evidence"
      "externalRef"
      "lastChangedAt"
      "lineage"
      "minutes"
      "occurrence"
      "organizationId"
      "publication"
      "reason"
      "record"
      "review"
      "revision"
      "source"
      "timing"
      "workItemRef" ]

/// An activity from its record body.
let ofBody (value: Json) : Decoded<Activity> =
    closed fields value
    |> Result.bind (fun () ->
        match text "activityId" value, text "organizationId" value, text "actorId" value, occurrenceOf value, timingOf value, integer "minutes" value with
        | Ok id, Ok organization, Ok actor, Ok occurrence, Ok timing, Ok minutes ->
            match
                classificationOf value,
                entryOf value,
                text "billability" value
                |> Result.bind (oneOf "billability" [ for b in [ Billable; NonBillable; PendingClassification ] -> billabilityName b, b ]),
                billingOf value,
                recordStateOf value,
                reviewOf value,
                text "publication" value
                |> Result.bind (
                    oneOf
                        "publication state"
                        [ for p in [ NotBillable; Unpublished; ReadyForPublication; Published; InvoicedExternally; AdjustmentRequired ] ->
                              publicationName p, p ]
                )
            with
            | Ok classification, Ok entry, Ok billability, Ok billing, Ok record, Ok review, Ok publication ->
                match
                    integer "revision" value,
                    instant "createdAt" value,
                    instant "lastChangedAt" value,
                    optionalText "reason" value,
                    optionalText "workItemRef" value,
                    optionalText "externalRef" value
                with
                | Ok revision, Ok created, Ok changed, Ok reason, Ok workItem, Ok external ->
                    match list "evidence" evidenceOf value, texts "lineage" value, optional "source" sourceOf value with
                    | Ok evidence, Ok lineage, Ok source ->
                        Ok
                            { ActivityId = id
                              OrganizationId = organization
                              ActorId = actor
                              Occurrence = occurrence
                              Timing = timing
                              Minutes = minutes
                              Classification = classification
                              EntryMethod = entry
                              Billability = billability
                              BillingReference = billing
                              Record = record
                              Review = review
                              Publication = publication
                              Revision = revision
                              CreatedAt = created
                              LastChangedAt = changed
                              Reason = reason
                              WorkItemRef = workItem
                              ExternalRef = external
                              Evidence = evidence
                              Lineage = lineage
                              Source = source }
                    | Error e, _, _
                    | _, Error e, _
                    | _, _, Error e -> Error e
                | Error e, _, _, _, _, _
                | _, Error e, _, _, _, _
                | _, _, Error e, _, _, _
                | _, _, _, Error e, _, _
                | _, _, _, _, Error e, _
                | _, _, _, _, _, Error e -> Error e
            | Error e, _, _, _, _, _, _
            | _, Error e, _, _, _, _, _
            | _, _, Error e, _, _, _, _
            | _, _, _, Error e, _, _, _
            | _, _, _, _, Error e, _, _
            | _, _, _, _, _, Error e, _
            | _, _, _, _, _, _, Error e -> Error e
        | Error e, _, _, _, _, _
        | _, Error e, _, _, _, _
        | _, _, Error e, _, _, _
        | _, _, _, Error e, _, _
        | _, _, _, _, Error e, _
        | _, _, _, _, _, Error e -> Error e)
