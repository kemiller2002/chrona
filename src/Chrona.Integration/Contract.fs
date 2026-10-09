/// What every version of the time-observation contract shares: its name, the
/// versions this release reads, and the structural reading of a payload's
/// envelope (requirement 18).
module Chrona.Integration.Contract

open System.Text.Json

/// A JSON string's text (never null for a string element).
let stringOf (element: JsonElement) =
    match element.GetString() with
    | null -> ""
    | text -> text

/// The contract's name, as each payload states it.
[<Literal>]
let Name = "chrona.time-observation"

/// Why a payload is not an observation of this contract: each reason a
/// person can read. Structural only; whether the work fits Chrona's rules is
/// decided inside Chrona.
type Invalid = string list

/// The contract and version a payload states.
type Envelope = { Contract: string; Version: int }

/// Reads the envelope every version carries: `contract` and `version`.
let envelope (text: string) : Result<Envelope * JsonElement, Invalid> =
    try
        use document = JsonDocument.Parse(text)
        let root = document.RootElement.Clone()

        if root.ValueKind <> JsonValueKind.Object then
            Error [ "the payload is not a JSON object" ]
        else
            match root.TryGetProperty "contract", root.TryGetProperty "version" with
            | (true, contract), (true, version) when contract.ValueKind = JsonValueKind.String && version.ValueKind = JsonValueKind.Number ->
                match version.TryGetInt32() with
                | true, v -> Ok({ Contract = (stringOf contract); Version = v }, root)
                | _ -> Error [ "'version' is not a whole number" ]
            | (true, _), (true, _) -> Error [ "'contract' must be text and 'version' a number" ]
            | (false, _), _ -> Error [ "'contract' is missing" ]
            | _, (false, _) -> Error [ "'version' is missing" ]
    with :? JsonException as error ->
        Error [ $"the payload is not valid JSON ({error.Message})" ]
