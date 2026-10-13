/// Arca's GitHub adapter behind the store (WI-0032): its requests sent as
/// Limen Http requests through the bridge, its tokens from Fides.
module Chrona.Application.StoreGitHub

open System
open System.Collections.Generic
open Arca
open Arca.GitHub
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Application.Bridge
open Chrona.Application.Store

let methodName =
    function
    | HttpMethod.Get -> "GET"
    | HttpMethod.Post -> "POST"
    | HttpMethod.Patch -> "PATCH"
    | HttpMethod.Put -> "PUT"
    | HttpMethod.Delete -> "DELETE"

let describeResolve =
    function
    | ResolveError.CredentialUnavailable _ -> Unopened.Refused "You are not signed in to GitHub any more. Sign in again."
    | ResolveError.CredentialRejected -> Unopened.Refused "GitHub refused your sign-in. Sign in again."
    | ResolveError.RepositoryNotFound repository -> Unopened.Refused $"Your GitHub account cannot see {repository}."
    | ResolveError.Call _ -> Unopened.Unreachable "GitHub could not be reached."

/// Arca's GitHub adapter, its requests sent as Limen Http requests, its waits
/// as `limen.schedule` timeouts, its tokens from Fides. A 401 is reported to
/// Fides, which then asks the person to sign in again.
let gitHub (bridge: Bridge) (tokens: unit -> TokenProvider option) (unauthorized: unit -> Async<unit>) : Backend =
    let host: Host =
        { Send =
            fun authorized ->
                async {
                    let request = authorized.Request
                    let credential = authorized.Credential |> Option.map AccessToken.authorization |> Option.toList

                    match!
                        bridge.Call(
                            Http(methodName request.Method, request.Url, request.Headers @ credential, request.Body, request.TimeoutMs, request.ResponseHeaders)
                        )
                    with
                    | Answered(AppProtocol.HttpSucceeded(status, headers, body)) ->
                        if status = 401 && authorized.Credential.IsSome then
                            do! unauthorized ()

                        return HttpOutcome.Response(status, Http.normalizeHeaders headers, body)
                    | Answered(AppProtocol.HttpFailed reason) ->
                        return
                            HttpOutcome.Failed(
                                match reason with
                                | "aborted" -> HttpFailure.Aborted
                                | "invalid-response" -> HttpFailure.InvalidResponse
                                | "too-large" -> HttpFailure.TooLarge
                                | _ -> HttpFailure.Network
                            )
                    | Answered AppProtocol.HttpCancelled -> return HttpOutcome.Cancelled
                    | Answered(AppProtocol.HttpUnknown reason) ->
                        return
                            HttpOutcome.OutcomeUnknown(
                                if reason = "connection-lost" then UnknownReason.ConnectionLost else UnknownReason.TimeoutAfterDispatch
                            )
                    | Read _
                    | Done
                    | Refused _
                    | Raw _
                    | Missing -> return HttpOutcome.Failed HttpFailure.InvalidResponse
                }
          Wait = fun delay -> bridge.Call(Sleep(int delay.TotalMilliseconds)) |> Async.Ignore
          Tokens =
            fun () ->
                match tokens () with
                | Some provider -> provider ()
                | None -> async.Return(Error TokenUnavailable.NoToken) }

    { Connect =
        fun location ->
            let github = GitHubStorage.connect host (GitHubConfig.create location)
            { Provider = github.Provider; PinAt = Some github.PinAt }
      Resolve =
        fun location ->
            async {
                match! Conversation.run host (Arca.GitHub.Identity.resolve (GitHubConfig.create location)) with
                | Ok snapshot -> return Ok snapshot
                | Error error -> return Error(describeResolve error)
            } }
