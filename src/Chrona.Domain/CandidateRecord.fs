/// One observation's candidate (requirements expansion 19, WI-0038) as an
/// Arca record inside the organization's folder, mutable under its
/// revision. A candidate a person must still decide (pending, or needing
/// attention) is `records/chrona.candidate/open/<id>.json`, always read;
/// a decided one moves to `records/chrona.candidate/decided/<yyyy>/<MM>/<id>.json`,
/// by the month it was decided, and is read with that month.
///
/// Pure.
module Chrona.Domain.CandidateRecord

open System
open Arca
open Chrona.Domain.Diagnostics
open Chrona.Domain.Observations
open Chrona.Domain.Codec

/// The record type of a candidate.
let recordType =
    match RecordType.create "chrona.candidate" with
    | Ok found -> found
    | Error text -> invalidOp ("internal: not a record type: " + text)

/// The candidate schema versions this Chrona reads and writes.
let schema =
    { Type = recordType
      OldestReadable = 1
      Current = 1 }

/// Whether a person must still decide it.
let isOpen (candidate: Candidate) =
    match candidate.Disposition with
    | Pending
    | NeedsAttention _ -> true
    | _ -> false

/// When it was last decided.
let decidedAt (candidate: Candidate) =
    candidate.Decisions |> List.tryLast |> Option.map _.At |> Option.defaultValue candidate.ReceivedAt

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

let private monthSegments (date: DateOnly) =
    [ "decided"; date.Year.ToString("0000"); date.Month.ToString("00") ]

/// The candidate's record key: open, or decided in a month.
let key (candidate: Candidate) : Result<RecordKey, Diagnostic> =
    match RecordId.create candidate.CandidateId with
    | Error _ -> Error(UnstorableRecord(candidate.CandidateId, "the candidate id is not a stable record id"))
    | Ok id ->
        let partition =
            if isOpen candidate then
                [ "open" ]
            else
                monthSegments (DateOnly.FromDateTime((decidedAt candidate).UtcDateTime))

        segments partition |> Result.map (fun partition -> { Type = recordType; Partition = partition; Id = id })

/// The candidate's path inside its organization's folder.
let path (candidate: Candidate) : Result<RelativePath, Diagnostic> =
    key candidate |> Result.bind (Layout.recordPath >> Result.mapError (LocationError.describe >> InvalidDataLocation))

let private folderOf (partition: string list) =
    segments ([ Layout.RecordsFolder; RecordType.value recordType ] @ partition)
    |> Result.bind (RelativePath.ofSegments >> Result.mapError (LocationError.describe >> InvalidDataLocation))

/// The folder of the candidates a person must still decide.
let openFolder () = folderOf [ "open" ]

/// The folder of the candidates decided in a month.
let decidedFolder (date: DateOnly) = folderOf (monthSegments date)

let private dispositionBody (disposition: Disposition) =
    Json.objectOf
        [ match disposition with
          | Pending -> "kind", Json.String "pending"
          | Accepted ids ->
              "kind", Json.String "accepted"
              "activityIds", Json.Array(ids |> List.map Json.String)
          | AcceptedWithChanges ids ->
              "kind", Json.String "acceptedWithChanges"
              "activityIds", Json.Array(ids |> List.map Json.String)
          | Rejected reason ->
              "kind", Json.String "rejected"
              "reason", Json.String reason
          | Duplicate original ->
              "kind", Json.String "duplicate"
              "candidateId", Json.String original
          | NeedsAttention problems ->
              "kind", Json.String "needsAttention"
              "problems", Json.Array(problems |> List.map (code >> Json.String)) ]

let private optionalString (value: string option) =
    match value with
    | Some text -> Json.String text
    | None -> Json.Null

