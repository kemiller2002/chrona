/// One daily attestation (requirements expansion 16) as an Arca record:
/// `records/chrona.attestation/<actor>/<yyyy>/<MM>/<id>.json` inside the
/// organization's folder. Attestations are statements made once: the record
/// is immutable, and a later attestation of the same day is a new record,
/// never a replacement.
///
/// Pure.
module Chrona.Domain.AttestationRecord

open System
open System.Globalization
open Arca
open Chrona.Domain.Diagnostics
open Chrona.Domain.Review
open Chrona.Domain.Codec

/// The record type of an attestation.
let recordType =
    match RecordType.create "chrona.attestation" with
    | Ok found -> found
    | Error text -> invalidOp ("internal: not a record type: " + text)

/// The attestation schema versions this Chrona reads and writes.
let schema =
    { Type = recordType
      OldestReadable = 1
      Current = 1 }

/// The attestation's stable id: its business date and the instant it was made.
let idOf (attestation: Attestation) =
    attestation.LocalDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
    + "-"
    + attestation.At.ToUniversalTime().ToString("HHmmssfffffff", CultureInfo.InvariantCulture)

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

/// The attestation's record key: by actor and the month of the day attested.
let key (attestation: Attestation) : Result<RecordKey, Diagnostic> =
    match RecordId.create (idOf attestation) with
    | Error _ -> Error(UnstorableRecord(idOf attestation, "the attestation id is not a stable record id"))
    | Ok id ->
        segments
            [ ActivityRecord.actorSegment attestation.ActorId
              attestation.LocalDate.Year.ToString("0000")
              attestation.LocalDate.Month.ToString("00") ]
        |> Result.map (fun partition ->
            { Type = recordType
              Partition = partition
              Id = id })

/// The attestation's path inside its organization's folder.
let path (attestation: Attestation) : Result<RelativePath, Diagnostic> =
    key attestation
    |> Result.bind (Layout.recordPath >> Result.mapError (LocationError.describe >> InvalidDataLocation))

/// The folder of one actor's attestations for one month.
let monthFolder (actorId: string) (date: DateOnly) : Result<RelativePath, Diagnostic> =
    segments
        [ Layout.RecordsFolder
          RecordType.value recordType
          ActivityRecord.actorSegment actorId
          date.Year.ToString("0000")
          date.Month.ToString("00") ]
    |> Result.bind (RelativePath.ofSegments >> Result.mapError (LocationError.describe >> InvalidDataLocation))

/// The attestation's record body.
let body (attestation: Attestation) =
    Json.objectOf
        [ "actorId", Json.String attestation.ActorId
          "localDate", Json.String(dateText attestation.LocalDate)
          "statement", Json.String attestation.Statement
          "covered",
          Json.Array(
              attestation.Covered
              |> List.map (fun (id, revision) -> Json.objectOf [ "activityId", Json.String id; "revision", Json.Number(decimal revision) ])
          )
          "at", Json.String(instantText attestation.At) ]

/// The attestation's canonical stored text.
let encode (attestation: Attestation) : Result<string, Diagnostic> =
    key attestation
    |> Result.bind (fun found ->
        { Id = found.Id
          Type = recordType
          SchemaVersion = schema.Current
          Mutability = Mutability.Immutable
          Body = body attestation }
        |> Record.encode Record.DefaultMaxBytes
        |> Result.mapError (fun _ -> UnstorableRecord(found.Id |> RecordId.value, "the attestation record is too large")))

let private coveredOf (value: Json) =
    closed [ "activityId"; "revision" ] value
    |> Result.bind (fun () ->
        match text "activityId" value, integer "revision" value with
        | Ok id, Ok revision when revision >= 1 -> Ok(id, revision)
        | Ok _, Ok _ -> Error "a covered revision must be at least 1"
        | Error e, _
        | _, Error e -> Error e)

/// An attestation from its record body.
let ofBody (value: Json) : Decoded<Attestation> =
    closed [ "actorId"; "at"; "covered"; "localDate"; "statement" ] value
    |> Result.bind (fun () ->
        match text "actorId" value, date "localDate" value, text "statement" value, list "covered" coveredOf value, instant "at" value with
        | Ok actor, Ok localDate, Ok statement, Ok covered, Ok at ->
            Ok
                { ActorId = actor
                  LocalDate = localDate
                  Statement = statement
                  Covered = covered
                  At = at }
        | Error e, _, _, _, _
        | _, Error e, _, _, _
        | _, _, Error e, _, _
        | _, _, _, Error e, _
        | _, _, _, _, Error e -> Error e)
