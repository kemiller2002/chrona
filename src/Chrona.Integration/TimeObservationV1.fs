/// Version 1 of the time-observation contract (requirement 18): a producer's
/// statement that a person worked on a project, for an interval or a number
/// of minutes. Read and written explicitly, field by field; an unknown field
/// is refused, never dropped.
module Chrona.Integration.TimeObservationV1

open System
open System.Globalization
open System.Text.Json
open Chrona.Integration.Contract

[<Literal>]
let Version = 1

/// When the work happened.
type Timing =
    | Interval of start: DateTimeOffset * finish: DateTimeOffset
    | Duration of minutes: int

/// Evidence the producer points at: a kind and a reference (a URL or an id).
type Evidence = { Kind: string; Reference: string }

/// One time observation, as version 1 states it.
type TimeObservation =
    { ObservationId: string
      SourceSystem: string
      OrganizationId: string
      ProjectId: string
      ActorId: string option
      WorkItemId: string option
      ExternalUrl: string option
      Timing: Timing
      Description: string option
      Evidence: Evidence list
      ObservedAt: DateTimeOffset }

let private fields =
    set [ "contract"; "version"; "observationId"; "sourceSystem"; "organizationId"; "projectId"; "actorId"; "workItemId"; "externalUrl"; "timing"; "description"; "evidence"; "observedAt" ]

let private instantText (at: DateTimeOffset) =
    at.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK", CultureInfo.InvariantCulture)

/// The observation's canonical text: the fields in the contract's order.
let serialize (observation: TimeObservation) =
    use stream = new IO.MemoryStream()

    (use writer = new Utf8JsonWriter(stream)
     let optional (name: string) (value: string option) =
         match value with
         | Some text -> writer.WriteString(name, text)
         | None -> writer.WriteNull name

     writer.WriteStartObject()
     writer.WriteString("contract", Name)
     writer.WriteNumber("version", Version)
     writer.WriteString("observationId", observation.ObservationId)
     writer.WriteString("sourceSystem", observation.SourceSystem)
     writer.WriteString("organizationId", observation.OrganizationId)
     writer.WriteString("projectId", observation.ProjectId)
     optional "actorId" observation.ActorId
     optional "workItemId" observation.WorkItemId
     optional "externalUrl" observation.ExternalUrl
     writer.WriteStartObject "timing"

     match observation.Timing with
     | Interval(start, finish) ->
         writer.WriteString("kind", "interval")
         writer.WriteString("start", instantText start)
         writer.WriteString("finish", instantText finish)
     | Duration minutes ->
         writer.WriteString("kind", "duration")
         writer.WriteNumber("minutes", minutes)

     writer.WriteEndObject()
     optional "description" observation.Description
     writer.WriteStartArray "evidence"

     for evidence in observation.Evidence do
         writer.WriteStartObject()
         writer.WriteString("kind", evidence.Kind)
         writer.WriteString("reference", evidence.Reference)
         writer.WriteEndObject()

     writer.WriteEndArray()
     writer.WriteString("observedAt", instantText observation.ObservedAt)
     writer.WriteEndObject())

    Text.Encoding.UTF8.GetString(stream.ToArray())

let private text (root: JsonElement) (name: string) =
    match root.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace((stringOf value))) -> Ok((stringOf value))
    | true, _ -> Error $"'{name}' must be non-empty text"
    | false, _ -> Error $"'{name}' is missing"

let private optionalText (root: JsonElement) (name: string) =
    match root.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.Null -> Ok None
    | true, value when value.ValueKind = JsonValueKind.String -> Ok(Some((stringOf value)))
    | true, _ -> Error $"'{name}' must be text or null"
    | false, _ -> Error $"'{name}' is missing (null when there is none)"

let private instant (root: JsonElement) (name: string) =
    text root name
    |> Result.bind (fun value ->
        match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, at when value.Contains 'T' && (value.EndsWith "Z" || value.Contains "+" || value.LastIndexOf '-' > value.IndexOf 'T') -> Ok at
        | _ -> Error $"'{name}' must be an instant with its offset")

