/// Asynchronous clients inside the Limen request/reply loop.
///
/// Fides' sign-in client and Arca's storage provider are asynchronous: they
/// ask their host for browser services and continue with the answers. Here
/// every such service is a kernel request. A call parks its continuation
/// under a fresh correlation id; the request goes out with the engine's next
/// reply (`Drain`); when the kernel answers, `Answer` resumes the
/// continuation, which runs until the client needs the browser again or
/// finishes. A finished operation yields engine messages. The browser
/// runtime has one thread, so all of this runs synchronously inside one
/// `App.step`; nothing here touches the browser itself.
module Chrona.Application.Bridge

open System.Collections.Generic
open Chrona.Engine.App

/// What a client asked of the browser.
type KernelCall =
    /// An Http request; its response body is read as text, and the named
    /// response headers are returned.
    | Http of method: string * url: string * headers: (string * string) list * body: string option * timeoutMs: int * responseHeaders: string list
    | DeviceGet of key: string
    | DeviceSet of key: string * value: string
    | DeviceRemove of key: string
    | TabGet of key: string
    | TabSet of key: string * value: string
    | TabRemove of key: string
    /// Leave the page for the identity provider's sign-in page.
    | Leave of url: string
    | ReplaceAddress of url: string
    | Announce of message: string
    /// Wait this long (a back-off).
    | Sleep of milliseconds: int
    /// A `limen.coordination` request (a Web Lock), as the contract
    /// serializes it.
    | Coordinate of request: string
    /// A `limen.store` request (IndexedDB), as the contract serializes it.
    | StoreOperation of request: string

/// What the kernel answered.
type KernelAnswer =
    | Answered of AppProtocol.HttpResult
    | Read of value: string option
    | Done
    /// The browser refused a storage request: `unavailable` or `quota-exceeded`.
    | Refused of reason: string
    /// A contract pack's result, as the kernel sent it (JSON).
    | Raw of result: string
    /// The kernel offers no such pack, or did not run the request.
    | Missing

/// One page's in-flight work.
[<Sealed>]
type Bridge() =
    let waiting = Dictionary<string, KernelAnswer -> unit>()
    let outbox = List<string * KernelCall>()
    let finished = List<Update.Msg>()
    let failures = List<exn>()
    let mutable sequence = 0

    /// Asks the browser for something and continues with its answer.
    member _.Call(request: KernelCall) : Async<KernelAnswer> =
        Async.FromContinuations(fun (resume, _, _) ->
            sequence <- sequence + 1
            let id = $"bridge-{sequence}"
            waiting[id] <- resume
            outbox.Add(id, request))

    /// Runs an operation to its first browser call (or its end), recording
    /// the messages it finishes with, or the exception it failed with.
    member _.Start(work: Async<Update.Msg list>) =
        Async.StartImmediate(
            async {
                match! Async.Catch work with
                | Choice1Of2 messages -> finished.AddRange messages
                | Choice2Of2 error -> failures.Add error
            }
        )

    /// Engine messages an operation produced while still running.
    member _.Emit(messages: Update.Msg list) = finished.AddRange messages

    /// Resumes the operation waiting on this correlation id; false when none is.
    member _.Answer (id: string) (answer: KernelAnswer) =
        match waiting.TryGetValue id with
        | true, resume ->
            waiting.Remove id |> ignore
            resume answer
            true
        | _ -> false

    /// The browser calls made and the engine messages produced since the last
    /// drain. An operation that failed unexpectedly is an exception.
    member _.Drain() : Result<(string * KernelCall) list * Update.Msg list, exn> =
        let calls = List.ofSeq outbox
        let messages = List.ofSeq finished
        let failed = List.ofSeq failures
        outbox.Clear()
        finished.Clear()
        failures.Clear()

        match failed with
        | error :: _ -> Error error
        | [] -> Ok(calls, messages)
