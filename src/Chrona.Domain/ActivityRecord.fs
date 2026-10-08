/// One activity as an Arca record (requirements expansion 5, 22): its
/// deterministic, partitioned path and its closed, versioned body.
///
/// Inside an organization's folder an activity lives at
/// `records/chrona.activity/<actor>/<yyyy>/<MM>/<ActivityId>.json`, by its
/// actor and the month of its business date. A month folder is a bounded
/// read: the today view reads one, never the whole history (38). The id is
/// stable and stored inside the record, so a record found under another
/// name is detected.
///
/// Pure. The body holds every field of the authoritative record, in the
/// record's own words; decoding refuses anything this version does not define.
module Chrona.Domain.ActivityRecord

open System
open System.Text
open Arca
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Domain.Codec

/// The record type of an activity.
let recordType =
    match RecordType.create "chrona.activity" with
    | Ok found -> found
    | Error text -> invalidOp ("internal: not a record type: " + text)

/// The activity schema versions this Chrona reads and writes.
let schema =
    { Type = recordType
      OldestReadable = 1
      Current = 1 }

// ---- Layout -------------------------------------------------------------------

let private keepsAsIs (index: int) (c: char) =
    Char.IsAsciiLetterOrDigit c || (index > 0 && (c = '.' || c = '-'))

/// The path segment for an actor id. Actor ids may hold characters a path
/// segment cannot (`github:octocat`); letters and digits stay, and so do `.`
/// and `-` after the first character. Every other character, `_` included,
/// becomes `_` and the lower-case hex of its UTF-8 bytes, so the segment is
/// deterministic and distinct actors never share one.
let actorSegment (actorId: string) =
    actorId
    |> Seq.mapi (fun index c ->
        if keepsAsIs index c then
            string c
        else
            Encoding.UTF8.GetBytes(string c) |> Array.map (fun b -> $"_{b:x2}") |> String.concat "")
    |> String.concat ""

let private segment (text: string) : Result<Segment, Diagnostic> =
    Segment.create text |> Result.mapError (LocationError.describe >> InvalidDataLocation)

let private segments (texts: string list) =
    texts
    |> List.fold
        (fun state text -> state |> Result.bind (fun found -> segment text |> Result.map (fun next -> found @ [ next ])))
        (Ok [])

let private monthParts (actorId: string) (year: int) (month: int) =
    [ actorSegment actorId; year.ToString("0000"); month.ToString("00") ]

/// The partition an activity belongs in: its actor, and the year and month
/// of its business date.
let partition (activity: Activity) =
    let date = activity.Occurrence.LocalDate
    segments (monthParts activity.ActorId date.Year date.Month)

/// The activity's record key.
let key (activity: Activity) : Result<RecordKey, Diagnostic> =
    match RecordId.create activity.ActivityId with
    | Error _ -> Error(UnstorableActivity(activity.ActivityId, "the id is not a stable record id"))
    | Ok id ->
        partition activity
        |> Result.map (fun parts ->
            { Type = recordType
              Partition = parts
              Id = id })

/// The activity's path inside its organization's folder.
let path (activity: Activity) : Result<RelativePath, Diagnostic> =
    key activity
    |> Result.bind (Layout.recordPath >> Result.mapError (LocationError.describe >> InvalidDataLocation))

/// The folder holding one actor's activities for one month, inside the
/// organization's folder: what the today and month views list and read.
let monthFolder (actorId: string) (date: DateOnly) : Result<RelativePath, Diagnostic> =
    segments ([ Layout.RecordsFolder; RecordType.value recordType ] @ monthParts actorId date.Year date.Month)
    |> Result.bind (RelativePath.ofSegments >> Result.mapError (LocationError.describe >> InvalidDataLocation))

