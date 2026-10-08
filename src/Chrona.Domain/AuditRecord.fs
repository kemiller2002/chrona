/// One audit entry (requirements expansion 25) as an Arca record (WI-0056):
/// `records/chrona.audit/<actor>/<yyyy>/<MM>/<id>.json` inside the
/// organization's folder, partitioned like the activities it audits (the
/// person whose time it is, and the month of its business date), so it is
/// read with them. An audit entry is a fact stated once: the record is
/// immutable, written in the same commit as the records it audits, and
/// never replaced. Git history stays supporting evidence (40); this is the
/// domain's own account of who changed what.
///
/// Pure.
module Chrona.Domain.AuditRecord

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open Arca
open Chrona.Domain.Diagnostics
open Chrona.Domain.Ledger
open Chrona.Domain.Codec

/// An audit entry and where it is kept: the person whose activities it
/// audits, and their business date.
type Audited =
    { Entry: AuditEntry
      Owner: string
      LocalDate: DateOnly }

/// The record type of an audit entry.
let recordType =
    match RecordType.create "chrona.audit" with
    | Ok found -> found
    | Error text -> invalidOp ("internal: not a record type: " + text)

/// The audit schema versions this Chrona reads and writes.
let schema =
    { Type = recordType
      OldestReadable = 1
      Current = 1 }

let private revisionsJson (revisions: (string * int) list) =
    Json.Array(revisions |> List.map (fun (id, revision) -> Json.objectOf [ "activityId", Json.String id; "revision", Json.Number(decimal revision) ]))

/// The record body.
let body (audited: Audited) =
    let entry = audited.Entry

    Json.objectOf
        [ "owner", Json.String audited.Owner
          "localDate", Json.String(dateText audited.LocalDate)
          "performer", Json.String entry.Performer
          "at", Json.String(instantText entry.At)
          "source", Json.String entry.Source
          "command", Json.String entry.Command
          "activityIds", Json.Array(entry.ActivityIds |> List.map Json.String)
          "priorRevisions", revisionsJson entry.PriorRevisions
          "resultingRevisions", revisionsJson entry.ResultingRevisions
          "reason", textOrNull entry.Reason
          "correlationId", textOrNull entry.CorrelationId ]

/// The entry's stable id: when it was made, and a digest of what it says,
/// so the same fact always has the same id and two facts never share one.
let idOf (audited: Audited) =
    let digest =
        SHA256.HashData(Encoding.UTF8.GetBytes(Json.canonicalText (body audited)))
        |> Convert.ToHexString
        |> fun hex -> hex.Substring(0, 16).ToLowerInvariant()

    audited.Entry.At.ToUniversalTime().ToString("yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture) + "-" + digest

let private segments (texts: string list) =
    texts
    |> List.fold
        (fun state text ->
            state
            |> Result.bind (fun found ->
                Segment.create text
                |> Result.mapError (LocationError.describe >> InvalidDataLocation)
                |> Result.map (fun next -> found @ [ next ])))
        (Ok [])

/// The entry's record key.
let key (audited: Audited) : Result<RecordKey, Diagnostic> =
    match RecordId.create (idOf audited) with
    | Error _ -> Error(UnstorableRecord(idOf audited, "the audit id is not a stable record id"))
    | Ok id ->
        segments
            [ ActivityRecord.actorSegment audited.Owner
              audited.LocalDate.Year.ToString("0000")
              audited.LocalDate.Month.ToString("00") ]
        |> Result.map (fun partition ->
            { Type = recordType
              Partition = partition
              Id = id })

/// The entry's path inside its organization's folder.
let path (audited: Audited) : Result<RelativePath, Diagnostic> =
    key audited
    |> Result.bind (Layout.recordPath >> Result.mapError (LocationError.describe >> InvalidDataLocation))

/// The folder of one person's audit entries for one month.
let monthFolder (actorId: string) (date: DateOnly) : Result<RelativePath, Diagnostic> =
    segments
        [ Layout.RecordsFolder
          RecordType.value recordType
          ActivityRecord.actorSegment actorId
          date.Year.ToString("0000")
          date.Month.ToString("00") ]
    |> Result.bind (RelativePath.ofSegments >> Result.mapError (LocationError.describe >> InvalidDataLocation))

/// The entry's canonical stored text.
let encode (audited: Audited) : Result<string, Diagnostic> =
    key audited
    |> Result.bind (fun found ->
        { Id = found.Id
          Type = recordType
          SchemaVersion = schema.Current
          Mutability = Mutability.Immutable
          Body = body audited }
        |> Record.encode Record.DefaultMaxBytes
        |> Result.mapError (fun _ -> UnstorableRecord(RecordId.value found.Id, "the audit record is too large")))

let private revisionOf (value: Json) =
    closed [ "activityId"; "revision" ] value
    |> Result.bind (fun () ->
        match text "activityId" value, integer "revision" value with
        | Ok id, Ok revision when revision >= 1 -> Ok(id, revision)
        | Ok _, Ok _ -> Error "a revision must be at least 1"
        | Error e, _
        | _, Error e -> Error e)

/// An audit entry from its record body.
let ofBody (value: Json) : Decoded<Audited> =
    closed
        [ "activityIds"; "at"; "command"; "correlationId"; "localDate"; "owner"; "performer"; "priorRevisions"; "reason"; "resultingRevisions"; "source" ]
        value
    |> Result.bind (fun () ->
        match
            text "owner" value,
            date "localDate" value,
            text "performer" value,
            instant "at" value,
            text "source" value,
            text "command" value,
            texts "activityIds" value
        with
        | Ok owner, Ok localDate, Ok performer, Ok at, Ok source, Ok command, Ok ids ->
            match
                list "priorRevisions" revisionOf value,
                list "resultingRevisions" revisionOf value,
                optionalText "reason" value,
                optionalText "correlationId" value
            with
            | Ok prior, Ok resulting, Ok reason, Ok correlation ->
                Ok
                    { Owner = owner
                      LocalDate = localDate
                      Entry =
                        { Performer = performer
                          At = at
                          Source = source
                          Command = command
                          ActivityIds = ids
                          PriorRevisions = prior
                          ResultingRevisions = resulting
                          Reason = reason
                          CorrelationId = correlation } }
            | Error e, _, _, _
            | _, Error e, _, _
            | _, _, Error e, _
            | _, _, _, Error e -> Error e
        | Error e, _, _, _, _, _, _
        | _, Error e, _, _, _, _, _
        | _, _, Error e, _, _, _, _
        | _, _, _, Error e, _, _, _
        | _, _, _, _, Error e, _, _
        | _, _, _, _, _, Error e, _
        | _, _, _, _, _, _, Error e -> Error e)

/// Where each new audit entry is kept: with the first of its activities
/// that `activities` holds. An entry about none of them is not placed.
let place (activities: Activity.Activity list) (entries: AuditEntry list) : Audited list =
    entries
    |> List.choose (fun entry ->
        entry.ActivityIds
        |> List.tryPick (fun id -> activities |> List.tryFind (fun a -> a.ActivityId = id))
        |> Option.map (fun activity ->
            { Entry = entry
              Owner = activity.ActorId
              LocalDate = activity.Occurrence.LocalDate }))
