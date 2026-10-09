/// One step of a period's review (requirements expansion 14, WI-0036) as an
/// Arca record: `records/chrona.review/<actor>/<yyyy>/<MM>/<id>.json` inside
/// the organization's folder, filed under the person whose time it is and
/// the month the period starts. Steps are made once: the record is
/// immutable, and a later step is a new record.
///
/// Pure.
module Chrona.Domain.ReviewRecord

open System
open System.Globalization
open Arca
open Chrona.Domain.Diagnostics
open Chrona.Domain.PeriodReview
open Chrona.Domain.Codec

/// The record type of a review step.
let recordType =
    match RecordType.create "chrona.review" with
    | Ok found -> found
    | Error text -> invalidOp ("internal: not a record type: " + text)

/// The review schema versions this Chrona reads and writes.
let schema =
    { Type = recordType
      OldestReadable = 1
      Current = 1 }

/// The step's stable id: its kind, the period's first day and the instant.
let idOf (review: PeriodReview) =
    kindName review.Kind
    + "-"
    + review.Period.Start.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
    + "-"
    + review.At.ToUniversalTime().ToString("yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture)

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

let private monthSegments (actorId: string) (date: DateOnly) =
    [ ActivityRecord.actorSegment actorId; date.Year.ToString("0000"); date.Month.ToString("00") ]

/// The step's record key: by the person and the month the period starts.
let key (review: PeriodReview) : Result<RecordKey, Diagnostic> =
    match RecordId.create (idOf review) with
    | Error _ -> Error(UnstorableRecord(idOf review, "the review id is not a stable record id"))
    | Ok id ->
        segments (monthSegments review.ActorId review.Period.Start)
        |> Result.map (fun partition ->
            { Type = recordType
              Partition = partition
              Id = id })

/// The step's path inside its organization's folder.
let path (review: PeriodReview) : Result<RelativePath, Diagnostic> =
    key review |> Result.bind (Layout.recordPath >> Result.mapError (LocationError.describe >> InvalidDataLocation))

/// The folder of one person's review steps for periods starting in one month.
let monthFolder (actorId: string) (date: DateOnly) : Result<RelativePath, Diagnostic> =
    segments ([ Layout.RecordsFolder; RecordType.value recordType ] @ monthSegments actorId date)
    |> Result.bind (RelativePath.ofSegments >> Result.mapError (LocationError.describe >> InvalidDataLocation))

/// The step's record body.
let body (review: PeriodReview) =
    Json.objectOf
        [ "kind", Json.String(kindName review.Kind)
          "actorId", Json.String review.ActorId
          "periodStart", Json.String(dateText review.Period.Start)
          "periodFinish", Json.String(dateText review.Period.Finish)
          "submissionId", Json.String review.SubmissionId
          "by", Json.String review.By
          "at", Json.String(instantText review.At)
          "covered",
          Json.Array(
              review.Covered
              |> List.map (fun (id, revision) -> Json.objectOf [ "activityId", Json.String id; "revision", Json.Number(decimal revision) ])
          )
          "note",
          match review.Note with
          | Some note -> Json.String note
          | None -> Json.Null ]

/// The step's canonical stored text.
let encode (review: PeriodReview) : Result<string, Diagnostic> =
    key review
    |> Result.bind (fun found ->
        { Id = found.Id
          Type = recordType
          SchemaVersion = schema.Current
          Mutability = Mutability.Immutable
          Body = body review }
        |> Record.encode Record.DefaultMaxBytes
        |> Result.mapError (fun _ -> UnstorableRecord(found.Id |> RecordId.value, "the review record is too large")))

let private coveredOf (value: Json) =
    closed [ "activityId"; "revision" ] value
    |> Result.bind (fun () ->
        match text "activityId" value, integer "revision" value with
        | Ok id, Ok revision when revision >= 1 -> Ok(id, revision)
        | Ok _, Ok _ -> Error "a covered revision must be at least 1"
        | Error e, _
        | _, Error e -> Error e)

/// A review step from its record body. A rejection or reopening without a
/// reason, or a period that ends before it starts, is refused.
let ofBody (value: Json) : Decoded<PeriodReview> =
    closed [ "actorId"; "at"; "by"; "covered"; "kind"; "note"; "periodFinish"; "periodStart"; "submissionId" ] value
    |> Result.bind (fun () ->
        match
            text "kind" value |> Result.bind (fun k -> kindOf k |> Option.map Ok |> Option.defaultValue (Error $"'{k}' is not a review step")),
            text "actorId" value,
            date "periodStart" value,
            date "periodFinish" value,
            text "submissionId" value,
            text "by" value,
            instant "at" value,
            list "covered" coveredOf value
        with
        | Ok kind, Ok actorId, Ok start, Ok finish, Ok submissionId, Ok by, Ok at, Ok covered ->
            optionalText "note" value
            |> Result.bind (fun note ->
                match kind, note with
                | _, _ when finish < start -> Error "the period ends before it starts"
                | (Rejection | Reopening), None -> Error "a rejection or reopening says why"
                | _ ->
                    Ok
                        { Kind = kind
                          ActorId = actorId
                          Period = { Start = start; Finish = finish }
                          SubmissionId = submissionId
                          By = by
                          At = at
                          Covered = covered
                          Note = note })
        | Error e, _, _, _, _, _, _, _
        | _, Error e, _, _, _, _, _, _
        | _, _, Error e, _, _, _, _, _
        | _, _, _, Error e, _, _, _, _
        | _, _, _, _, Error e, _, _, _
        | _, _, _, _, _, Error e, _, _
        | _, _, _, _, _, _, Error e, _
        | _, _, _, _, _, _, _, Error e -> Error e)
