/// Limen's browser/engine protocol (version 1) as the Chrona application
/// reads and writes it: the kernel's messages in, the engine's view, effects
/// and handshake answer out. Mechanics only; no Chrona decision is made here.
///
/// The application requests Navigation (routes), Clipboard (copying an
/// export), Http (the deployment's configuration and Fides' exchange) and
/// Storage (sign-in state Fides keeps across tabs), and four optional
/// capability packs: `limen.schedule` (the timer's once-a-second wake-up),
/// `limen.environment` (the browser's time zone), `chrona.print` (the
/// browser's print dialog) and `chrona.host` (this tab's session storage,
/// leaving for the identity provider, tidying the address bar after its
/// callback, and telling the origin's other tabs about sign-in).
///
/// See the `protocol` export of `@echelon-foundry/limen` (0.7.1) and its
/// generated schedule and environment contracts.
module Chrona.Application.AppProtocol

open System.Text.Json
open System.Text.Json.Nodes
open Chrona.Engine.View
open Chrona.Application.Json
open Chrona.Application.Limen

type CapabilityOffer =
    { Id: string
      Version: int
      Fingerprint: string }

/// `limen.schedule` v1 (sha256 from the installed package's generated contract).
let schedule =
    { Id = "limen.schedule"
      Version = 1
      Fingerprint = "sha256:627657798f0fa735ac6f955eb4f413e7c397f0aef6942b340a87683a30db54d0" }

/// `limen.environment` v1.
let environment =
    { Id = "limen.environment"
      Version = 1
      Fingerprint = "sha256:1b206aa0bd7b72688e47f8034166128078a168a42ef9b52cf4a93499b00782f7" }

/// `chrona.print` v1: Chrona's own pack (web-kernel/print.js) that opens the
/// browser's print dialog for the page's Folio document.
let print =
    { Id = "chrona.print"
      Version = 1
      Fingerprint = "chrona.print/1: print" }

/// `chrona.host` v1: Chrona's own pack (web-kernel/host.js) for what Fides'
/// client needs from the browser beyond Limen's core effects.
let host =
    { Id = "chrona.host"
      Version = 1
      Fingerprint = "chrona.host/1: tab storage, leave, replace address, broadcast, reload, focus" }

/// `limen.lifecycle` v1 (sha256 from the installed package's generated
/// contract): the page's visibility, freezing, back/forward-cache restores
/// and connectivity, as facts (WI-0063).
let lifecycle =
    { Id = "limen.lifecycle"
      Version = 1
      Fingerprint = "sha256:cadd4037cac232869a486237ec523e667af9490cdcf3e4fb1ba729cbc5c87978" }

/// `limen.coordination` v1: Web Locks, so one tab holds this browser's
/// unsent changes (Arca's LocalStorageQueue.own; WI-0067).
let coordination =
    { Id = "limen.coordination"
      Version = 1
      Fingerprint = "sha256:510dfbcd2f3f7966b842d518d209a511ada3ffb30132853f291e9d5fa8342684" }

/// The optional packs the application selects when the kernel offers them.
let wanted = [ schedule; environment; print; host; lifecycle; coordination ]

/// What came back for an Http request (Limen's EffectOutcome).
type HttpResult =
    /// A response; the headers are those the request asked for, names in lower case.
    | HttpSucceeded of status: int * headers: (string * string) list * body: string
    | HttpFailed of reason: string
    | HttpCancelled
    /// The request may or may not have reached the server.
    | HttpUnknown of reason: string

/// What came back for a Storage request.
type StorageResult =
    | StorageValue of value: string option
    | StorageFailed of reason: string

type NavigationOutcome =
    | Moved of hash: string
    | Dispatched
    | NavigationFailed of reason: string

[<NoComparison; NoEquality>]
type CapabilityOutcome =
    | Completed of result: JsonNode
    | NotExecuted of reason: string

[<NoComparison; NoEquality>]
type Inbound =
    | Initialize of
        protocolVersion: int *
        effects: string list *
        origin: string *
        path: string *
        query: (string * string) list *
        hash: string *
        handshake: JsonNode option
    | Event of name: string * key: string option * value: string option * isChecked: bool option
    | LocationChanged of hash: string
    | NavigationResult of correlationId: string * NavigationOutcome
    | CapabilityResult of correlationId: string * capability: string * CapabilityOutcome
    /// Whether the browser accepted text onto the clipboard.
    | ClipboardResult of correlationId: string * succeeded: bool
    | CapabilityFact of capability: string * fact: JsonNode
    | HttpResponse of correlationId: string * HttpResult
    | StorageResponse of correlationId: string * StorageResult

