/// Small, total helpers for reading Chrona's stored JSON (Arca's `Json`
/// values). Stored content is untrusted input: every read names the field
/// and says what was wrong, and objects are closed so a field this version
/// does not define is refused rather than ignored.
module Chrona.Domain.Codec

open System
open System.Globalization
open Arca

/// A decoded value, or one sentence saying why the input is not one.
type Decoded<'a> = Result<'a, string>

let field (name: string) (value: Json) : Decoded<Json> =
    match Json.field name value with
    | Some found -> Ok found
    | None -> Error $"'{name}' is missing"

let text name value : Decoded<string> =
    field name value
    |> Result.bind (function
        | Json.String found -> Ok found
        | _ -> Error $"'{name}' is not text")

/// Text, or None for `null`.
let optionalText name value : Decoded<string option> =
    field name value
    |> Result.bind (function
        | Json.Null -> Ok None
        | Json.String found -> Ok(Some found)
        | _ -> Error $"'{name}' is not text or null")

let integer name value : Decoded<int> =
    field name value
    |> Result.bind (function
        | Json.Number number when number = Math.Floor number && number >= decimal Int32.MinValue && number <= decimal Int32.MaxValue ->
            Ok(int number)
        | _ -> Error $"'{name}' is not a whole number")

let flag name value : Decoded<bool> =
    field name value
    |> Result.bind (function
        | Json.Bool found -> Ok found
        | _ -> Error $"'{name}' is not true or false")

/// The members of an object whose every key is one of `names`.
let closed (names: string list) (value: Json) : Decoded<unit> =
    match value with
    | Json.Object members ->
        match members |> List.tryFind (fun (key, _) -> not (List.contains key names)) with
        | Some(key, _) -> Error $"'{key}' is not a field this version reads"
        | None -> Ok()
    | _ -> Error "expected an object"

/// Every element decoded, or the first failure.
let traverse (decode: 'a -> Decoded<'b>) (items: 'a list) : Decoded<'b list> =
    List.foldBack
        (fun item state ->
            match decode item, state with
            | Ok decoded, Ok rest -> Ok(decoded :: rest)
            | Error error, _ -> Error error
            | Ok _, Error error -> Error error)
        items
        (Ok [])

let list name (decode: Json -> Decoded<'b>) value : Decoded<'b list> =
    field name value
    |> Result.bind (function
        | Json.Array items -> traverse decode items
        | _ -> Error $"'{name}' is not a list")

let texts name value : Decoded<string list> =
    list
        name
        (function
        | Json.String found -> Ok found
        | _ -> Error $"'{name}' holds something other than text")
        value

let optional name (decode: Json -> Decoded<'b>) value : Decoded<'b option> =
    field name value
    |> Result.bind (function
        | Json.Null -> Ok None
        | found -> decode found |> Result.map Some)

// ---- Instants, dates and times ------------------------------------------------

[<Literal>]
let private InstantFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz"

/// An instant with its offset, exactly: `2026-10-08T09:30:00.0000000-04:00`.
let instantText (at: DateTimeOffset) = at.ToString(InstantFormat, CultureInfo.InvariantCulture)

let ofInstantText (name: string) (value: string) : Decoded<DateTimeOffset> =
    match DateTimeOffset.TryParseExact(value, InstantFormat, CultureInfo.InvariantCulture, DateTimeStyles.None) with
    | true, at -> Ok at
    | _ -> Error $"'{name}' is not an instant with an offset"

let instant name value = text name value |> Result.bind (ofInstantText name)

let dateText (date: DateOnly) = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

let date name value : Decoded<DateOnly> =
    text name value
    |> Result.bind (fun found ->
        match DateOnly.TryParseExact(found, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, parsed -> Ok parsed
        | _ -> Error $"'{name}' is not a yyyy-MM-dd date")

let timeText (time: TimeOnly) = time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)

let time name value : Decoded<TimeOnly> =
    text name value
    |> Result.bind (fun found ->
        match TimeOnly.TryParseExact(found, "HH:mm:ss.fffffff", CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, parsed -> Ok parsed
        | _ -> Error $"'{name}' is not an HH:mm:ss.fffffff time")

let textOrNull =
    function
    | Some found -> Json.String found
    | None -> Json.Null
