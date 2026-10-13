/// The Chrona application's side of the Limen boundary: kernel messages to
/// engine messages, engine effects to Limen requests, every step under the
/// Aegis boundary. Deterministic for a given `Env`: the clock, fresh ids and
/// the two ports arrive as values, and the only effectful code is the
/// composition root (`Runtime`) that supplies them.
///
/// The ports (DF-CHRONA-2026-0003):
/// - `Env.Session` is the session of a deployment that configures no
///   sign-in (`localSession`). `Env.Identity` is Fides sign-in (WI-0029),
///   used when the deployment's configuration (`chrona.deployment.json`, read
///   at start) names an exchange.
/// - `Env.Store` is the store port. `inMemoryStore` acknowledges every commit
///   at once and keeps nothing beyond the model; Arca (WI-0032) replaces it
///   with an asynchronous store answering the same requests.
///
/// What the boundary keeps and is given is in `AppState`; turning effects
/// into requests is in `AppRequests`.
module Chrona.Application.App

open System
open System.Text.Json.Nodes
open Aegis
open Chrona.Engine.App
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Application.Json
open Chrona.Application.Limen
open Chrona.Application.Boundary
open Chrona.Application.AppProtocol
open Chrona.Application.AppState
open Chrona.Application.AppRequests

let private view (state: State) =
    let app = state.Model |> Option.map Project.project |> Option.defaultValue []
    app @ faultView state.Fault

/// One engine message through `run`, its effects through `requests`, then
/// whatever the identity port did (`settle`), and any message that must
/// follow at once (an environment the kernel cannot describe, a finished
/// sign-in) handled in the same reply.
let rec private advance (env: Env) (state: State) (msg: Update.Msg) (sent: Request list) =
    match state.Model with
    | None -> state, sent
    | Some model ->
        let model, effects = run env msg model
        let state, made, immediate = requests env { state with Model = Some model } effects
        immediate |> List.fold (fun (state, sent) msg -> advance env state msg sent) (settle env state (sent @ made))

/// The bridge's browser calls become requests; the operations that finished
/// become engine messages. A wait the kernel cannot time is answered at once.
/// An operation that failed unexpectedly is raised here, inside the Aegis
/// boundary.
and private settle (env: Env) (state: State) (sent: Request list) =
    match env.Bridge.Drain() with
    | Result.Error error -> raise error
    | Ok([], []) -> state, sent
    | Ok(calls, finished) ->
        let state, made, untimed =
            calls
            |> List.fold
                (fun (state: State, made, untimed) (id, call) ->
                    match kernelRequest state id call with
                    | Ok request -> { state with Pending = state.Pending.Add(id, BridgeCall) }, made @ [ request ], untimed
                    | Error answer -> state, made, untimed @ [ id, answer ])
                (state, [], [])

        untimed |> List.iter (fun (id, answer) -> env.Bridge.Answer id answer |> ignore)
        let state, sent = if untimed.IsEmpty then state, sent @ made else settle env state (sent @ made)

        finished |> List.fold (fun (state, sent) msg -> advance env state msg sent) (state, sent)

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

/// A `chrona.host` result: a value read from tab storage, or done.
let private hosted (result: JsonNode) =
    match tryField "value" result with
    | Some value -> Bridge.Read(Some(asString "$.result.value" value))
    | None -> Bridge.Read None

let private answerBridge (env: Env) (id: string) (answer: Bridge.KernelAnswer) =
    if not (env.Bridge.Answer id answer) then
        raise (CapabilityFailed("correlation", $"A result for {id}, which no operation is waiting on"))

/// The build named in the deployment's `chrona-build.json`, if it names one.
let private buildOf (body: string) =
    try
        match JsonNode.Parse body |> Option.ofObj |> Option.bind (fun node -> tryField "build" node) with
        | Some node when node.GetValueKind() = System.Text.Json.JsonValueKind.String -> Some(node.GetValue<string>())
        | _ -> None
    with _ ->
        None

let private scheduled (generation: int) (result: JsonNode) =
    match tryField "kind" result |> Option.map (asString "$.result.kind") with
    | Some "Fired" -> Some(Update.Ticked generation)
    | _ -> None

