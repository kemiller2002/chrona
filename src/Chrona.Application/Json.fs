/// JSON reading and writing for the application tier.
///
/// Reading is defensive: every message the kernel sends is externally
/// sourced data, so a shape the engine does not expect raises
/// `MalformedInput` (an operational failure Aegis classifies at the boundary)
/// rather than an InvalidOperationException from a JsonNode accessor (which
/// Aegis would treat as a programming defect).
///
/// Writing goes through Utf8JsonWriter directly: no reflection-based
/// serialization, so nothing breaks when the WebAssembly build trims.
module Chrona.Application.Json

open System
open System.Globalization
open System.Buffers
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes

/// Externally sourced JSON did not have the shape the boundary expects.
exception MalformedInput of path: string * expected: string

let parse (text: string) : JsonNode =
    // A JSON `null` document parses to a null reference.
    match Option.ofObj (JsonNode.Parse text) with
    | Some node -> node
    | None -> raise (MalformedInput("$", "a JSON value"))

let private kindOf (node: JsonNode) = node.GetValueKind()

/// The node at `name` in an object, or None when it is absent or null.
let tryField (name: string) (node: JsonNode) : JsonNode option =
    match node with
    | :? JsonObject as o when o.ContainsKey name ->
        Option.ofObj o[name]
    | _ -> None

let asObject (path: string) (node: JsonNode) =
    match node with
    | :? JsonObject as o -> o :> JsonNode
    | _ -> raise (MalformedInput(path, "an object"))

let asArray (path: string) (node: JsonNode) : JsonNode list =
    match node with
    | :? JsonArray as a ->
        a
        |> Seq.mapi (fun index item ->
            match Option.ofObj item with
            | Some value -> value
            | None -> raise (MalformedInput($"{path}[{index}]", "a value, not null")))
        |> Seq.toList
    | _ -> raise (MalformedInput(path, "an array"))

let asString (path: string) (node: JsonNode) =
    if kindOf node = JsonValueKind.String then
        node.GetValue<string>()
    else
        raise (MalformedInput(path, "a string"))

let asInt64 (path: string) (node: JsonNode) =
    match kindOf node with
    | JsonValueKind.Number ->
        match Int64.TryParse(node.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture) with
        | true, value -> value
        | _ -> raise (MalformedInput(path, "an integer"))
    | _ -> raise (MalformedInput(path, "a number"))

let asInt (path: string) (node: JsonNode) =
    let value = asInt64 path node

    if value < int64 Int32.MinValue || value > int64 Int32.MaxValue then
        raise (MalformedInput(path, "a 32-bit integer"))
    else
        int value

let asBool (path: string) (node: JsonNode) =
    match kindOf node with
    | JsonValueKind.True -> true
    | JsonValueKind.False -> false
    | _ -> raise (MalformedInput(path, "a boolean"))

let required (name: string) (path: string) (read: string -> JsonNode -> 'a) (node: JsonNode) =
    let at = $"{path}.{name}"

    match tryField name node with
    | Some value -> read at value
    | None -> raise (MalformedInput(at, "a value"))

let optional (name: string) (path: string) (read: string -> JsonNode -> 'a) (node: JsonNode) =
    tryField name node |> Option.map (read $"{path}.{name}")

/// Writes a document with `write` and returns its text.
let write (write: Utf8JsonWriter -> unit) =
    let buffer = ArrayBufferWriter<byte>()

    (
        use writer = new Utf8JsonWriter(buffer, JsonWriterOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping))
        write writer
    )

    Encoding.UTF8.GetString(buffer.WrittenSpan)

/// Copies a parsed node into a writer (used to echo handshake identities).
let writeNode (writer: Utf8JsonWriter) (node: JsonNode) = node.WriteTo writer
