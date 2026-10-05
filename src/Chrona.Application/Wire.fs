/// The slice page's side of the Limen boundary: maps kernel messages onto
/// engine commands and runs each step under the Aegis boundary. Ported from
/// `verification/kernel-slice/src/transport.ts`.
module Chrona.Application.Wire

open Aegis
open Chrona.Engine
open Chrona.Engine.KernelSlice
open Chrona.Application.Json
open Chrona.Application.Limen
open Chrona.Application.Boundary

/// The Limen protocol version (`Initialize.protocolVersion`) the slice speaks.
[<Literal>]
let ProtocolVersion = 1

type State =
    { Model: Model
      /// Correlation ids the engine has minted: `slice-N`.
      Sequence: int
      Fault: FaultView option }

let initial =
    { Model = initialModel
      Sequence = 0
      Fault = None }

let render (state: State) (effects: Effect list) (handshake: Handshake option) =
    encode (KernelSliceView.project state.Model @ faultView state.Fault) effects handshake

let private command (correlationId: string) (name: string) (value: string option) =
    match eventToCommand name (defaultArg value "") correlationId with
    | Some command -> command
    // index.html and the engine disagree: a defect, not an operational failure.
    | None -> invalidOp $"Unknown event name: {name}"

/// Pure: (state, message) -> (state, reply). Raising leaves the state as it
/// was; the boundary classifies or re-raises.
let step (state: State) (inbound: Inbound) =
    // The one place a correlation id is made: the engine mints them.
    let sequence = state.Sequence + 1
    let correlationId = $"slice-{sequence}"

    let apply (cmd: Command) =
        match transition state.Model cmd with
        | TransitionResult.Accepted(model, effects) -> model, effects
        // A refused command leaves the model as it was and requests nothing.
        | TransitionResult.Rejected(model, _) -> model, []

    let model, effects, handshake =
        match inbound with
        | Initialize(protocolVersion, offered, offer) ->
            if protocolVersion <> ProtocolVersion then
                raise (MalformedInput("$.protocolVersion", $"Unsupported protocol version {protocolVersion}"))
            elif not (List.contains "Storage" offered) then
                raise (CapabilityFailed("Storage", "The kernel does not offer the Storage effect this slice requires"))
            else
                // The kernel applies this view only after it verifies the
                // answer; a Rejected answer leaves the page unbound.
                state.Model, [], Some(offer |> Option.map answer |> Option.defaultValue missing)
        // The slice has no URL-driven state: re-projecting unchanged is what
        // "nothing happened here" looks like.
        | LocationChanged -> state.Model, [], None
        | Event(name, _, value, _) ->
            let model, effects = apply (command correlationId name value)
            model, effects, None
        | StorageResult(correlation, outcome) ->
            let model, effects = apply (RecordStorage(correlation, outcome))
            model, effects, None

    let next =
        { state with
            Model = model
            Sequence = sequence }

    next, render next effects handshake

/// Handles one kernel message under the Aegis boundary. A fault leaves the
/// state as it was and is shown until the next message.
let handle (aegis: AegisConfig) (state: State) (messageJson: string) =
    let cleared = { state with Fault = None }

    match capture aegis "Chrona.KernelSlice.dispatch" (fun () -> step cleared (decode messageJson)) with
    | Ok result -> result
    | Result.Error fault ->
        let faulted = { state with Fault = Some fault }
        faulted, render faulted [] None