/// `?a=1&b=two%20words` as decoded pairs, in order. A name without `=` has
/// an empty value.
let queryPairs (query: string) =
    query.TrimStart('?').Split('&', System.StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun pair ->
        let decode (text: string) = System.Uri.UnescapeDataString(text.Replace('+', ' '))

        match pair.IndexOf '=' with
        | -1 -> decode pair, ""
        | index -> decode (pair.Substring(0, index)), decode (pair.Substring(index + 1)))
    |> List.ofArray

let private location (path: string) (node: JsonNode) =
    required "path" path asString node, (optional "hash" path asString node |> Option.defaultValue "")

let private navigation (path: string) (node: JsonNode) =
    match required "kind" path asString node with
    | "Success" -> Moved(snd (location $"{path}.location" (required "location" path asObject node)))
    | "Dispatched" -> Dispatched
    | "Failure" -> NavigationFailed(required "reason" path asString node)
    | other -> raise (MalformedInput($"{path}.kind", $"a known navigation outcome, not '{other}'"))

let private capability (path: string) (node: JsonNode) =
    match required "kind" path asString node with
    | "Completed" -> Completed(required "result" path asObject node)
    | "Unsupported"
    | "Rejected" -> NotExecuted(required "reason" path asString node)
    | other -> raise (MalformedInput($"{path}.kind", $"a known capability outcome, not '{other}'"))

let private effectResult (node: JsonNode) =
    let path = "$.result"
    let correlation = required "correlationId" path asString node

    match required "kind" path asString node with
    | "NavigationResult" -> NavigationResult(correlation, navigation $"{path}.outcome" (required "outcome" path asObject node))
    | "ClipboardResult" ->
        let outcome = required "outcome" path asObject node

        match required "kind" $"{path}.outcome" asString outcome with
        | "Success" -> ClipboardResult(correlation, true)
        | "Failure" -> ClipboardResult(correlation, false)
        | other -> raise (MalformedInput($"{path}.outcome.kind", $"a known clipboard outcome, not '{other}'"))
    | "CapabilityResult" ->
        CapabilityResult(correlation, required "capability" path asString node, capability $"{path}.outcome" (required "outcome" path asObject node))
    | "HttpResult" ->
        let outcome = required "outcome" path asObject node
        let at = $"{path}.outcome"

        let result =
            match required "kind" at asString outcome with
            | "Success" ->
                let body = optional "body" at asString outcome |> Option.defaultValue ""

                let headers =
                    match tryField "headers" outcome with
                    | Some found ->
                        match found with
                        | :? JsonObject as headers ->
                            headers
                            |> Seq.choose (fun pair ->
                                pair.Value
                                |> Option.ofObj
                                |> Option.map (fun value -> pair.Key.ToLowerInvariant(), asString $"{at}.headers.{pair.Key}" value))
                            |> List.ofSeq
                        | _ -> []
                    | None -> []

                HttpSucceeded(required "status" at asInt outcome, headers, body)
            | "Failure" -> HttpFailed(required "reason" at asString outcome)
            | "Cancelled" -> HttpCancelled
            | "OutcomeUnknown" -> HttpUnknown(required "reason" at asString outcome)
            | other -> raise (MalformedInput($"{at}.kind", $"a known Http outcome, not '{other}'"))

        HttpResponse(correlation, result)
    | "StorageResult" ->
        let outcome = required "outcome" path asObject node
        let at = $"{path}.outcome"

        let result =
            match required "kind" at asString outcome with
            | "Success" ->
                StorageValue(optional "value" at asString outcome)
            | "Failure" -> StorageFailed(required "reason" at asString outcome)
            | other -> raise (MalformedInput($"{at}.kind", $"a known Storage outcome, not '{other}'"))

        StorageResponse(correlation, result)
    | other -> raise (CapabilityFailed(other, $"Unexpected {other} ({correlation}): the application requests no such effect"))

/// Reads one message from the kernel.
let decode (messageJson: string) =
    let message = parse messageJson |> asObject "$"

    match required "kind" "$" asString message with
    | "Initialize" ->
        let where = required "location" "$" asObject message
        let path, hash = location "$.location" where

        Initialize(
            required "protocolVersion" "$" asInt message,
            required "capabilities" "$" asArray message |> List.mapi (fun index item -> asString $"$.capabilities[{index}]" item),
            required "origin" "$.location" asString where,
            path,
            optional "query" "$.location" asString where |> Option.defaultValue "" |> queryPairs,
            hash,
            tryField "handshake" message
        )
    | "Event" ->
        let event = required "event" "$" asObject message

        Event(
            required "name" "$.event" asString event,
            optional "key" "$.event" asString event,
            optional "value" "$.event" asString event,
            optional "checked" "$.event" asBool event
        )
    | "LocationChanged" -> LocationChanged(snd (location "$.location" (required "location" "$" asObject message)))
    | "EffectResult" -> effectResult (required "result" "$" asObject message)
    | "CapabilityFact" -> CapabilityFact(required "capability" "$" asString message, required "fact" "$" asObject message)
    | other -> raise (MalformedInput("$.kind", $"a known message kind, not '{other}'"))

