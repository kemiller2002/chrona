/// Limen's browser/engine protocol (version 1) as the Chrona application
/// reads and writes it: the kernel's messages in, the engine's view, effects
/// and handshake answer out. Mechanics only; no Chrona decision is made here.
///
/// The application requests Navigation (routes), Clipboard (copying an
/// export) and three optional capability packs: `limen.schedule` (the
/// timer's once-a-second wake-up), `limen.environment` (the browser's time
/// zone) and `chrona.print` (the browser's print dialog). It requests no Http
/// or Storage effect, so a result for one of those is a contract violation,
/// not a domain outcome.
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

/// The optional packs the application selects when the kernel offers them.
let wanted = [ schedule; environment; print ]

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
    | Initialize of protocolVersion: int * effects: string list * path: string * hash: string * handshake: JsonNode option
    | Event of name: string * key: string option * value: string option * isChecked: bool option
    | LocationChanged of hash: string
    | NavigationResult of correlationId: string * NavigationOutcome
    | CapabilityResult of correlationId: string * capability: string * CapabilityOutcome
    /// Whether the browser accepted text onto the clipboard.
    | ClipboardResult of correlationId: string * succeeded: bool
    | CapabilityFact of capability: string * fact: JsonNode

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
    | other -> raise (CapabilityFailed(other, $"Unexpected {other} ({correlation}): the application requests no such effect"))

/// Reads one message from the kernel.
let decode (messageJson: string) =
    let message = parse messageJson |> asObject "$"

    match required "kind" "$" asString message with
    | "Initialize" ->
        let path, hash = location "$.location" (required "location" "$" asObject message)
        Initialize(
            required "protocolVersion" "$" asInt message,
            required "capabilities" "$" asArray message |> List.mapi (fun index item -> asString $"$.capabilities[{index}]" item),
            path,
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