/// Pure apart from `env`: (state, message) -> (state, reply).
let step (env: Env) (state: State) (inbound: Inbound) =
    match inbound with
    | Initialize(protocolVersion, effects, origin, path, query, hash, offer) ->
        if protocolVersion <> 1 then
            raise (MalformedInput("$.protocolVersion", $"Unsupported protocol version {protocolVersion}"))
        elif not (List.contains "Navigation" effects) then
            raise (CapabilityFailed("Navigation", "The kernel does not offer the Navigation effect the application's routes need"))
        elif not (List.contains "Http" effects && List.contains "Storage" effects) then
            raise (CapabilityFailed("Http", "The kernel does not offer the Http and Storage effects configuration and sign-in need"))

        match answer offer with
        | Rejected _ as handshake -> state, encode (view state) [] [] (Some handshake)
        | Accepted(_, _, capabilities) as handshake ->
            let started =
                { state with
                    Model = Some(Model.initial env.Session env.Store.Kind (env.Now()))
                    Origin = origin
                    Path = path
                    Capabilities = capabilities
                    Effects = effects }

            let next, sent = advance env started (Update.Started({ Origin = origin; Path = path }, hash, query)) []
            let next, sent = advance env next (Update.BuildKnown env.Build) sent

            // The page's comings and goings, when the kernel offers them.
            let next, sent =
                if negotiated lifecycle next then
                    let id = $"app-{next.Sequence + 1}"

                    { next with Sequence = next.Sequence + 1; Pending = next.Pending.Add(id, Lifecycle) },
                    sent @ [ Subscribe(id, [ "visibility"; "pageLifecycle"; "freezing"; "connectivity" ]) ]
                else
                    next, sent

            next, encode (view next) sent [] (Some handshake)
    | other ->
        let state, msg =
            match other with
            | Event(name, key, value, isChecked) -> state, Some(Update.Ui(name, key, defaultArg value "", isChecked))
            | LocationChanged hash -> state, Some(Update.LocationMoved hash)
            // The engine moved already; the browser following is not news.
            | NavigationResult(id, _) ->
                let _, state = take id state
                state, None
            | ClipboardResult(id, succeeded) ->
                match take id state with
                | Copying purpose, state -> state, Some(Update.Copied(purpose, succeeded))
                | _ -> raise (CapabilityFailed("Clipboard", $"A clipboard result for {id}, which was not a copy"))
            | CapabilityResult(id, capability, outcome) ->
                match take id state, outcome with
                | (Environment, state), Completed result -> state, Some(environmentZone result)
                | (Environment, state), NotExecuted _ -> state, Some Update.EnvironmentUnavailable
                | (Tick generation, state), Completed result -> state, scheduled generation result
                // A contract pack's result, decoded by the contract's codec.
                | (BridgeCall, state), Completed result when capability = coordination.Id || capability = store.Id ->
                    answerBridge env id (Bridge.Raw(result.ToJsonString()))
                    state, None
                | (BridgeCall, state), NotExecuted _ when capability = coordination.Id || capability = store.Id ->
                    answerBridge env id Bridge.Missing
                    state, None
                | (BridgeCall, state), Completed result ->
                    // A wait the bridge asked for has passed, or a chrona.host answer.
                    match tryField "kind" result |> Option.map (asString "$.result.kind") with
                    | Some "Fired" -> answerBridge env id Bridge.Done
                    | _ -> answerBridge env id (hosted result)

                    state, None
                // The subscription took; its facts follow.
                | (Lifecycle, state), _ -> state, None
                | (Reloading, state), _ -> state, None
                | (Focusing, state), _ -> state, None
                | (ReturnKeep, state), _ -> state, None
                | (ReturnLoad, state), Completed result ->
                    match tryField "value" result with
                    | Some value -> state, Some(Update.ReturnRead(Some(asString "$.result.value" value)))
                    | None -> state, Some(Update.ReturnRead None)
                | (ReturnLoad, state), NotExecuted _ -> state, Some(Update.ReturnRead None)
                | (BridgeCall, state), NotExecuted _ ->
                    answerBridge env id (Bridge.Read None)
                    state, None
                | (_, state), _ -> state, None
            | HttpResponse(id, result) ->
                match take id state, result with
                | (Configuration, state), HttpSucceeded(200, _, body) -> state, Some(Update.ConfigurationRead(Some body))
                | (Configuration, state), _ -> state, Some(Update.ConfigurationRead None)
                | (ShellCheck, state), HttpSucceeded(200, _, body) -> state, Some(Update.ShellChecked(buildOf body))
                | (ShellCheck, state), _ -> state, Some(Update.ShellChecked None)
                | (BridgeCall, state), result ->
                    answerBridge env id (Bridge.Answered result)
                    state, None
                | (_, _), _ -> raise (CapabilityFailed("Http", $"An Http result for {id}, which was not an Http request"))
            | StorageResponse(id, result) ->
                match take id state with
                | TimerLoad key, state ->
                    match result with
                    | StorageValue value -> state, Some(Update.TimerLoaded(key, value))
                    | StorageFailed _ -> state, Some(Update.TimerLoaded(key, None))
                // A timer the browser would not keep is still in the page; nothing else changes.
                | TimerSave, state -> state, None
                | BridgeCall, state ->
                    answerBridge
                        env
                        id
                        (match result with
                         | StorageValue value -> Bridge.Read value
                         | StorageFailed reason -> Bridge.Refused reason)

                    state, None
                | _ -> raise (CapabilityFailed("Storage", $"A Storage result for {id}, which was not a Storage request"))
            | CapabilityFact(capability, fact) when capability = host.Id ->
                match tryField "kind" fact |> Option.map (asString "$.fact.kind") with
                | Some "Broadcast" ->
                    env.Identity.Receive(required "message" "$.fact" asString fact)
                    state, None
                | _ -> raise (MalformedInput("$.fact.kind", "a known chrona.host fact"))
            | CapabilityFact(capability, fact) when capability = lifecycle.Id ->
                let visible () = tryField "visibility" fact |> Option.map (asString "$.fact.visibility") = Some "visible"
                let flagOf name = tryField name fact |> Option.map (fun node -> node.GetValue<bool>())

                match tryField "kind" fact |> Option.map (asString "$.fact.kind") with
                | Some "VisibilityChanged" when visible () -> state, Some Update.PageReturned
                | Some "PageShown" when flagOf "persisted" = Some true -> state, Some Update.PageReturned
                | Some "Resumed" -> state, Some Update.PageReturned
                | Some "ConnectivityChanged" ->
                    match flagOf "online" with
                    | Some online -> state, Some(Update.ConnectionChanged online)
                    | None -> raise (MalformedInput("$.fact.online", "true or false"))
                | Some("VisibilityChanged" | "PageShown" | "PageHidden" | "Frozen" | "PrerenderActivated" | "ConnectionChanged") -> state, None
                | _ -> raise (MalformedInput("$.fact.kind", "a known limen.lifecycle fact"))
            | CapabilityFact(capability, fact) when capability = coordination.Id ->
                match tryField "kind" fact |> Option.map (asString "$.fact.kind") with
                // Another tab took the unsent changes over ("use this tab
                // instead" there): this tab no longer holds them.
                | Some "LockLost" ->
                    env.Store.Lost()
                    state, None
                | _ -> raise (MalformedInput("$.fact.kind", "a limen.coordination fact Chrona asked for"))
            | CapabilityFact(capability, fact) when capability = store.Id ->
                match tryField "kind" fact |> Option.map (asString "$.fact.kind") with
                // The browser closed the database under the page (cleared
                // site data, eviction): the queue is opened again.
                | Some "ConnectionLost" ->
                    env.Store.Reconnect()
                    state, None
                // Another context upgraded the database; Arca's next request
                // reports whatever it changed.
                | Some "VersionChanged" -> state, None
                | _ -> raise (MalformedInput("$.fact.kind", "a limen.store fact"))
            | CapabilityFact(capability, _) ->
                raise (
                    CapabilityFailed(
                        capability,
                        $"Unexpected CapabilityFact from {capability}: this engine watches only chrona.host, limen.lifecycle, limen.coordination and limen.store"
                    )
                )
            | Initialize _ -> invalidOp "handled above"

        let next, sent =
            match msg with
            | Some msg -> advance env state msg []
            | None -> settle env state []

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