// ---------------------------------------------------------------------------
// The handshake answer.
// ---------------------------------------------------------------------------

[<NoComparison; NoEquality>]
type Handshake =
    | Accepted of minor: int * contract: JsonNode * capabilities: CapabilityOffer list
    | Rejected of reason: (Utf8JsonWriter -> unit)

/// Accepts the Core contract, selecting each wanted capability pack the
/// kernel offers at exactly the version and fingerprint this engine was
/// written against; or says precisely why not.
let answer (offer: JsonNode option) : Handshake =
    match offer with
    | None -> Rejected(fun writer -> writer.WriteString("kind", "HandshakeMissing"))
    | Some offer ->
        match Limen.answer offer with
        | Limen.Rejected reason -> Rejected reason
        | Limen.Accepted(minor, contract) ->
            let offered =
                match tryField "capabilities" offer with
                | Some list ->
                    asArray "$.handshake.capabilities" list
                    |> List.mapi (fun index item ->
                        let path = $"$.handshake.capabilities[{index}]"

                        { Id = required "id" path asString item
                          Version = required "version" path asInt item
                          Fingerprint = required "fingerprint" path asString item })
                | None -> []

            Accepted(minor, contract, wanted |> List.filter (fun w -> List.contains w offered))

// ---------------------------------------------------------------------------
// The reply.
// ---------------------------------------------------------------------------

/// An effect, as Limen requests it.
type Request =
    | Push of correlationId: string * url: string
    | Wake of correlationId: string * delayMs: int
    | DescribeEnvironment of correlationId: string
    | Copy of correlationId: string * text: string
    | PrintPage of correlationId: string
    /// An Http request whose response body is read as text.
    | Http of
        correlationId: string *
        method: string *
        url: string *
        headers: (string * string) list *
        body: string option *
        timeoutMs: int *
        responseHeaders: string list
    | StorageGet of correlationId: string * key: string
    | StorageSet of correlationId: string * key: string * value: string
    | StorageRemove of correlationId: string * key: string
    /// A `chrona.host` request: its operation and string arguments.
    | Host of correlationId: string * operation: string * arguments: (string * string) list
    /// A `limen.lifecycle` subscription to these topics.
    | Subscribe of correlationId: string * topics: string list
    /// A `limen.coordination` exclusive lock, never stolen.
    | Acquire of correlationId: string * name: string * wait: bool

let private writeScalar (writer: Utf8JsonWriter) =
    function
    | Text text -> writer.WriteStringValue text
    | Flag flag -> writer.WriteBooleanValue flag
    | Number number -> writer.WriteNumberValue number

let private writeView (writer: Utf8JsonWriter) (view: View) =
    writer.WriteStartObject()

    for name, value in view do
        writer.WritePropertyName name

        match value with
        | Value scalar -> writeScalar writer scalar
        | Items items ->
            writer.WriteStartArray()

            for item in items do
                writer.WriteStartObject()

                for field, scalar in item do
                    writer.WritePropertyName field
                    writeScalar writer scalar

                writer.WriteEndObject()

            writer.WriteEndArray()

    writer.WriteEndObject()

let private writeCapability (writer: Utf8JsonWriter) (correlationId: string) (offer: CapabilityOffer) (request: Utf8JsonWriter -> unit) =
    writer.WriteString("kind", "Capability")
    writer.WriteString("correlationId", correlationId)
    writer.WriteString("capability", offer.Id)
    writer.WriteNumber("version", offer.Version)
    writer.WritePropertyName "request"
    writer.WriteStartObject()
    request writer
    writer.WriteEndObject()

