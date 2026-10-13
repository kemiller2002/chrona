/// The application's side of the Limen boundary, as values: what the
/// composition root supplies (`Env`: the clock, fresh ids and the ports),
/// what the boundary keeps between messages (`State`), and what each
/// correlation id was minted for (`Purpose`).
module Chrona.Application.AppState

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

let internal negotiated (offer: CapabilityOffer) (state: State) = List.contains offer state.Capabilities

/// Runs one engine message.
let internal run (env: Env) (msg: Update.Msg) (model: Model) =
    let ctx: Update.Ctx = { Now = env.Now(); NewId = env.NewId }
    UpdateDispatch.update ctx msg model

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
