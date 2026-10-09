/// The organization's timesheet period configuration (requirements
/// expansion 15, WI-0036) as an Arca record:
/// `records/chrona.configuration/periods.json` inside the organization's
/// folder, mutable under its revision. Absent, the organization's periods
/// are weekly from its manifest's week start, with its manifest's
/// submission and approval policy. The time zone is the organization's, so
/// it is not stored here.
///
/// Pure.
module Chrona.Domain.PeriodConfigRecord

open System
open Arca
open Chrona.Domain.Diagnostics
open Chrona.Domain.Periods
open Chrona.Domain.Codec

/// The organization's period configuration, as stored.
type StoredPeriods =
    { Config: PeriodConfig
      /// Counted from 1, raised with each change.
      Revision: int
      ChangedBy: string
      ChangedAt: DateTimeOffset }

/// The record type of organization configuration.
let recordType = Organization.configurationType

/// The period configuration's schema versions this Chrona reads and writes.
let schema =
    { Type = recordType
      OldestReadable = 1
      Current = 1 }

/// The record's id.
[<Literal>]
let Id = "periods"

let key () : Result<RecordKey, Diagnostic> =
    match RecordId.create Id with
    | Ok id ->
        Ok
            { Type = recordType
              Partition = []
              Id = id }
    | Error _ -> Error(UnstorableRecord(Id, "not a record id"))

/// The record's path inside its organization's folder.
let path () : Result<RelativePath, Diagnostic> =
    key () |> Result.bind (Layout.recordPath >> Result.mapError (LocationError.describe >> InvalidDataLocation))

/// The folder of the organization's configuration.
let folder () : Result<RelativePath, Diagnostic> =
    RelativePath.parse (String.concat "/" [ Layout.RecordsFolder; RecordType.value recordType ])
    |> Result.mapError (LocationError.describe >> InvalidDataLocation)

let private cadenceName =
    function
    | Daily -> "daily"
    | Weekly -> "weekly"
    | Biweekly _ -> "biweekly"
    | SemiMonthly -> "semiMonthly"
    | Monthly -> "monthly"

/// The record body.
let body (stored: StoredPeriods) =
    Json.objectOf
        [ "cadence", Json.String(cadenceName stored.Config.Cadence)
          match stored.Config.Cadence with
          | Biweekly anchor -> "anchor", Json.String(dateText anchor)
          | _ -> ()
          "weekStart", Json.String(stored.Config.WeekStart.ToString())
          "submissionExpected", Json.Bool stored.Config.SubmissionExpected
          "approvalRequired", Json.Bool stored.Config.ApprovalRequired
          "revision", Json.Number(decimal stored.Revision)
          "changedBy", Json.String stored.ChangedBy
          "changedAt", Json.String(instantText stored.ChangedAt) ]

/// The record's canonical stored text.
let encode (stored: StoredPeriods) : Result<string, Diagnostic> =
    key ()
    |> Result.bind (fun found ->
        { Id = found.Id
          Type = recordType
          SchemaVersion = schema.Current
          Mutability = Mutability.Mutable
          Body = body stored }
        |> Record.encode Record.DefaultMaxBytes
        |> Result.mapError (fun _ -> UnstorableRecord(Id, "the period configuration record is too large")))

let private weekStartOf (text: string) =
    match Enum.TryParse<DayOfWeek>(text, false) with
    | true, day when Enum.IsDefined day && day.ToString() = text -> Ok day
    | _ -> Error $"'{text}' is not a day of the week"

/// The configuration from its record body, reckoned in `zoneId` (the
/// organization's time zone).
let ofBody (zoneId: string) (value: Json) : Decoded<StoredPeriods> =
    closed [ "anchor"; "approvalRequired"; "cadence"; "changedAt"; "changedBy"; "revision"; "submissionExpected"; "weekStart" ] value
    |> Result.bind (fun () ->
        let cadence =
            match text "cadence" value with
            | Ok "daily" -> Ok Daily
            | Ok "weekly" -> Ok Weekly
            | Ok "biweekly" -> date "anchor" value |> Result.map Biweekly
            | Ok "semiMonthly" -> Ok SemiMonthly
            | Ok "monthly" -> Ok Monthly
            | Ok other -> Error $"'{other}' is not a period cadence"
            | Error e -> Error e

        let anchorOnlyForBiweekly =
            match cadence, field "anchor" value with
            | Ok(Biweekly _), _ -> Ok()
            | _, Ok _ -> Error "'anchor' is only for a biweekly cadence"
            | _ -> Ok()

        match
            cadence,
            anchorOnlyForBiweekly,
            text "weekStart" value |> Result.bind weekStartOf,
            flag "submissionExpected" value,
            flag "approvalRequired" value,
            integer "revision" value,
            text "changedBy" value,
            instant "changedAt" value
        with
        | Ok _, Ok(), Ok _, Ok _, Ok _, Ok revision, Ok _, Ok _ when revision < 1 -> Error "'revision' must be at least 1"
        | Ok cadence, Ok(), Ok weekStart, Ok submission, Ok approval, Ok revision, Ok changedBy, Ok changedAt ->
            Ok
                { Config =
                    { Cadence = cadence
                      WeekStart = weekStart
                      ZoneId = zoneId
                      SubmissionExpected = submission
                      ApprovalRequired = approval }
                  Revision = revision
                  ChangedBy = changedBy
                  ChangedAt = changedAt }
        | Error e, _, _, _, _, _, _, _
        | _, Error e, _, _, _, _, _, _
        | _, _, Error e, _, _, _, _, _
        | _, _, _, Error e, _, _, _, _
        | _, _, _, _, Error e, _, _, _
        | _, _, _, _, _, Error e, _, _
        | _, _, _, _, _, _, Error e, _
        | _, _, _, _, _, _, _, Error e -> Error e)
