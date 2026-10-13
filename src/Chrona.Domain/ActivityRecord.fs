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
///
/// Reading a record back into an activity is `ActivityRecordReading`.
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

let internal keepsAsIs (index: int) (c: char) =
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

/// The actor id a segment was made from (`actorSegment`'s inverse), or None
/// when the text is not one `actorSegment` makes.
let actorOfSegment (segment: string) : string option =
    let rec bytesFrom (index: int) (bytes: byte list) =
        if index >= segment.Length then
            Some(List.rev bytes)
        elif segment[index] = '_' && index + 2 < segment.Length then
            match Byte.TryParse(segment.Substring(index + 1, 2), Globalization.NumberStyles.HexNumber, Globalization.CultureInfo.InvariantCulture) with
            | true, value -> bytesFrom (index + 3) (value :: bytes)
            | _ -> None
        elif segment[index] = '_' then
            None
        else
            bytesFrom (index + 1) (List.rev (List.ofArray (Encoding.UTF8.GetBytes(string segment[index]))) @ bytes)

    bytesFrom 0 []
    |> Option.map (Array.ofList >> Encoding.UTF8.GetString)
    |> Option.filter (fun actorId -> actorSegment actorId = segment)

let internal segment (text: string) : Result<Segment, Diagnostic> =
    Segment.create text |> Result.mapError (LocationError.describe >> InvalidDataLocation)

let internal segments (texts: string list) =
    texts
    |> List.fold
        (fun state text -> state |> Result.bind (fun found -> segment text |> Result.map (fun next -> found @ [ next ])))
        (Ok [])

let internal monthParts (actorId: string) (year: int) (month: int) =
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

let internal timingJson =
    function
    | Interval(start, finish) ->
        Json.objectOf [ "kind", Json.String "interval"; "start", Json.String(instantText start); "finish", Json.String(instantText finish) ]
    | DurationOnDate minutes -> Json.objectOf [ "kind", Json.String "duration"; "minutes", Json.Number(decimal minutes) ]

let internal entryJson =
    function
    | Manual -> Json.objectOf [ "kind", Json.String "manual"; "sourceSystem", Json.Null ]
    | Timer -> Json.objectOf [ "kind", Json.String "timer"; "sourceSystem", Json.Null ]
    | Imported system -> Json.objectOf [ "kind", Json.String "imported"; "sourceSystem", Json.String system ]

let internal recordJson =
    function
    | Recorded -> Json.objectOf [ "state", Json.String "recorded"; "reason", Json.Null; "by", Json.Array [] ]
    | Voided reason -> Json.objectOf [ "state", Json.String "voided"; "reason", Json.String reason; "by", Json.Array [] ]
    | Superseded by ->
        Json.objectOf [ "state", Json.String "superseded"; "reason", Json.Null; "by", Json.Array(by |> List.map Json.String) ]

let internal reviewJson =
    let plain (name: string) = Json.objectOf [ "state", Json.String name; "reason", Json.Null ]

    function
    | Unsubmitted -> plain "unsubmitted"
    | Submitted -> plain "submitted"
    | Approved -> plain "approved"
    | Reopened -> plain "reopened"
    | Rejected reason -> Json.objectOf [ "state", Json.String "rejected"; "reason", Json.String reason ]

let internal publicationName =
    function
    | NotBillable -> "notBillable"
    | Unpublished -> "unpublished"
    | ReadyForPublication -> "readyForPublication"
    | Published -> "published"
    | InvoicedExternally -> "invoicedExternally"
    | AdjustmentRequired -> "adjustmentRequired"

let internal billabilityName =
    function
    | Billable -> "billable"
    | NonBillable -> "nonBillable"
    | PendingClassification -> "pendingClassification"

let internal evidenceJson (evidence: Evidence) =
    Json.objectOf
        [ "id", Json.String evidence.Id
          "url", Json.String evidence.Url
          "kind", Json.String evidence.Kind
          "label", Json.String evidence.Label
          "capturedAt", Json.String(instantText evidence.CapturedAt)
          "hash", textOrNull evidence.Hash
          "source", textOrNull evidence.Source
          "notes", textOrNull evidence.Notes ]

let internal sourceJson (source: ObservationSource) =
    Json.objectOf
        [ "sourceSystem", Json.String source.SourceSystem
          "observationId", Json.String source.ObservationId
          "externalUrl", textOrNull source.ExternalUrl
          "ingestedAt", Json.String(instantText source.IngestedAt) ]

/// A classification as stored.
let classificationJson (classification: Classification) =
    Json.objectOf
        [ "projectId", Json.String classification.ProjectId
          "clientId", textOrNull classification.ClientId
          "engagementId", textOrNull classification.EngagementId
          "activityTypeId", Json.String classification.ActivityTypeId
          "tags", Json.Array(classification.Tags |> List.map Json.String)
          "description", Json.String classification.Description
          "businessPurpose", Json.String classification.BusinessPurpose ]

/// A stored classification, from the object holding it as `classification`.
let classificationOf value =
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
          "classification", classificationJson classification
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