/// The distinct month folders that hold an actor's activities on these dates.
let monthFolders (actorId: string) (dates: DateOnly list) =
    dates
    |> List.distinctBy (fun date -> date.Year, date.Month)
    |> List.sort
    |> List.fold
        (fun state date -> state |> Result.bind (fun found -> monthFolder actorId date |> Result.map (fun folder -> found @ [ folder ])))
        (Ok [])

// ---- Body ---------------------------------------------------------------------

let private timingJson =
    function
    | Interval(start, finish) ->
        Json.objectOf [ "kind", Json.String "interval"; "start", Json.String(instantText start); "finish", Json.String(instantText finish) ]
    | DurationOnDate minutes -> Json.objectOf [ "kind", Json.String "duration"; "minutes", Json.Number(decimal minutes) ]

let private entryJson =
    function
    | Manual -> Json.objectOf [ "kind", Json.String "manual"; "sourceSystem", Json.Null ]
    | Timer -> Json.objectOf [ "kind", Json.String "timer"; "sourceSystem", Json.Null ]
    | Imported system -> Json.objectOf [ "kind", Json.String "imported"; "sourceSystem", Json.String system ]

let private recordJson =
    function
    | Recorded -> Json.objectOf [ "state", Json.String "recorded"; "reason", Json.Null; "by", Json.Array [] ]
    | Voided reason -> Json.objectOf [ "state", Json.String "voided"; "reason", Json.String reason; "by", Json.Array [] ]
    | Superseded by ->
        Json.objectOf [ "state", Json.String "superseded"; "reason", Json.Null; "by", Json.Array(by |> List.map Json.String) ]

let private reviewJson =
    let plain (name: string) = Json.objectOf [ "state", Json.String name; "reason", Json.Null ]

    function
    | Unsubmitted -> plain "unsubmitted"
    | Submitted -> plain "submitted"
    | Approved -> plain "approved"
    | Reopened -> plain "reopened"
    | Rejected reason -> Json.objectOf [ "state", Json.String "rejected"; "reason", Json.String reason ]

let private publicationName =
    function
    | NotBillable -> "notBillable"
    | Unpublished -> "unpublished"
    | ReadyForPublication -> "readyForPublication"
    | Published -> "published"
    | InvoicedExternally -> "invoicedExternally"
    | AdjustmentRequired -> "adjustmentRequired"

let private billabilityName =
    function
    | Billable -> "billable"
    | NonBillable -> "nonBillable"
    | PendingClassification -> "pendingClassification"

let private evidenceJson (evidence: Evidence) =
    Json.objectOf
        [ "id", Json.String evidence.Id
          "url", Json.String evidence.Url
          "kind", Json.String evidence.Kind
          "label", Json.String evidence.Label
          "capturedAt", Json.String(instantText evidence.CapturedAt)
          "hash", textOrNull evidence.Hash ]

let private sourceJson (source: ObservationSource) =
    Json.objectOf
        [ "sourceSystem", Json.String source.SourceSystem
          "observationId", Json.String source.ObservationId
          "externalUrl", textOrNull source.ExternalUrl
          "ingestedAt", Json.String(instantText source.IngestedAt) ]

