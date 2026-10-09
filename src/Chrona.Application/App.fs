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
      Store: Store.StorePort
      /// The Chrona build this page runs (`development` when it was not
      /// built for a deployment; WI-0063).
      Build: string }

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
    | Copying of Update.CopyPurpose
    | Printing
    /// Reading the deployment's configuration document.
    | Configuration
    /// A browser service Fides' client or Arca's adapter asked for (Bridge).
    | BridgeCall
    /// Reading the device's kept timer (WI-0055), under this key.
    | TimerLoad of key: string
    /// Keeping or clearing it.
    | TimerSave
    /// The page's lifecycle subscription (WI-0063).
    | Lifecycle
    /// Asking which Chrona build the deployment serves now.
    | ShellCheck
    /// Reloading the page for a newer Chrona.
    | Reloading
    /// Moving focus to the control that replaced the one the person used.
    | Focusing
    /// Keeping or forgetting the return target in this tab (CHX-460).
    | ReturnKeep
    /// Reading the return target this tab kept across a sign-in.
    | ReturnLoad

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

/// Where the deployment says which Chrona build it serves: written beside the
/// WebAssembly build (`build/wasm/wwwroot/chrona-build.json`), asked for with
/// a fresh query so no cache answers for the server (WI-0063).
let buildUrl (state: State) (at: DateTimeOffset) =
    let folder = state.Path.Substring(0, state.Path.LastIndexOf '/' + 1)
    let site = folder.TrimEnd('/')
    let parent = site.Substring(0, site.LastIndexOf '/' + 1)
    state.Origin + parent + $"build/wasm/wwwroot/chrona-build.json?at={at.ToUnixTimeMilliseconds()}"

/// Where this tab keeps the address to return to after sign-in: session
/// storage, which survives the round trip to GitHub in this tab and nothing
/// longer. It holds a relative address, never a token (CHX-460).
[<Literal>]
let ReturnKey = "chrona.returnTo"

/// How long the exchange and the configuration may take to answer.
[<Literal>]
let RequestTimeoutMs = 15000

