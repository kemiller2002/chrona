/// Limen's browser/engine protocol (version 1), as the application tier reads
/// and writes it: the kernel's messages in, the engine's view and handshake
/// answer out. Mechanics only; no Chrona decision is made here.
///
/// The slice negotiates no capability pack and requests only the built-in
/// Storage effect, so the kernel can only send it the handshake, events,
/// storage results and location changes. Anything else is not a message this
/// engine could have caused.
///
/// See the `protocol` export of `@echelon-foundry/limen` (0.7.0).
module Chrona.Application.Limen

open System.Text.Json
open System.Text.Json.Nodes
open Chrona.Engine.View
open Chrona.Engine.KernelSlice
open Chrona.Application.Json

/// A generated contract unit the engine was written against. The kernel
/// offers its own identity in the handshake; the engine accepts only this.
type Contract =
    { Id: string
      Version: int
      Fingerprint: string }

/// Limen Core's contract identity (`limen.core` v1).
let core =
    { Id = "limen.core"
      Version = 1
      Fingerprint = "sha256:2d5e16b7111fc78a319706b9927e4523cfcc519b7a2c9352ca8283ba32d6b71c" }

/// The newest protocol revision this engine speaks (1.4).
[<Literal>]
let ProtocolMinor = 4

[<NoComparison; NoEquality>]
type Inbound =
    | Initialize of protocolVersion: int * effects: string list * handshake: JsonNode option
    /// `isChecked` is the radio or checkbox state (protocol 1.2), when sent.
    | Event of name: string * key: string option * value: string option * isChecked: bool option
    | StorageResult of correlationId: string * outcome: StorageOutcome
    | LocationChanged

/// The kernel offered an effect or capability the engine cannot run without,
/// or answered a request the engine never made.
exception CapabilityFailed of capability: string * problem: string

let private storageOutcome (path: string) (node: JsonNode) =
    match required "kind" path asString node with
    | "Success" ->
        match tryField "value" node with
        | None -> StorageSucceeded None
        | Some value -> StorageSucceeded(Some(asString $"{path}.value" value))
    | "Failure" ->
        match required "reason" path asString node with
        | "unavailable" -> StorageFailed Unavailable
        | "quota-exceeded" -> StorageFailed QuotaExceeded
        | other -> raise (MalformedInput($"{path}.reason", $"a known storage failure reason, not '{other}'"))
    | other -> raise (MalformedInput($"{path}.kind", $"a known storage outcome, not '{other}'"))

/// The slice only ever requests Storage effects, so any other result has no
/// request behind it: a contract violation, not a domain outcome.
let private effectResult (node: JsonNode) =
    let path = "$.result"
    let kind = required "kind" path asString node
    let correlation = required "correlationId" path asString node

    match kind with
    | "StorageResult" -> StorageResult(correlation, storageOutcome $"{path}.outcome" (required "outcome" path asObject node))
    | other -> raise (CapabilityFailed(other, $"Unexpected {other} ({correlation}): this slice only requests Storage effects"))

/// Reads one message from the kernel.
let decode (messageJson: string) =
    let message = parse messageJson |> asObject "$"

    match required "kind" "$" asString message with
    | "Initialize" ->
        Initialize(
            required "protocolVersion" "$" asInt message,
            required "capabilities" "$" asArray message |> List.mapi (fun index item -> asString $"$.capabilities[{index}]" item),
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
    | "LocationChanged" -> LocationChanged
    | "EffectResult" -> effectResult (required "result" "$" asObject message)
    // The handshake selects no optional capability, so the kernel activates
    // none and routes no facts here.
    | "CapabilityFact" ->
        let capability = required "capability" "$" asString message
        raise (CapabilityFailed(capability, $"Unexpected CapabilityFact from {capability}: this slice negotiated no capabilities"))
    | other -> raise (MalformedInput("$.kind", $"a known message kind, not '{other}'"))

// ---------------------------------------------------------------------------
// The handshake answer.
// ---------------------------------------------------------------------------

[<NoComparison; NoEquality>]
type Handshake =
    | Accepted of minor: int * contract: JsonNode
    | Rejected of reason: (Utf8JsonWriter -> unit)

/// A pre-1.1 kernel sends no offer; the engine refuses to run as a legacy
/// engine.
let missing = Rejected(fun writer -> writer.WriteString("kind", "HandshakeMissing"))

/// The engine's answer to the kernel's handshake offer: accept the Core
/// contract, selecting no capability pack, or say precisely why not.
let answer (offer: JsonNode) =
    let path = "$.handshake"
    let protocol = required "protocol" path asObject offer
    let major = required "major" $"{path}.protocol" asInt protocol
    let minor = required "minor" $"{path}.protocol" asInt protocol
    let contract = required "contract" path asObject offer

    let sameCore =
        required "unit" $"{path}.contract" asString contract = core.Id
        && required "version" $"{path}.contract" asInt contract = core.Version
        && required "fingerprint" $"{path}.contract" asString contract = core.Fingerprint

    if major <> 1 then
        Rejected(fun writer ->
            writer.WriteString("kind", "ProtocolUnsupported")
            writer.WritePropertyName "offered"
            writeNode writer protocol)
    elif not sameCore then
        Rejected(fun writer ->
            writer.WriteString("kind", "ContractMismatch")
            writer.WritePropertyName "expected"
            writer.WriteStartObject()
            writer.WriteString("unit", core.Id)
            writer.WriteNumber("version", core.Version)
            writer.WriteString("fingerprint", core.Fingerprint)
            writer.WriteEndObject()
            writer.WritePropertyName "offered"
            writeNode writer contract)
    else
        Accepted(min minor ProtocolMinor, contract)

// ---------------------------------------------------------------------------
// The engine's reply: view, no effects, and (to Initialize only) the handshake.
// ---------------------------------------------------------------------------

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

let private writeEffect (writer: Utf8JsonWriter) (effect: Effect) =
    writer.WriteStartObject()
    writer.WriteString("kind", "Storage")

    match effect with
    | StorageGet(correlationId, key) ->
        writer.WriteString("correlationId", correlationId)
        writer.WriteString("operation", "get")
        writer.WriteString("key", key)
    | StorageSet(correlationId, key, value) ->
        writer.WriteString("correlationId", correlationId)
        writer.WriteString("operation", "set")
        writer.WriteString("key", key)
        writer.WriteString("value", value)

    writer.WriteEndObject()

/// The engine's complete reply to one kernel message.
let encode (view: View) (effects: Effect list) (handshake: Handshake option) =
    write (fun writer ->
        writer.WriteStartObject()
        writer.WritePropertyName "view"
        writeView writer view
        writer.WritePropertyName "effects"
        writer.WriteStartArray()
        effects |> List.iter (writeEffect writer)
        writer.WriteEndArray()
        writer.WritePropertyName "cancellations"
        writer.WriteStartArray()
        writer.WriteEndArray()

        match handshake with
        | None -> ()
        | Some(Accepted(minor, contract)) ->
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