let private timing (root: JsonElement) =
    match root.TryGetProperty "timing" with
    | true, value when value.ValueKind = JsonValueKind.Object ->
        let names = value.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

        match text value "kind" with
        | Ok "interval" when names = set [ "kind"; "start"; "finish" ] ->
            match instant value "start", instant value "finish" with
            | Ok start, Ok finish -> Ok(Interval(start, finish))
            | Error e, _
            | _, Error e -> Error $"timing: {e}"
        | Ok "duration" when names = set [ "kind"; "minutes" ] ->
            match value.GetProperty("minutes").TryGetInt32() with
            | true, minutes -> Ok(Duration minutes)
            | _ -> Error "timing: 'minutes' must be a whole number"
        | Ok("interval" | "duration") -> Error "timing: an interval has 'start' and 'finish'; a duration has 'minutes'"
        | Ok other -> Error $"timing: '{other}' is not a kind of timing"
        | Error e -> Error $"timing: {e}"
    | true, _ -> Error "'timing' must be an object"
    | false, _ -> Error "'timing' is missing"

let private evidence (root: JsonElement) =
    match root.TryGetProperty "evidence" with
    | true, value when value.ValueKind = JsonValueKind.Array ->
        value.EnumerateArray()
        |> Seq.map (fun item ->
            if item.ValueKind <> JsonValueKind.Object then
                Error "evidence: each item must be an object"
            elif item.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq <> set [ "kind"; "reference" ] then
                Error "evidence: each item has exactly 'kind' and 'reference'"
            else
                match text item "kind", text item "reference" with
                | Ok kind, Ok reference -> Ok { Kind = kind; Reference = reference }
                | Error e, _
                | _, Error e -> Error $"evidence: {e}")
        |> List.ofSeq
        |> fun results ->
            match results |> List.choose (function Error e -> Some e | Ok _ -> None) with
            | [] -> Ok(results |> List.choose (function Ok v -> Some v | Error _ -> None))
            | problems -> Error(String.concat "; " problems)
    | true, _ -> Error "'evidence' must be a list"
    | false, _ -> Error "'evidence' is missing (an empty list when there is none)"

/// Reads a version 1 payload whose envelope was read: every field checked,
/// every problem reported, nothing guessed.
let parse (root: JsonElement) : Result<TimeObservation, Invalid> =
    let unknown =
        root.EnumerateObject()
        |> Seq.map _.Name
        |> Seq.filter (fields.Contains >> not)
        |> Seq.map (fun name -> $"'{name}' is not a field of version 1")
        |> List.ofSeq

    let observationId = text root "observationId"
    let sourceSystem = text root "sourceSystem"
    let organizationId = text root "organizationId"
    let projectId = text root "projectId"
    let actorId = optionalText root "actorId"
    let workItemId = optionalText root "workItemId"
    let externalUrl = optionalText root "externalUrl"
    let timed = timing root
    let description = optionalText root "description"
    let pointed = evidence root
    let observedAt = instant root "observedAt"

    let problems =
        unknown
        @ ([ observationId |> Result.map ignore
             sourceSystem |> Result.map ignore
             organizationId |> Result.map ignore
             projectId |> Result.map ignore
             actorId |> Result.map ignore
             workItemId |> Result.map ignore
             externalUrl |> Result.map ignore
             timed |> Result.map ignore
             description |> Result.map ignore
             pointed |> Result.map ignore
             observedAt |> Result.map ignore ]
           |> List.choose (function Error e -> Some e | Ok() -> None))

    match problems with
    | [] ->
        let get = function Ok v -> v | Error e -> invalidOp e

        Ok
            { ObservationId = get observationId
              SourceSystem = get sourceSystem
              OrganizationId = get organizationId
              ProjectId = get projectId
              ActorId = get actorId
              WorkItemId = get workItemId
              ExternalUrl = get externalUrl
              Timing = get timed
              Description = get description
              Evidence = get pointed
              ObservedAt = get observedAt }
    | found -> Error found