/// A browser service the bridge's clients asked for, as a Limen request, or
/// the answer it gets at once: a wait the kernel cannot time (no schedule
/// pack) is over, and a pack the kernel does not offer is missing.
let private kernelRequest (state: State) (id: string) (call: Bridge.KernelCall) =
    let hostCall operation arguments =
        if negotiated host state then
            Ok(Host(id, operation, arguments))
        else
            raise (CapabilityFailed("chrona.host", "Sign-in needs the chrona.host pack, which the kernel did not offer"))

    match call with
    | Bridge.Http(method, url, headers, body, timeoutMs, responseHeaders) -> Ok(Http(id, method, url, headers, body, timeoutMs, responseHeaders))
    | Bridge.DeviceGet key -> Ok(StorageGet(id, key))
    | Bridge.DeviceSet(key, value) -> Ok(StorageSet(id, key, value))
    | Bridge.DeviceRemove key -> Ok(StorageRemove(id, key))
    | Bridge.TabGet key -> hostCall "tabGet" [ "key", key ]
    | Bridge.TabSet(key, value) -> hostCall "tabSet" [ "key", key; "value", value ]
    | Bridge.TabRemove key -> hostCall "tabRemove" [ "key", key ]
    | Bridge.Leave url -> hostCall "leave" [ "url", url ]
    | Bridge.ReplaceAddress url -> hostCall "replaceAddress" [ "url", url ]
    | Bridge.Announce message -> hostCall "broadcast" [ "message", message ]
    | Bridge.Coordinate request when negotiated coordination state -> Ok(Contracted(id, coordination, request))
    | Bridge.StoreOperation request when negotiated store state -> Ok(Contracted(id, store, request))
    // Without the pack: answered at once, as missing.
    | Bridge.Coordinate _
    | Bridge.StoreOperation _ -> Error Bridge.Missing
    | Bridge.Sleep milliseconds when negotiated schedule state -> Ok(Wake(id, milliseconds))
    | Bridge.Sleep _ -> Error Bridge.Done

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
            // Relative to the page: its path, and the place in the fragment.
            | Update.Navigate(Limen.Routing.NavigationEffect.Push location) ->
                minted Navigation, requests @ [ Push(id, state.Path + Limen.Routing.Location.href Places.mode location) ], immediate
            | Update.Navigate(Limen.Routing.NavigationEffect.Replace location) ->
                minted Navigation, requests @ [ Replace(id, state.Path + Limen.Routing.Location.href Places.mode location) ], immediate
            // The return target lives in this tab's session storage (chrona.host).
            | Update.KeepReturn(Some target) when negotiated host state ->
                minted ReturnKeep, requests @ [ Host(id, "tabSet", [ "key", ReturnKey; "value", target ]) ], immediate
            | Update.KeepReturn None when negotiated host state ->
                minted ReturnKeep, requests @ [ Host(id, "tabRemove", [ "key", ReturnKey ]) ], immediate
            | Update.KeepReturn _ -> state, requests, immediate
            | Update.ReadReturn when negotiated host state -> minted ReturnLoad, requests @ [ Host(id, "tabGet", [ "key", ReturnKey ]) ], immediate
            | Update.ReadReturn -> state, requests, immediate @ [ Update.ReturnRead None ]
            | Update.Wake(generation, ms) when negotiated schedule state -> minted (Tick generation), requests @ [ Wake(id, ms) ], immediate
            // Without the schedule pack the display does not refresh between
            // events; timing itself is unaffected (it is timestamps).
            | Update.Wake _ -> state, requests, immediate
            | Update.DescribeEnvironment when negotiated environment state -> minted Environment, requests @ [ DescribeEnvironment id ], immediate
            | Update.DescribeEnvironment -> state, requests, immediate @ [ Update.EnvironmentUnavailable ]
            | Update.CopyText(purpose, text) when List.contains "Clipboard" state.Effects -> minted (Copying purpose), requests @ [ Copy(id, text) ], immediate
            | Update.CopyText(purpose, _) -> state, requests, immediate @ [ Update.Copied(purpose, false) ]
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
            | Update.LeaveDevice(choice, unsent) ->
                env.Store.SignedOut choice unsent
                state, requests, immediate
            | Update.ClearDevice ->
                env.Store.ClearDevice()
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
                state, requests, immediate
            | Update.ReadInboxes ->
                env.Store.ReadInboxes()
                state, requests, immediate
            | Update.LoadTimer key ->
                minted (TimerLoad key), requests @ [ StorageGet(id, key) ], immediate
            | Update.SaveTimer(key, Some value) -> minted TimerSave, requests @ [ StorageSet(id, key, value) ], immediate
            | Update.SaveTimer(key, None) -> minted TimerSave, requests @ [ StorageRemove(id, key) ], immediate
            | Update.CheckShell when List.contains "Http" state.Effects ->
                minted ShellCheck, requests @ [ Http(id, "GET", buildUrl state (env.Now()), [], None, RequestTimeoutMs, []) ], immediate
            | Update.CheckShell -> state, requests, immediate
            | Update.ReloadPage when negotiated host state -> minted Reloading, requests @ [ Host(id, "reload", []) ], immediate
            | Update.ReloadPage -> state, requests, immediate
            | Update.FocusControl target when negotiated host state ->
                minted Focusing, requests @ [ Host(id, "focus", [ "target", target ]) ], immediate
            | Update.FocusControl _ -> state, requests, immediate
            | Update.TakeOverQueue ->
                env.Store.TakeOver()
                state, requests, immediate
            | Update.SendEarlier ->
                env.Store.SendEarlier()
                state, requests, immediate
            | Update.DiscardEarlier ->
                env.Store.DiscardEarlier()
                state, requests, immediate
            | Update.ClaimQueue ->
                env.Store.Claim()
                state, requests, immediate
            | Update.SendUnsent ->
                env.Store.SendNow()
                state, requests, immediate
            | Update.DiscardUnsent ->
                env.Store.Discard()
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