let private observationBody (o: Observation) =
    Json.objectOf
        [ "observationId", Json.String o.ObservationId
          "sourceSystem", Json.String o.SourceSystem
          "organizationId", Json.String o.OrganizationId
          "projectId", Json.String o.ProjectId
          "actorId", optionalString o.ActorId
          "workItemId", optionalString o.WorkItemId
          "externalUrl", optionalString o.ExternalUrl
          "timing",
          (match o.Timing with
           | ObservedInterval(start, finish) ->
               Json.objectOf [ "kind", Json.String "interval"; "start", Json.String(instantText start); "finish", Json.String(instantText finish) ]
           | ObservedDuration minutes -> Json.objectOf [ "kind", Json.String "duration"; "minutes", Json.Number(decimal minutes) ])
          "description", optionalString o.Description
          "evidence", Json.Array(o.Evidence |> List.map (fun (kind, reference) -> Json.objectOf [ "kind", Json.String kind; "reference", Json.String reference ]))
          "observedAt", Json.String(instantText o.ObservedAt) ]

/// The candidate's record body.
let body (candidate: Candidate) =
    Json.objectOf
        [ "candidateId", Json.String candidate.CandidateId
          "observation", observationBody candidate.Observation
          "receivedAt", Json.String(instantText candidate.ReceivedAt)
          "disposition", dispositionBody candidate.Disposition
          "revision", Json.Number(decimal candidate.Revision)
          "decisions",
          Json.Array(
              candidate.Decisions
              |> List.map (fun d -> Json.objectOf [ "by", Json.String d.By; "at", Json.String(instantText d.At); "disposition", dispositionBody d.Disposition ])
          ) ]

/// The candidate's canonical stored text.
let encode (candidate: Candidate) : Result<string, Diagnostic> =
    key candidate
    |> Result.bind (fun found ->
        { Id = found.Id
          Type = recordType
          SchemaVersion = schema.Current
          Mutability = Mutability.Mutable
          Body = body candidate }
        |> Record.encode Record.DefaultMaxBytes
        |> Result.mapError (fun _ -> UnstorableRecord(candidate.CandidateId, "the candidate record is too large")))