/// The activity's record body.
let body (activity: Activity) =
    let occurrence = activity.Occurrence
    let classification = activity.Classification
    let billing = activity.BillingReference

    Json.objectOf
        [ "activityId", Json.String activity.ActivityId
          "organizationId", Json.String activity.OrganizationId
          "actorId", Json.String activity.ActorId
          "occurrence",
          Json.objectOf
              [ "zone", Json.String occurrence.Zone
                "offsetMinutes", Json.Number(decimal occurrence.OffsetMinutes)
                "localDate", Json.String(dateText occurrence.LocalDate)
                "localTime", Json.String(timeText occurrence.LocalTime) ]
          "timing", timingJson activity.Timing
          "minutes", Json.Number(decimal activity.Minutes)
          "classification",
          Json.objectOf
              [ "projectId", Json.String classification.ProjectId
                "clientId", textOrNull classification.ClientId
                "engagementId", textOrNull classification.EngagementId
                "activityTypeId", Json.String classification.ActivityTypeId
                "tags", Json.Array(classification.Tags |> List.map Json.String)
                "description", Json.String classification.Description
                "businessPurpose", Json.String classification.BusinessPurpose ]
          "entryMethod", entryJson activity.EntryMethod
          "billability", Json.String(billabilityName activity.Billability)
          "billingReference",
          Json.objectOf
              [ "rateReference", textOrNull billing.RateReference
                "billingClass", textOrNull billing.BillingClass
                "contractReference", textOrNull billing.ContractReference ]
          "record", recordJson activity.Record
          "review", reviewJson activity.Review
          "publication", Json.String(publicationName activity.Publication)
          "revision", Json.Number(decimal activity.Revision)
          "createdAt", Json.String(instantText activity.CreatedAt)
          "lastChangedAt", Json.String(instantText activity.LastChangedAt)
          "reason", textOrNull activity.Reason
          "workItemRef", textOrNull activity.WorkItemRef
          "externalRef", textOrNull activity.ExternalRef
          "evidence", Json.Array(activity.Evidence |> List.map evidenceJson)
          "lineage", Json.Array(activity.Lineage |> List.map Json.String)
          "source",
          (match activity.Source with
           | Some source -> sourceJson source
           | None -> Json.Null) ]

/// The activity as a mutable Arca record: every transition rewrites it under
/// its revision.
let toRecord (activity: Activity) : Result<Record, Diagnostic> =
    key activity
    |> Result.map (fun found ->
        { Id = found.Id
          Type = recordType
          SchemaVersion = schema.Current
          Mutability = Mutability.Mutable
          Body = body activity })

/// The activity's canonical stored text.
let encode (activity: Activity) : Result<string, Diagnostic> =
    toRecord activity
    |> Result.bind (fun record ->
        Record.encode Record.DefaultMaxBytes record
        |> Result.mapError (fun error ->
            UnstorableActivity(
                activity.ActivityId,
                match error with
                | EncodeError.TooLarge(bytes, limit) -> $"the record is {bytes} bytes, over the {limit}-byte limit"
                | EncodeError.InvalidSchemaVersion version -> $"schema version {version} is not valid"
            )))

// ---- Decoding -----------------------------------------------------------------

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

let private classificationOf value =
    field "classification" value
    |> Result.bind (fun c ->
        closed [ "activityTypeId"; "businessPurpose"; "clientId"; "description"; "engagementId"; "projectId"; "tags" ] c
        |> Result.bind (fun () ->
            match
                text "projectId" c,
                optionalText "clientId" c,
                optionalText "engagementId" c,
                text "activityTypeId" c,
                texts "tags" c,
                text "description" c,
                text "businessPurpose" c
            with
            | Ok project, Ok client, Ok engagement, Ok activityType, Ok tags, Ok description, Ok purpose ->
                Ok
                    { ProjectId = project
                      ClientId = client
                      EngagementId = engagement
                      ActivityTypeId = activityType
                      Tags = tags
                      Description = description
                      BusinessPurpose = purpose }
            | Error e, _, _, _, _, _, _
            | _, Error e, _, _, _, _, _
            | _, _, Error e, _, _, _, _
            | _, _, _, Error e, _, _, _
            | _, _, _, _, Error e, _, _
            | _, _, _, _, _, Error e, _
            | _, _, _, _, _, _, Error e -> Error e))

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
    closed [ "capturedAt"; "hash"; "id"; "kind"; "label"; "url" ] value
    |> Result.bind (fun () ->
        match text "id" value, text "url" value, text "kind" value, text "label" value, instant "capturedAt" value, optionalText "hash" value with
        | Ok id, Ok url, Ok kind, Ok label, Ok captured, Ok hash ->
            Ok
                { Id = id
                  Url = url
                  Kind = kind
                  Label = label
                  CapturedAt = captured
                  Hash = hash }
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
