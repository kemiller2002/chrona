/// Sign-in through Fides (CHX-022, CHX-023): the real Fides client, running
/// inside the Limen request/reply loop.
///
/// Fides' client is asynchronous and asks its host for a handful of browser
/// services (`ClientPorts`). Here every port that needs the browser becomes a
/// kernel request: the exchange is Limen's Http effect, device storage its
/// Storage effect, and this tab's session storage, leaving for the provider,
/// tidying the address bar and telling the other tabs are `chrona.host`
/// requests. A port call parks its continuation under a fresh correlation id
/// and the request goes out with the engine's next reply; when the kernel
/// answers, the continuation resumes, and runs until the client needs the
/// browser again or finishes. A finished operation becomes an engine message
/// (`IdentityChanged`). The browser runtime has one thread, so all of this
/// runs synchronously inside one `App.step`.
///
/// Tokens stay inside the Fides client. Chrona sees the identity the provider
/// resolved (subject and login), never a typed name, and hands Arca the
/// client's token provider (`TokenProvider`), never a token.
module Chrona.Application.Identity

open System
open System.Collections.Generic
open Fides
open Fides.Client
open Chrona.Domain
open Chrona.Engine.App
open Chrona.Engine.App.Model

/// What the client asked of the browser.
type KernelCall =
    | Post of url: string * body: string
    | DeviceGet of key: string
    | DeviceSet of key: string * value: string
    | DeviceRemove of key: string
    | TabGet of key: string
    | TabSet of key: string * value: string
    | TabRemove of key: string
    /// Leave the page for the provider's sign-in page.
    | Leave of url: string
    | ReplaceAddress of url: string
    | Announce of message: string

/// What the kernel answered.
type KernelAnswer =
    | Posted of HttpOutcome
    | Read of value: string option
    | Done

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
      /// Resumes the operation waiting on this correlation id; false when no
      /// operation is waiting on it.
      Answer: string -> KernelAnswer -> bool
      /// The kernel calls made and the engine messages produced since the
      /// last drain. An operation that failed unexpectedly is an exception.
      Drain: unit -> Result<(string * KernelCall) list * Update.Msg list, exn>
      /// Arca's token provider, once sign-in is configured.
      TokenProvider: unit -> Arca.TokenProvider option }

/// The engine's session for an identity the provider resolved: the stable
/// subject is the actor, the login is only a display name (CHX-022).
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

/// A new identity port. `now` and `randomBytes` are the clock and the
/// browser's cryptographic random source.
let create (now: unit -> DateTimeOffset) (randomBytes: int -> byte array) : IdentityPort =
    let waiting = Dictionary<string, KernelAnswer -> unit>()
    let outbox = List<string * KernelCall>()
    let finished = List<Update.Msg>()
    let failures = List<exn>()
    let mutable sequence = 0
    let mutable client: FidesClient option = None

    let call (request: KernelCall) : Async<KernelAnswer> =
        Async.FromContinuations(fun (resume, _, _) ->
            sequence <- sequence + 1
            let id = $"identity-{sequence}"
            waiting[id] <- resume
            outbox.Add(id, request))

    let read request =
        async {
            match! call request with
            | Read value -> return value
            | _ -> return None
        }

    let ports (exchange: string) =
        { PostToExchange =
            fun path body ->
                async {
                    match! call (Post(exchange + path, body)) with
                    | Posted outcome -> return outcome
                    | _ -> return HttpOutcome.Failed TransportFailure.Unreachable
                }
          TabStorage =
            { Read = fun key -> read (TabGet key)
              Write = fun key value -> call (TabSet(key, value)) |> Async.Ignore
              Remove = fun key -> call (TabRemove key) |> Async.Ignore }
          DeviceStorage =
            { Read = fun key -> read (DeviceGet key)
              Write = fun key value -> call (DeviceSet(key, value)) |> Async.Ignore
              Remove = fun key -> call (DeviceRemove key) |> Async.Ignore }
          Navigate = fun url -> call (Leave url) |> Async.Ignore
          ReplaceAddress = fun url -> call (ReplaceAddress url) |> Async.Ignore
          Broadcast = fun message -> call (Announce message) |> Async.Ignore |> Async.StartImmediate
          Now = now
          RandomBytes = randomBytes }

    /// Runs an operation to its first kernel call (or its end), recording the
    /// message it finishes with, or the exception it failed with.
    let start (work: Async<Update.Msg option>) =
        Async.StartImmediate(
            async {
                match! Async.Catch work with
                | Choice1Of2(Some msg) -> finished.Add msg
                | Choice1Of2 None -> ()
                | Choice2Of2 error -> failures.Add error
            }
        )

    let withClient (work: FidesClient -> Async<Update.Msg option>) =
        match client with
        | Some fides -> start (work fides)
        | None -> ()

    { Begin =
        fun config query ->
            let fides = FidesClient.create (configuration config) catalog (ports config.Exchange)
            client <- Some fides

            if isCallback query then
                start (async {
                    let! outcome = fides.CompleteCallback query
                    return Some(Update.IdentityChanged(callbackChange outcome))
                })
            else
                start (async {
                    let! state = fides.Restore()
                    return Some(Update.IdentityChanged(changeOf state))
                })
      SignIn =
        fun kept ->
            withClient (fun fides ->
                async {
                    match! fides.SignIn(retention kept) with
                    // The page is leaving for the provider; it returns with the callback.
                    | Ok() -> return Some(Update.IdentityChanged SigningIn)
                    | Error _ -> return Some(Update.IdentityChanged(SignedOutWith(Some "sign_in_failed")))
                })
      SignOut =
        fun () ->
            withClient (fun fides ->
                async {
                    let! _ = fides.SignOut()
                    return Some(Update.IdentityChanged(SignedOutWith(Some "signed_out")))
                })
      Receive =
        fun message ->
            withClient (fun fides ->
                async {
                    do! fides.Receive message
                    return Some(Update.IdentityChanged(changeOf (fides.State())))
                })
      Answer =
        fun id answer ->
            match waiting.TryGetValue id with
            | true, resume ->
                waiting.Remove id |> ignore
                resume answer
                true
            | _ -> false
      Drain =
        fun () ->
            let calls = List.ofSeq outbox
            let messages = List.ofSeq finished
            let failed = List.ofSeq failures
            outbox.Clear()
            finished.Clear()
            failures.Clear()

            match failed with
            | error :: _ -> Error error
            | [] -> Ok(calls, messages)
      TokenProvider = fun () -> client |> Option.map Fides.Arca.TokenBridge.ofClient }