let private all (results: Decoded<'a> list) =
    match results |> List.choose (function Error e -> Some e | Ok _ -> None) with
    | [] -> Ok(results |> List.choose (function Ok v -> Some v | Error _ -> None))
    | e :: _ -> Error e

let private dispositionOf (value: Json) : Decoded<Disposition> =
    match text "kind" value with
    | Ok "pending" -> closed [ "kind" ] value |> Result.map (fun () -> Pending)
    | Ok "accepted" -> closed [ "activityIds"; "kind" ] value |> Result.bind (fun () -> texts "activityIds" value) |> Result.map Accepted
    | Ok "acceptedWithChanges" ->
        closed [ "activityIds"; "kind" ] value |> Result.bind (fun () -> texts "activityIds" value) |> Result.map AcceptedWithChanges
    | Ok "rejected" -> closed [ "kind"; "reason" ] value |> Result.bind (fun () -> text "reason" value) |> Result.map Rejected
    | Ok "duplicate" -> closed [ "candidateId"; "kind" ] value |> Result.bind (fun () -> text "candidateId" value) |> Result.map Duplicate
    | Ok "needsAttention" ->
        closed [ "kind"; "problems" ] value
        |> Result.bind (fun () -> texts "problems" value)
        |> Result.map (List.map RecordedProblem >> NeedsAttention)
    | Ok other -> Error $"'{other}' is not a disposition"
    | Error e -> Error e

let private observationOf (value: Json) : Decoded<Observation> =
    closed [ "actorId"; "description"; "evidence"; "externalUrl"; "observationId"; "observedAt"; "organizationId"; "projectId"; "sourceSystem"; "timing"; "workItemId" ] value
    |> Result.bind (fun () ->
        let timing =
            field "timing" value
            |> Result.bind (fun t ->
                match text "kind" t with
                | Ok "interval" ->
                    closed [ "finish"; "kind"; "start" ] t
                    |> Result.bind (fun () ->
                        match instant "start" t, instant "finish" t with
                        | Ok s, Ok f -> Ok(ObservedInterval(s, f))
                        | Error e, _
                        | _, Error e -> Error e)
                | Ok "duration" -> closed [ "kind"; "minutes" ] t |> Result.bind (fun () -> integer "minutes" t) |> Result.map ObservedDuration
                | Ok other -> Error $"'{other}' is not a timing"
                | Error e -> Error e)

        let evidence =
            list
                "evidence"
                (fun item ->
                    closed [ "kind"; "reference" ] item
                    |> Result.bind (fun () ->
                        match text "kind" item, text "reference" item with
                        | Ok k, Ok r -> Ok(k, r)
                        | Error e, _
                        | _, Error e -> Error e))
                value

        match text "observationId" value, text "sourceSystem" value, text "organizationId" value, text "projectId" value with
        | Ok observationId, Ok sourceSystem, Ok organizationId, Ok projectId ->
            match
                optionalText "actorId" value,
                optionalText "workItemId" value,
                optionalText "externalUrl" value,
                timing,
                optionalText "description" value,
                evidence,
                instant "observedAt" value
            with
            | Ok actorId, Ok workItemId, Ok externalUrl, Ok timing, Ok description, Ok evidence, Ok observedAt ->
                Ok
                    { ObservationId = observationId
                      SourceSystem = sourceSystem
                      OrganizationId = organizationId
                      ProjectId = projectId
                      ActorId = actorId
                      WorkItemId = workItemId
                      ExternalUrl = externalUrl
                      Timing = timing
                      Description = description
                      Evidence = evidence
                      ObservedAt = observedAt }
            | Error e, _, _, _, _, _, _
            | _, Error e, _, _, _, _, _
            | _, _, Error e, _, _, _, _
            | _, _, _, Error e, _, _, _
            | _, _, _, _, Error e, _, _
            | _, _, _, _, _, Error e, _
            | _, _, _, _, _, _, Error e -> Error e
        | Error e, _, _, _
        | _, Error e, _, _
        | _, _, Error e, _
        | _, _, _, Error e -> Error e)

/// A candidate from its record body.
let ofBody (value: Json) : Decoded<Candidate> =
    closed [ "candidateId"; "decisions"; "disposition"; "observation"; "receivedAt"; "revision" ] value
    |> Result.bind (fun () ->
        let decisions =
            list
                "decisions"
                (fun d ->
                    closed [ "at"; "by"; "disposition" ] d
                    |> Result.bind (fun () ->
                        match text "by" d, instant "at" d, field "disposition" d |> Result.bind dispositionOf with
                        | Ok by, Ok at, Ok disposition -> Ok { By = by; At = at; Disposition = disposition }
                        | Error e, _, _
                        | _, Error e, _
                        | _, _, Error e -> Error e))
                value

        match
            text "candidateId" value,
            field "observation" value |> Result.bind observationOf,
            instant "receivedAt" value,
            field "disposition" value |> Result.bind dispositionOf,
            integer "revision" value,
            decisions
        with
        | Ok _, Ok _, Ok _, Ok _, Ok revision, Ok _ when revision < 1 -> Error "'revision' must be at least 1"
        | Ok _, Ok _, Ok _, Ok _, Ok _, Ok [] -> Error "a candidate keeps at least the decision it was received with"
        | Ok id, Ok observation, Ok receivedAt, Ok disposition, Ok revision, Ok decisions ->
            Ok
                { CandidateId = id
                  Observation = observation
                  ReceivedAt = receivedAt
                  Disposition = disposition
                  Revision = revision
                  Decisions = decisions }
        | Error e, _, _, _, _, _
        | _, Error e, _, _, _, _
        | _, _, Error e, _, _, _
        | _, _, _, Error e, _, _
        | _, _, _, _, Error e, _
        | _, _, _, _, _, Error e -> Error e)
