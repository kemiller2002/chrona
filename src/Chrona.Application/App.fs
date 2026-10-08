/// The Chrona application's side of the Limen boundary: kernel messages to
/// engine messages, engine effects to Limen requests, every step under the
/// Aegis boundary. Deterministic for a given `Env`: the clock, fresh ids and
/// the two ports arrive as values, and the only effectful code is the
/// composition root (`Runtime`) that supplies them.
///
/// The ports (DF-CHRONA-2026-0003):
/// - `Env.Session` is the identity port. `localSession` is its implementation
///   until Fides sign-in (WI-0029).
/// - `Env.Store` is the store port. `inMemoryStore` acknowledges every commit
///   at once and keeps nothing beyond the model; Arca (WI-0032) replaces it
///   with an asynchronous store answering the same requests.
module Chrona.Application.App

open System
open System.Text.Json.Nodes
open Aegis
open Chrona.Engine.App
open Chrona.Engine.App.Model
open Chrona.Application.Json
open Chrona.Application.Limen
open Chrona.Application.Boundary
open Chrona.Application.AppProtocol

[<NoComparison; NoEquality>]
type Env =
    { Now: unit -> DateTimeOffset
      /// A fresh, unique id with a prefix.
      NewId: string -> string
      Session: Session
      StoreKind: StoreKind
      Store: StoreRequest -> StoreOutcome }

/// The identity port's implementation until sign-in exists.
let localSession =
    { ActorId = "local-person"
      OrganizationId = "local"
      DisplayName = "Local session"
      Kind = LocalSession }

/// The store port's in-memory implementation: every commit is acknowledged
/// at once; nothing outlives the tab.
let inMemoryStore (_: StoreRequest) = Committed

/// What a correlation id was minted for.
type Purpose =
    | Tick of generation: int
    | Environment
    | Navigation

[<NoComparison; NoEquality>]
type State =
    { Model: Model option
      /// The page's path, which routes are appended to as fragments.
      Path: string
      Capabilities: CapabilityOffer list
      Pending: Map<string, Purpose>
      Sequence: int
      Fault: FaultView option }

let initial =
    { Model = None
      Path = "/"
      Capabilities = []
      Pending = Map.empty
      Sequence = 0
      Fault = None }

let private negotiated (offer: CapabilityOffer) (state: State) = List.contains offer state.Capabilities

/// Runs one engine message, then every store request it produced through the
/// store port, feeding each answer back, until no store work remains.
let rec private run (env: Env) (msg: Update.Msg) (model: Model) (effects: Update.Effect list) =
    let ctx: Update.Ctx = { Now = env.Now(); NewId = env.NewId }
    let next, produced = Update.update ctx msg model
    let stores, others = produced |> List.partition (function Update.Store _ -> true | _ -> false)

    stores
    |> List.fold
        (fun (model, effects) effect ->
            match effect with
            | Update.Store request -> run env (Update.StoreAnswered(request.CommitId, env.Store request)) model effects
            | _ -> model, effects)
        (next, effects @ others)

/// Engine effects to Limen requests, minting the correlation ids.
let private requests (state: State) (effects: Update.Effect list) =
    effects
    |> List.fold
        (fun (state: State, requests, immediate) effect ->
            let sequence = state.Sequence + 1
            let id = $"app-{sequence}"
            let minted purpose = { state with Sequence = sequence; Pending = state.Pending.Add(id, purpose) }

            match effect with
            | Update.Navigate hash -> minted Navigation, requests @ [ Push(id, state.Path + hash) ], immediate
            | Update.Wake(generation, ms) when negotiated schedule state -> minted (Tick generation), requests @ [ Wake(id, ms) ], immediate
            // Without the schedule pack the display does not refresh between
            // events; timing itself is unaffected (it is timestamps).
            | Update.Wake _ -> state, requests, immediate
            | Update.DescribeEnvironment when negotiated environment state -> minted Environment, requests @ [ DescribeEnvironment id ], immediate
            | Update.DescribeEnvironment -> state, requests, immediate @ [ Update.EnvironmentUnavailable ]
            | Update.Store _ -> invalidOp "Store requests are answered before replying")
        (state, [], [])

