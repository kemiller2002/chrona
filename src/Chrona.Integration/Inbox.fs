/// Where producers put observations for Chrona, and how a payload found there
/// is read (requirement 18). An observation for an organization is the file
/// `inbox/<sourceSystem>/<observationId>.json` inside the organization's
/// folder; the path is built only from checked segments, so no producer's
/// id can name a place outside its source's inbox.
module Chrona.Integration.Inbox

open System
open Chrona.Integration.Contract

/// The folder producers write under, inside an organization's folder.
[<Literal>]
let Folder = "inbox"

/// The most text one observation may take.
[<Literal>]
let MaxBytes = 65536

/// Whether text is safe as one path segment: letters, digits, `.`, `_` and
/// `-`, not starting with a dot, at most 100 characters.
let isSegment (text: string) =
    not (String.IsNullOrEmpty text)
    && text.Length <= 100
    && text[0] <> '.'
    && text |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '.' || c = '_' || c = '-')

/// The path of a source's observation, relative to the organization's
/// folder; an unsafe segment is refused.
let path (sourceSystem: string) (observationId: string) =
    match isSegment sourceSystem, isSegment observationId with
    | true, true -> Ok $"{Folder}/{sourceSystem}/{observationId}.json"
    | false, _ -> Error $"'{sourceSystem}' cannot name a source's inbox"
    | _, false -> Error $"'{observationId}' cannot name an observation"

/// The source and observation id a path names, when it is an inbox file.
let ofPath (relative: string) =
    match relative.Split '/' with
    | [| folder; source; file |] when folder = Folder && file.EndsWith ".json" ->
        let id = file.Substring(0, file.Length - ".json".Length)
        if isSegment source && isSegment id then Some(source, id) else None
    | _ -> None

/// A payload read from the inbox, as every contract version this release
/// knows reads it. Versions are read side by side: a payload states its
/// version, and one this release does not know is invalid, never guessed.
type Read =
    | V1 of TimeObservationV1.TimeObservation
    | Invalid of Contract.Invalid

/// Reads a payload found at `inbox/<source>/<id>.json`: its envelope, its
/// version, its fields, and that it is filed where it says it belongs.
let read (sourceSystem: string) (observationId: string) (text: string) =
    if Text.Encoding.UTF8.GetByteCount text > MaxBytes then
        Invalid [ $"the payload is larger than {MaxBytes} bytes" ]
    else
        match envelope text with
        | Error reasons -> Invalid reasons
        | Ok({ Contract = contract }, _) when contract <> Name -> Invalid [ $"'{contract}' is not the {Name} contract" ]
        | Ok({ Version = TimeObservationV1.Version }, root) ->
            match TimeObservationV1.parse root with
            | Error reasons -> Invalid reasons
            | Ok observation when observation.SourceSystem <> sourceSystem || observation.ObservationId <> observationId ->
                Invalid [ $"the observation says it is {observation.SourceSystem}/{observation.ObservationId}, but it is filed as {sourceSystem}/{observationId}" ]
            | Ok observation -> V1 observation
        | Ok({ Version = version }, _) -> Invalid [ $"version {version} of {Name} is not one this Chrona reads" ]
