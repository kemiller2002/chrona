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
      /// The page's in-flight asynchronous work (sign-in and storage).
      Bridge: Bridge.Bridge
      Identity: Identity.IdentityPort
      Store: Store.StorePort }

/// The identity port's implementation until sign-in exists.
let localSession =
    { ActorId = "local-person"
      OrganizationId = "local"
      DisplayName = "Local session"
      Kind = LocalSession }

/// What a correlation id was minted for.
type Purpose =
    | Tick of generation: int
    | Environment
    | Navigation
    | Copying
    | Printing
    /// Reading the deployment's configuration document.
    | Configuration
    /// A browser service Fides' client or Arca's adapter asked for (Bridge).
    | BridgeCall

[<NoComparison; NoEquality>]
type State =
    { Model: Model option
      /// The page's origin, for addresses on this site.
      Origin: string
      /// The page's path, which routes are appended to as fragments.
      Path: string
      Capabilities: CapabilityOffer list
      /// The core effects the kernel offered (Navigation, Clipboard, ...).
      Effects: string list
      Pending: Map<string, Purpose>
      Sequence: int
      Fault: FaultView option }

let initial =
    { Model = None
      Origin = ""
      Path = "/"
      Capabilities = []
      Effects = []
      Pending = Map.empty
      Sequence = 0
      Fault = None }

let private negotiated (offer: CapabilityOffer) (state: State) = List.contains offer state.Capabilities

/// Runs one engine message.
let private run (env: Env) (msg: Update.Msg) (model: Model) =
    let ctx: Update.Ctx = { Now = env.Now(); NewId = env.NewId }
    Update.update ctx msg model

/// The deployment's configuration document: `chrona.deployment.json` beside
/// the page. A deployment replaces it; the repository's copy runs locally.
let configurationUrl (state: State) =
    let folder = state.Path.Substring(0, state.Path.LastIndexOf '/' + 1)
    state.Origin + folder + "chrona.deployment.json"

/// How long the exchange and the configuration may take to answer.
[<Literal>]
let RequestTimeoutMs = 15000

/// A browser service the bridge's clients asked for, as a Limen request;
/// None for a wait the kernel cannot time (no schedule pack), which is
/// answered at once.
let private kernelRequest (state: State) (id: string) (call: Bridge.KernelCall) =
    let hostCall operation arguments =
        if negotiated host state then
            Some(Host(id, operation, arguments))
        else
            raise (CapabilityFailed("chrona.host", "Sign-in needs the chrona.host pack, which the kernel did not offer"))

    match call with
    | Bridge.Http(method, url, headers, body, timeoutMs, responseHeaders) -> Some(Http(id, method, url, headers, body, timeoutMs, responseHeaders))
    | Bridge.DeviceGet key -> Some(StorageGet(id, key))
    | Bridge.DeviceSet(key, value) -> Some(StorageSet(id, key, value))
    | Bridge.DeviceRemove key -> Some(StorageRemove(id, key))
    | Bridge.TabGet key -> hostCall "tabGet" [ "key", key ]
    | Bridge.TabSet(key, value) -> hostCall "tabSet" [ "key", key; "value", value ]
    | Bridge.TabRemove key -> hostCall "tabRemove" [ "key", key ]
    | Bridge.Leave url -> hostCall "leave" [ "url", url ]
    | Bridge.ReplaceAddress url -> hostCall "replaceAddress" [ "url", url ]
    | Bridge.Announce message -> hostCall "broadcast" [ "message", message ]
    | Bridge.Sleep milliseconds when negotiated schedule state -> Some(Wake(id, milliseconds))
    | Bridge.Sleep _ -> None

/// Engine effects to Limen requests, minting the correlation ids. Sign-in and
/// storage effects start work in their ports; their requests follow on `settle`.
let private requests (env: Env) (state: State) (effects: Update.Effect list) =
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
            | Update.CopyText text when List.contains "Clipboard" state.Effects -> minted Copying, requests @ [ Copy(id, text) ], immediate
            | Update.CopyText _ -> state, requests, immediate @ [ Update.Copied false ]
            | Update.Print when negotiated print state -> minted Printing, requests @ [ PrintPage id ], immediate
            // Without the print pack the browser's own Print still prints
            // the Folio document.
            | Update.Print -> state, requests, immediate
            | Update.ReadConfiguration when List.contains "Http" state.Effects ->
                minted Configuration, requests @ [ Http(id, "GET", configurationUrl state, [], None, RequestTimeoutMs, []) ], immediate
            | Update.ReadConfiguration -> state, requests, immediate @ [ Update.ConfigurationRead None ]
            | Update.BeginIdentity(config, callback) ->
                env.Identity.Begin config callback
                state, requests, immediate
            | Update.SignIn retention ->
                env.Identity.SignIn retention
                state, requests, immediate
            | Update.SignOut ->
                env.Identity.SignOut()
                state, requests, immediate
            | Update.Store request ->
                env.Store.Commit request
                state, requests, immediate
            | Update.OpenStore(config, session, dates) ->
                env.Store.Open config session dates
                state, requests, immediate
            | Update.ConfirmAdministrator ->
                env.Store.Confirm()
                state, requests, immediate
            | Update.ReadMonths dates ->
                env.Store.Read dates
                state, requests, immediate
            | Update.RebuildIndex ->
                env.Store.Rebuild()
                state, requests, immediate)
        (state, [], [])

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
                    | Some request -> { state with Pending = state.Pending.Add(id, BridgeCall) }, made @ [ request ], untimed
                    | None -> state, made, untimed @ [ id ])
                (state, [], [])

        untimed |> List.iter (fun id -> env.Bridge.Answer id Bridge.Done |> ignore)
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

            let next, sent = advance env started (Update.Started(hash, query)) []
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
            | ClipboardResult(id, succeeded) ->
                let _, state = take id state
                state, Some(Update.Copied succeeded)
            | CapabilityResult(id, _, outcome) ->
                match take id state, outcome with
                | (Environment, state), Completed result -> state, Some(environmentZone result)
                | (Environment, state), NotExecuted _ -> state, Some Update.EnvironmentUnavailable
                | (Tick generation, state), Completed result -> state, scheduled generation result
                | (BridgeCall, state), Completed result ->
                    // A wait the bridge asked for has passed, or a chrona.host answer.
                    match tryField "kind" result |> Option.map (asString "$.result.kind") with
                    | Some "Fired" -> answerBridge env id Bridge.Done
                    | _ -> answerBridge env id (hosted result)

                    state, None
                | (BridgeCall, state), NotExecuted _ ->
                    answerBridge env id (Bridge.Read None)
                    state, None
                | (_, state), _ -> state, None
            | HttpResponse(id, result) ->
                match take id state, result with
                | (Configuration, state), HttpSucceeded(200, _, body) -> state, Some(Update.ConfigurationRead(Some body))
                | (Configuration, state), _ -> state, Some(Update.ConfigurationRead None)
                | (BridgeCall, state), result ->
                    answerBridge env id (Bridge.Answered result)
                    state, None
                | (_, _), _ -> raise (CapabilityFailed("Http", $"An Http result for {id}, which was not an Http request"))
            | StorageResponse(id, result) ->
                match take id state with
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
            | CapabilityFact(capability, _) ->
                raise (CapabilityFailed(capability, $"Unexpected CapabilityFact from {capability}: this engine watches only chrona.host"))
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
