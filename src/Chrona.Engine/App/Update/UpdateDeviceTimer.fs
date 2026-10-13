/// The device's timer (WI-0055): where this device keeps it, and a kept
/// timer recovered explicitly with its elapsed time (10.2).
module Chrona.Engine.App.UpdateDeviceTimer

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update
open Chrona.Engine.App.UpdateCommands
open Chrona.Engine.App.UpdateRecording
open Chrona.Engine.App.UpdateCorrections
open Chrona.Engine.App.UpdateReports
open Chrona.Engine.App.UpdatePlaces
open Chrona.Engine.App.UpdatePeriods
open Chrona.Engine.App.UpdateCandidates
open Chrona.Engine.App.UpdateMembers
open Chrona.Engine.App.UpdateResolving
open Chrona.Engine.App.UpdateEvents

/// Whether this device keeps an active or stopped timer for the person (WI-0055).
let hasKeptTimer (model: Model) =
    TimerRecord.ofState model.Timer model.Stopped |> Option.isSome

/// Where this device keeps the person's timer.
let internal timerKey (model: Model) =
    TimerRecord.key model.Session.OrganizationId model.Session.ActorId

/// Whether someone is working here whose timer this device keeps.
let internal keepsTimers (model: Model) =
    match model.Identity.Mode with
    | SignedInMode
    | LocalOnly -> true
    | _ -> false

/// The records cannot be opened (offline at startup, for example), but the
/// person's own timer on this device keeps working.
let internal offlineTimer (model: Model) =
    model.Identity.Mode = SignedInMode
    && model.Store.Failure.IsSome
    && hasKeptTimer model

/// A timer the device kept, recovered explicitly with its elapsed time
/// (10.2): never silently discarded, and never over a timer already here.
let internal timerLoaded (key: string) (value: string option) (model: Model) =
    // Said now, and again when the records open.
    let say (note: string) (model: Model) = { model with Announcement = note; TimerNote = Some note }

    match value with
    | _ when key <> timerKey model || hasKeptTimer model -> model, []
    | None -> model, []
    | Some text ->
        match TimerRecord.decode text with
        // Left as it is in the browser: it is not overwritten while this
        // page has no timer of its own.
        | Error _ -> say "A timer kept in this browser could not be read. It was left as it is." model, []
        | Ok(TimerRecord.KeptStopped timer) ->
            match tryZone timer.ZoneId with
            | Error _ -> say "A stopped timer kept in this browser names an unknown time zone. It was left as it is." model, []
            | Ok zone ->
                let finish = timer.Segments |> List.choose _.Finish |> List.fold max (List.head timer.Segments).Start

                match Timer.stop zone finish (Timer.Paused timer) with
                | Ok(_, stopped) ->
                    say
                        $"Your stopped timer was recovered: {Format.minutes stopped.TotalMinutes} held. Complete the record to save it."
                        { model with
                            Stopped = Some stopped
                            LongTimerConfirmed = false
                            CompletionDraft =
                                stopped.Timer.Classification |> Option.map ofClassification |> Option.defaultValue emptyClassification },
                    []
                | Error _ -> model, []
        | Ok(TimerRecord.KeptPaused timer) ->
            say
                $"Your paused timer was recovered: {Format.minutes (Timer.elapsedMinutes model.Now timer)} so far."
                { model with Timer = Timer.Paused timer },
            []
        | Ok(TimerRecord.KeptRunning timer) ->
            let generation = model.TickGeneration + 1

            say
                $"Your timer was recovered and is still running: {Format.minutes (Timer.elapsedMinutes model.Now timer)} so far."
                { model with
                    Timer = Timer.Running timer
                    TickGeneration = generation },
            [ Wake(generation, TickMs) ]