let private view (state: State) =
    let app = state.Model |> Option.map Project.project |> Option.defaultValue []
    app @ faultView state.Fault

/// One engine message through `run`, its effects through `requests`, and any
/// message that must follow at once (an environment the kernel cannot
/// describe) handled in the same reply.
let rec private advance (env: Env) (state: State) (msg: Update.Msg) (sent: Request list) =
    match state.Model with
    | None -> state, sent
    | Some model ->
        let model, effects = run env msg model []
        let state, made, immediate = requests { state with Model = Some model } effects
        immediate |> List.fold (fun (state, sent) msg -> advance env state msg sent) (state, sent @ made)

let private take (id: string) (state: State) =
    match state.Pending.TryFind id with
    | Some purpose -> purpose, { state with Pending = state.Pending.Remove id }
    | None -> raise (CapabilityFailed("correlation", $"A result for {id}, which this engine never requested"))

let private environmentZone (result: JsonNode) =
    match tryField "kind" result |> Option.map (asString "$.result.kind") with
    | Some "Described" ->
        let environment = required "environment" "$.result" asObject result
        Update.EnvironmentDescribed(required "timeZone" "$.result.environment" asString environment)
    | _ -> Update.EnvironmentUnavailable

let private scheduled (generation: int) (result: JsonNode) =
    match tryField "kind" result |> Option.map (asString "$.result.kind") with
    | Some "Fired" -> Some(Update.Ticked generation)
    | _ -> None

/// Pure apart from `env`: (state, message) -> (state, reply).
let step (env: Env) (state: State) (inbound: Inbound) =
    match inbound with
    | Initialize(protocolVersion, effects, path, hash, offer) ->
        if protocolVersion <> 1 then
            raise (MalformedInput("$.protocolVersion", $"Unsupported protocol version {protocolVersion}"))
        elif not (List.contains "Navigation" effects) then
            raise (CapabilityFailed("Navigation", "The kernel does not offer the Navigation effect the application's routes need"))

        match answer offer with
        | Rejected _ as handshake -> state, encode (view state) [] [] (Some handshake)
        | Accepted(_, _, capabilities) as handshake ->
            let started =
                { state with
                    Model = Some(Model.initial env.Session env.StoreKind (env.Now()))
                    Path = path
                    Capabilities = capabilities }

            let next, sent = advance env started (Update.Started hash) []
            next, encode (view next) sent [] (Some handshake)
    | other ->
        let state, msg =
            match other with
            | Event(name, key, value, isChecked) -> state, Some(Update.Ui(name, key, defaultArg value "", isChecked))
            | LocationChanged hash -> state, Some(Update.LocationMoved hash)
            | NavigationResult(id, outcome) ->
                let _, state = take id state

                match outcome with
                | Moved hash -> state, Some(Update.LocationMoved hash)
                | Dispatched
                | NavigationFailed _ -> state, None
            | CapabilityResult(id, _, outcome) ->
                match take id state, outcome with
                | (Environment, state), Completed result -> state, Some(environmentZone result)
                | (Environment, state), NotExecuted _ -> state, Some Update.EnvironmentUnavailable
                | (Tick generation, state), Completed result -> state, scheduled generation result
                | (_, state), _ -> state, None
            | CapabilityFact(capability, _) ->
                raise (CapabilityFailed(capability, $"Unexpected CapabilityFact from {capability}: this engine watches nothing"))
            | Initialize _ -> invalidOp "handled above"

        let next, sent =
            match msg with
            | Some msg -> advance env state msg []
            | None -> state, []

        next, encode (view next) sent [] None

/// Handles one kernel message under the Aegis boundary. A fault leaves the
/// state as it was and is shown until the next message.
let handle (aegis: AegisConfig) (env: Env) (state: State) (messageJson: string) =
    let cleared = { state with Fault = None }

    match capture aegis "Chrona.App.dispatch" (fun () -> step env cleared (decode messageJson)) with
    | Ok result -> result
    | Result.Error fault ->
        let faulted = { state with Fault = Some fault }
        faulted, encode (view faulted) [] [] None