let private writeRequest (writer: Utf8JsonWriter) (request: Request) =
    writer.WriteStartObject()

    match request with
    | Push(correlationId, url) ->
        writer.WriteString("kind", "Navigation")
        writer.WriteString("operation", "push")
        writer.WriteString("correlationId", correlationId)
        writer.WriteString("url", url)
    | Wake(correlationId, delayMs) ->
        writeCapability writer correlationId schedule (fun w ->
            w.WriteString("operation", "timeout")
            w.WriteNumber("delayMs", delayMs))
    | Acquire(correlationId, name, wait) ->
        writeCapability writer correlationId coordination (fun w ->
            w.WriteString("operation", "acquire")
            w.WriteString("name", name)
            w.WriteString("mode", "exclusive")
            w.WriteBoolean("wait", wait)
            w.WriteBoolean("steal", false))
    | Subscribe(correlationId, topics) ->
        writeCapability writer correlationId lifecycle (fun w ->
            w.WriteString("operation", "subscribe")
            w.WritePropertyName "topics"
            w.WriteStartArray()

            for topic in topics do
                w.WriteStringValue topic

            w.WriteEndArray())
    | DescribeEnvironment correlationId ->
        writeCapability writer correlationId environment (fun w ->
            w.WriteString("operation", "describe")
            w.WritePropertyName "preferences"
            w.WriteStartArray()
            w.WriteEndArray())
    | Copy(correlationId, text) ->
        writer.WriteString("kind", "Clipboard")
        writer.WriteString("correlationId", correlationId)
        writer.WriteString("operation", "writeText")
        writer.WriteString("text", text)
    | PrintPage correlationId -> writeCapability writer correlationId print (fun w -> w.WriteString("action", "print"))
    | Http(correlationId, method, url, headers, body, timeoutMs, responseHeaders) ->
        writer.WriteString("kind", "Http")
        writer.WriteString("correlationId", correlationId)
        writer.WriteString("method", method)
        writer.WriteString("url", url)

        if not headers.IsEmpty then
            writer.WritePropertyName "headers"
            writer.WriteStartObject()
            headers |> List.iter (fun (name, value) -> writer.WriteString(name, value))
            writer.WriteEndObject()

        body |> Option.iter (fun body -> writer.WriteString("body", body))
        writer.WriteNumber("timeoutMs", timeoutMs)
        writer.WriteString("response", "text")
        writer.WriteString("credentials", "omit")

        if not responseHeaders.IsEmpty then
            writer.WritePropertyName "responseHeaders"
            writer.WriteStartArray()
            responseHeaders |> List.iter writer.WriteStringValue
            writer.WriteEndArray()
    | StorageGet(correlationId, key) ->
        writer.WriteString("kind", "Storage")
        writer.WriteString("operation", "get")
        writer.WriteString("correlationId", correlationId)
        writer.WriteString("key", key)
    | StorageSet(correlationId, key, value) ->
        writer.WriteString("kind", "Storage")
        writer.WriteString("operation", "set")
        writer.WriteString("correlationId", correlationId)
        writer.WriteString("key", key)
        writer.WriteString("value", value)
    | StorageRemove(correlationId, key) ->
        writer.WriteString("kind", "Storage")
        writer.WriteString("operation", "remove")
        writer.WriteString("correlationId", correlationId)
        writer.WriteString("key", key)
    | Host(correlationId, operation, arguments) ->
        writeCapability writer correlationId host (fun w ->
            w.WriteString("operation", operation)
            arguments |> List.iter (fun (name, value) -> w.WriteString(name, value)))

    writer.WriteEndObject()

let private writeOffer (writer: Utf8JsonWriter) (offer: CapabilityOffer) =
    writer.WriteStartObject()
    writer.WriteString("id", offer.Id)
    writer.WriteNumber("version", offer.Version)
    writer.WriteString("fingerprint", offer.Fingerprint)
    writer.WriteEndObject()

/// The engine's complete reply to one kernel message.
let encode (view: View) (requests: Request list) (cancellations: string list) (handshake: Handshake option) =
    write (fun writer ->
        writer.WriteStartObject()
        writer.WritePropertyName "view"
        writeView writer view
        writer.WritePropertyName "effects"
        writer.WriteStartArray()
        requests |> List.iter (writeRequest writer)
        writer.WriteEndArray()
        writer.WritePropertyName "cancellations"
        writer.WriteStartArray()
        cancellations |> List.iter writer.WriteStringValue
        writer.WriteEndArray()

        match handshake with
        | None -> ()
        | Some(Accepted(minor, contract, capabilities)) ->
            writer.WritePropertyName "handshake"
            writer.WriteStartObject()
            writer.WriteString("kind", "Accepted")
            writer.WritePropertyName "protocol"
            writer.WriteStartObject()
            writer.WriteNumber("major", 1)
            writer.WriteNumber("minor", minor)
            writer.WriteEndObject()
            writer.WritePropertyName "contract"
            writeNode writer contract
            writer.WritePropertyName "capabilities"
            writer.WriteStartArray()
            capabilities |> List.iter (writeOffer writer)
            writer.WriteEndArray()
            writer.WriteEndObject()
        | Some(Rejected reason) ->
            writer.WritePropertyName "handshake"
            writer.WriteStartObject()
            writer.WriteString("kind", "Rejected")
            writer.WritePropertyName "reason"
            writer.WriteStartObject()
            reason writer
            writer.WriteEndObject()
            writer.WriteEndObject()

        writer.WriteEndObject())
