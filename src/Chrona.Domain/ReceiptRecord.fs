/// One observation's processing receipt (requirements expansion 19,
/// WI-0038) as an Arca record: `records/chrona.receipt/<source>/<id>.json`
/// inside the organization's folder, where the producer finds what became
/// of what it sent. A receipt is written only after the candidate it names
/// is durable (or, for a payload that is not an observation at all, with
/// the reasons), and is never rewritten.
///
/// Pure.
module Chrona.Domain.ReceiptRecord

open Arca
open Chrona.Domain.Diagnostics
open Chrona.Domain.Observations
open Chrona.Domain.Codec

/// The record type of a receipt.
let recordType =
    match RecordType.create "chrona.receipt" with
    | Ok found -> found
    | Error text -> invalidOp ("internal: not a record type: " + text)

/// The receipt schema versions this Chrona reads and writes.
let schema =
    { Type = recordType
      OldestReadable = 1
      Current = 1 }

let private segment (text: string) =
    Segment.create text |> Result.mapError (LocationError.describe >> InvalidDataLocation)

/// The receipt's record key: by source, then observation id.
let keyOf (sourceSystem: string) (observationId: string) : Result<RecordKey, Diagnostic> =
    match RecordId.create observationId, segment sourceSystem with
    | Ok id, Ok source -> Ok { Type = recordType; Partition = [ source ]; Id = id }
    | Error _, _ -> Error(UnstorableRecord(observationId, "the observation id is not a stable record id"))
    | _, Error diagnostic -> Error diagnostic

/// A receipt's path inside its organization's folder.
let pathOf (sourceSystem: string) (observationId: string) : Result<RelativePath, Diagnostic> =
    keyOf sourceSystem observationId
    |> Result.bind (Layout.recordPath >> Result.mapError (LocationError.describe >> InvalidDataLocation))

let path (receipt: Receipt) = pathOf receipt.SourceSystem receipt.ObservationId

/// The receipt's record body.
let body (receipt: Receipt) =
    Json.objectOf
        [ "sourceSystem", Json.String receipt.SourceSystem
          "observationId", Json.String receipt.ObservationId
          "outcome",
          (match receipt.Outcome with
           | CandidateRecorded candidateId -> Json.objectOf [ "kind", Json.String "candidateRecorded"; "candidateId", Json.String candidateId ]
           | InvalidObservation reasons -> Json.objectOf [ "kind", Json.String "invalidObservation"; "reasons", Json.Array(reasons |> List.map Json.String) ])
          "at", Json.String(instantText receipt.At) ]

/// The receipt's canonical stored text.
let encode (receipt: Receipt) : Result<string, Diagnostic> =
    keyOf receipt.SourceSystem receipt.ObservationId
    |> Result.bind (fun found ->
        { Id = found.Id
          Type = recordType
          SchemaVersion = schema.Current
          Mutability = Mutability.Immutable
          Body = body receipt }
        |> Record.encode Record.DefaultMaxBytes
        |> Result.mapError (fun _ -> UnstorableRecord(receipt.ObservationId, "the receipt record is too large")))

/// A receipt from its record body.
let ofBody (value: Json) : Decoded<Receipt> =
    closed [ "at"; "observationId"; "outcome"; "sourceSystem" ] value
    |> Result.bind (fun () ->
        let outcome =
            field "outcome" value
            |> Result.bind (fun o ->
                match text "kind" o with
                | Ok "candidateRecorded" -> closed [ "candidateId"; "kind" ] o |> Result.bind (fun () -> text "candidateId" o) |> Result.map CandidateRecorded
                | Ok "invalidObservation" -> closed [ "kind"; "reasons" ] o |> Result.bind (fun () -> texts "reasons" o) |> Result.map InvalidObservation
                | Ok other -> Error $"'{other}' is not a receipt outcome"
                | Error e -> Error e)

        match text "sourceSystem" value, text "observationId" value, outcome, instant "at" value with
        | Ok source, Ok id, Ok outcome, Ok at -> Ok { SourceSystem = source; ObservationId = id; Outcome = outcome; At = at }
        | Error e, _, _, _
        | _, Error e, _, _
        | _, _, Error e, _
        | _, _, _, Error e -> Error e)

/// The folder of a source's receipts, inside the organization's folder.
let sourceFolder (sourceSystem: string) : Result<RelativePath, Diagnostic> =
    [ Layout.RecordsFolder; RecordType.value recordType; sourceSystem ]
    |> List.fold
        (fun state text ->
            state
            |> Result.bind (fun found -> segment text |> Result.map (fun next -> found @ [ next ])))
        (Ok [])
    |> Result.bind (RelativePath.ofSegments >> Result.mapError (LocationError.describe >> InvalidDataLocation))
