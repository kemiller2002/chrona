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
      Identity: Identity.IdentityPort
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
    | Copying
    | Printing
    /// Reading the deployment's configuration document.
    | Configuration
    /// A browser service Fides' client asked for (Identity).
    | IdentityCall

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

/// The deployment's configuration document: `chrona.deployment.json` beside
/// the page. A deployment replaces it; the repository's copy runs locally.
let configurationUrl (state: State) =
    let folder = state.Path.Substring(0, state.Path.LastIndexOf '/' + 1)
    state.Origin + folder + "chrona.deployment.json"

/// How long the exchange and the configuration may take to answer.
[<Literal>]
let RequestTimeoutMs = 15000

/// A browser service the identity port asked for, as a Limen request.
let private kernelRequest (state: State) (id: string) (call: Identity.KernelCall) =
    let hostCall operation arguments =
        if negotiated host state then
            Host(id, operation, arguments)
        else
            raise (CapabilityFailed("chrona.host", "Sign-in needs the chrona.host pack, which the kernel did not offer"))

    match call with
    | Identity.Post(url, body) -> Http(id, "POST", url, [ "Content-Type", "application/json" ], Some body, RequestTimeoutMs)
    | Identity.DeviceGet key -> StorageGet(id, key)
    | Identity.DeviceSet(key, value) -> StorageSet(id, key, value)
    | Identity.DeviceRemove key -> StorageRemove(id, key)
    | Identity.TabGet key -> hostCall "tabGet" [ "key", key ]
    | Identity.TabSet(key, value) -> hostCall "tabSet" [ "key", key; "value", value ]
    | Identity.TabRemove key -> hostCall "tabRemove" [ "key", key ]
    | Identity.Leave url -> hostCall "leave" [ "url", url ]
    | Identity.ReplaceAddress url -> hostCall "replaceAddress" [ "url", url ]
    | Identity.Announce message -> hostCall "broadcast" [ "message", message ]

/// Engine effects to Limen requests, minting the correlation ids. Sign-in
/// effects start work in the identity port; its requests follow on `settle`.
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
                minted Configuration, requests @ [ Http(id, "GET", configurationUrl state, [], None, RequestTimeoutMs) ], immediate
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
            | Update.Store _ -> invalidOp "Store requests are answered before replying")
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
        let model, effects = run env msg model []
        let state, made, immediate = requests env { state with Model = Some model } effects
        immediate |> List.fold (fun (state, sent) msg -> advance env state msg sent) (settle env state (sent @ made))

/// The identity port's kernel calls become requests; the operations it
/// finished become engine messages. An operation that failed unexpectedly
/// is raised here, inside the Aegis boundary.
and private settle (env: Env) (state: State) (sent: Request list) =
    match env.Identity.Drain() with
    | Result.Error error -> raise error
    | Ok([], []) -> state, sent
    | Ok(calls, finished) ->
        let state, made =
            calls
            |> List.fold
                (fun (state: State, made) (id, call) ->
                    { state with Pending = state.Pending.Add(id, IdentityCall) }, made @ [ kernelRequest state id call ])
                (state, [])

        finished |> List.fold (fun (state, sent) msg -> advance env state msg sent) (state, sent @ made)

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

/// A kernel Http outcome as Fides reads it.
let private posted =
    function
    | HttpSucceeded(status, body) -> Identity.Posted(Fides.HttpOutcome.Responded { Status = status; Body = body })
    | HttpUnknown _ -> Identity.Posted(Fides.HttpOutcome.Failed Fides.TransportFailure.TimedOut)
    | HttpFailed _
    | HttpCancelled -> Identity.Posted(Fides.HttpOutcome.Failed Fides.TransportFailure.Unreachable)

/// A `chrona.host` result: a value read from tab storage, or done.
let private hosted (result: JsonNode) =
    match tryField "value" result with
    | Some value -> Identity.Read(Some(asString "$.result.value" value))
    | None -> Identity.Read None

let private answerIdentity (env: Env) (id: string) (answer: Identity.KernelAnswer) =
    if not (env.Identity.Answer id answer) then
        raise (CapabilityFailed("correlation", $"A result for {id}, which no sign-in operation is waiting on"))

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
                    Model = Some(Model.initial env.Session env.StoreKind (env.Now()))
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
                | (IdentityCall, state), Completed result ->
                    answerIdentity env id (hosted result)
                    state, None
                | (IdentityCall, state), NotExecuted _ ->
                    answerIdentity env id (Identity.Read None)
                    state, None
                | (_, state), _ -> state, None
            | HttpResponse(id, result) ->
                match take id state, result with
                | (Configuration, state), HttpSucceeded(200, body) -> state, Some(Update.ConfigurationRead(Some body))
                | (Configuration, state), _ -> state, Some(Update.ConfigurationRead None)
                | (IdentityCall, state), result ->
                    answerIdentity env id (posted result)
                    state, None
                | (_, _), _ -> raise (CapabilityFailed("Http", $"An Http result for {id}, which was not an Http request"))
            | StorageResponse(id, result) ->
                match take id state with
                | IdentityCall, state ->
                    answerIdentity
                        env
                        id
                        (match result with
                         | StorageValue value -> Identity.Read value
                         | StorageFailed _ -> Identity.Read None)

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
