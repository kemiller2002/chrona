/// The device's own unfinished timer, kept in this browser so it survives a
/// refresh, a restart or a lost connection (requirements expansion 10.1,
/// 10.2; WI-0055).
///
/// It is not a record and never an authority: it holds this person's active
/// or stopped-but-not-completed timer on this device, nothing else, under a
/// key of their own, and becomes activities only through the usual
/// completion and its rules. Elapsed time is always derived from its
/// segments' timestamps.
///
/// Pure.
module Chrona.Domain.TimerRecord

open Arca
open Chrona.Domain.Timer
open Chrona.Domain.Codec

/// What the device keeps.
type Kept =
    | KeptRunning of ActiveTimer
    | KeptPaused of ActiveTimer
    /// Stopped, its time held until it is completed (every segment closed).
    | KeptStopped of ActiveTimer

/// The format this Chrona writes and reads.
[<Literal>]
let Format = 1

/// The browser storage key for one person's timer in one organization.
let key (organizationId: string) (actorId: string) =
    $"chrona.timer.{organizationId}.{ActivityRecord.actorSegment actorId}"

let private segmentJson (segment: Segment) =
    Json.objectOf
        [ "start", Json.String(instantText segment.Start)
          "finish", (segment.Finish |> Option.map (instantText >> Json.String) |> Option.defaultValue Json.Null) ]

/// The kept timer as canonical text.
let encode (kept: Kept) =
    let state, timer =
        match kept with
        | KeptRunning t -> "running", t
        | KeptPaused t -> "paused", t
        | KeptStopped t -> "stopped", t

    Json.canonicalText (
        Json.objectOf
            [ "chronaTimer", Json.Number(decimal Format)
              "state", Json.String state
              "timerId", Json.String timer.TimerId
              "organizationId", Json.String timer.OrganizationId
              "actorId", Json.String timer.ActorId
              "deviceId", Json.String timer.DeviceId
              "zoneId", Json.String timer.ZoneId
              "segments", Json.Array(timer.Segments |> List.map segmentJson)
              "classification",
              (timer.Classification |> Option.map ActivityRecord.classificationJson |> Option.defaultValue Json.Null) ]
    )

let private segmentOf (value: Json) : Decoded<Segment> =
    closed [ "finish"; "start" ] value
    |> Result.bind (fun () ->
        match instant "start" value, optionalText "finish" value with
        | Ok start, Ok None -> Ok { Start = start; Finish = None }
        | Ok start, Ok(Some finish) ->
            ofInstantText "finish" finish
            |> Result.bind (fun finish ->
                if finish < start then Error "a segment finishes before it starts" else Ok { Start = start; Finish = Some finish })
        | Error e, _
        | _, Error e -> Error e)

/// A kept timer from its text, refusing anything this Chrona did not write.
let decode (stored: string) : Decoded<Kept> =
    match Json.parse stored with
    | Error error -> Error(JsonError.describe error)
    | Ok value ->
        closed [ "actorId"; "chronaTimer"; "classification"; "deviceId"; "organizationId"; "segments"; "state"; "timerId"; "zoneId" ] value
        |> Result.bind (fun () -> integer "chronaTimer" value)
        |> Result.bind (fun format -> if format = Format then Ok() else Error $"timer format {format}, not {Format}")
        |> Result.bind (fun () ->
            let classification =
                match field "classification" value with
                | Ok Json.Null -> Ok None
                | Ok _ -> ActivityRecord.classificationOf value |> Result.map Some
                | Error e -> Error e

            match
                text "state" value,
                text "timerId" value,
                text "organizationId" value,
                text "actorId" value,
                text "deviceId" value,
                text "zoneId" value,
                list "segments" segmentOf value,
                classification
            with
            | Ok state, Ok id, Ok organization, Ok actor, Ok device, Ok zone, Ok segments, Ok classification ->
                let timer =
                    { TimerId = id
                      OrganizationId = organization
                      ActorId = actor
                      DeviceId = device
                      ZoneId = zone
                      Segments = segments
                      Classification = classification }

                let openSegments = segments |> List.filter (fun s -> s.Finish.IsNone) |> List.length

                match state, openSegments with
                | _, _ when segments.IsEmpty -> Error "a timer has at least one segment"
                | "running", 1 when (List.last segments).Finish.IsNone -> Ok(KeptRunning timer)
                | "paused", 0 -> Ok(KeptPaused timer)
                | "stopped", 0 -> Ok(KeptStopped timer)
                | other, _ -> Error $"'{other}' does not match the timer's segments"
            | Error e, _, _, _, _, _, _, _
            | _, Error e, _, _, _, _, _, _
            | _, _, Error e, _, _, _, _, _
            | _, _, _, Error e, _, _, _, _
            | _, _, _, _, Error e, _, _, _
            | _, _, _, _, _, Error e, _, _
            | _, _, _, _, _, _, Error e, _
            | _, _, _, _, _, _, _, Error e -> Error e)

/// What a device keeps for a timer state and a stopped timer awaiting
/// completion; None when there is nothing to keep.
let ofState (state: TimerState) (stopped: Stopped option) =
    match stopped, state with
    | Some held, _ -> Some(KeptStopped held.Timer)
    | None, Running timer -> Some(KeptRunning timer)
    | None, Paused timer -> Some(KeptPaused timer)
    | None, Idle -> None
