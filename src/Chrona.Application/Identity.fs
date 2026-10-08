/// Sign-in through Fides (CHX-022, CHX-023): the real Fides client, running
/// inside the Limen request/reply loop.
///
/// Fides' client is asynchronous and asks its host for a handful of browser
/// services (`ClientPorts`). Each becomes a kernel request through the
/// `Bridge`: the exchange is Limen's Http effect, device storage its Storage
/// effect, and this tab's session storage, leaving for the provider, tidying
/// the address bar and telling the other tabs are `chrona.host` requests. A
/// finished operation becomes an engine message (`IdentityChanged`).
///
/// Tokens stay inside the Fides client. Chrona sees the identity the provider
/// resolved (subject and login), never a typed name, and hands Arca the
/// client's token provider (`TokenProvider`), never a token.
module Chrona.Application.Identity

open System
open Fides
open Fides.Client
open Chrona.Domain
open Chrona.Engine.App
open Chrona.Engine.App.Model
open Chrona.Application.Bridge

/// The identity port the application drives.
[<NoComparison; NoEquality>]
type IdentityPort =
    { /// Sets the client up from the deployment's configuration and either
      /// completes the provider's callback or restores a kept session.
      Begin: Deployment.IdentityConfig -> (string * string) list -> unit
      SignIn: Retention -> unit
      SignOut: unit -> unit
      /// A message another tab broadcast.
      Receive: string -> unit
      /// Arca's token provider, once sign-in is configured.
      TokenProvider: unit -> Arca.TokenProvider option
      /// GitHub refused the token: the session is revoked.
      ReportUnauthorized: unit -> Async<unit> }

/// The engine's session for an identity the provider resolved: the stable
/// subject is the actor, the login is only a display name (CHX-022). The
/// engine places it in the deployment's organization.
let sessionOf (identity: Identity) : Session =
    let (ProviderId provider) = identity.Provider

    { ActorId = $"{provider}:{identity.Subject}"
      OrganizationId = "local"
      DisplayName = identity.Login
      Kind = SignedIn provider }

/// The engine's view of a session state.
let changeOf (state: SessionState) : IdentityChange =
    match state with
    | SessionState.SignedIn identity -> SignedInAs(sessionOf identity)
    | SessionState.SigningIn -> SigningIn
    | SessionState.SignedOut -> SignedOutWith None
    | SessionState.Expired -> SignedOutWith(Some "expired")
    | SessionState.Revoked -> SignedOutWith(Some "revoked")
    | SessionState.ProviderUnavailable -> ProviderUnavailable

/// The engine's view of how a callback ended.
let callbackChange (outcome: CallbackOutcome) : IdentityChange =
    match outcome with
    | CompletedSignIn identity -> SignedInAs(sessionOf identity)
    | other -> SignedOutWith(Some(CallbackOutcome.code other))

/// Whether a page was opened with the provider's callback.
let isCallback (query: (string * string) list) =
    query |> List.exists (fun (name, _) -> name = "state" || name = "code" || name = "error")

let private retention =
    function
    | ThisPage -> MemoryOnly
    | ThisTab -> SessionScoped

/// The client configuration for a deployment's identity settings.
let configuration (config: Deployment.IdentityConfig) : ClientConfiguration =
    { Application = config.Application
      Provider = ProviderId config.Provider
      ClientId = config.ClientId
      RedirectUri = config.RedirectUri }

/// The providers Chrona can sign in with.
let catalog = ProviderCatalog.ofList [ GitHub.provider GitHub.githubDotCom ]

/// How long the exchange may take to answer.
[<Literal>]
let ExchangeTimeoutMs = 15000

/// A new identity port over the page's bridge. `now` and `randomBytes` are
/// the clock and the browser's cryptographic random source.
let create (bridge: Bridge) (now: unit -> DateTimeOffset) (randomBytes: int -> byte array) : IdentityPort =
    let mutable client: FidesClient option = None

    let read request =
        async {
            match! bridge.Call request with
            | Read value -> return value
            | _ -> return None
        }

    let ports (exchange: string) =
        { PostToExchange =
            fun path body ->
                async {
                    match! bridge.Call(Http("POST", exchange + path, [ "Content-Type", "application/json" ], Some body, ExchangeTimeoutMs, [])) with
                    | Answered(AppProtocol.HttpSucceeded(status, _, body)) -> return HttpOutcome.Responded { Status = status; Body = body }
                    | Answered(AppProtocol.HttpUnknown _) -> return HttpOutcome.Failed TransportFailure.TimedOut
                    | _ -> return HttpOutcome.Failed TransportFailure.Unreachable
                }
          TabStorage =
            { Read = fun key -> read (TabGet key)
              Write = fun key value -> bridge.Call(TabSet(key, value)) |> Async.Ignore
              Remove = fun key -> bridge.Call(TabRemove key) |> Async.Ignore }
          DeviceStorage =
            { Read = fun key -> read (DeviceGet key)
              Write = fun key value -> bridge.Call(DeviceSet(key, value)) |> Async.Ignore
              Remove = fun key -> bridge.Call(DeviceRemove key) |> Async.Ignore }
          Navigate = fun url -> bridge.Call(Leave url) |> Async.Ignore
          ReplaceAddress = fun url -> bridge.Call(ReplaceAddress url) |> Async.Ignore
          Broadcast =
            fun message ->
                bridge.Start(
                    async {
                        let! _ = bridge.Call(Announce message)
                        return []
                    }
                )
          Now = now
          RandomBytes = randomBytes }

    let changed change = [ Update.IdentityChanged change ]

    let withClient (work: FidesClient -> Async<Update.Msg list>) =
        match client with
        | Some fides -> bridge.Start(work fides)
        | None -> ()

    { Begin =
        fun config query ->
            let fides = FidesClient.create (configuration config) catalog (ports config.Exchange)
            client <- Some fides

            if isCallback query then
                bridge.Start(async {
                    let! outcome = fides.CompleteCallback query
                    return changed (callbackChange outcome)
                })
            else
                bridge.Start(async {
                    let! state = fides.Restore()
                    return changed (changeOf state)
                })
      SignIn =
        fun kept ->
            withClient (fun fides ->
                async {
                    match! fides.SignIn(retention kept) with
                    // The page is leaving for the provider; it returns with the callback.
                    | Ok() -> return changed SigningIn
                    | Error _ -> return changed (SignedOutWith(Some "sign_in_failed"))
                })
      SignOut =
        fun () ->
            withClient (fun fides ->
                async {
                    let! _ = fides.SignOut()
                    return changed (SignedOutWith(Some "signed_out"))
                })
      Receive =
        fun message ->
            withClient (fun fides ->
                async {
                    do! fides.Receive message
                    return changed (changeOf (fides.State()))
                })
      TokenProvider = fun () -> client |> Option.map Fides.Arca.TokenBridge.ofClient
      ReportUnauthorized =
        fun () ->
            match client with
            | Some fides -> fides.ReportUnauthorized()
            | None -> async.Return() }
