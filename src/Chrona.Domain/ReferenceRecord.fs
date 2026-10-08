/// One reference item (requirements expansion 4) as an Arca record:
/// `records/chrona.reference/<kind>/<id>.json` inside the organization's
/// folder, mutable under its revision. The item's id is stable and stored
/// inside the record.
///
/// Pure.
module Chrona.Domain.ReferenceRecord

open Arca
open Chrona.Domain.Diagnostics
open Chrona.Domain.Reference
open Chrona.Domain.Codec

/// The record type of reference data.
let recordType = Organization.referenceType

/// The reference schema versions this Chrona reads and writes.
let schema =
    { Type = recordType
      OldestReadable = 1
      Current = 1 }

let private kinds = [ Client; Project; Engagement; ActivityType; Tag ]

let private kindOf (name: string) =
    kinds |> List.tryFind (fun kind -> kindName kind = name)

/// The item's record key.
let key (item: Item) : Result<RecordKey, Diagnostic> =
    match RecordId.create item.Id, Segment.create (kindName item.Kind) with
    | Ok id, Ok kind ->
        Ok
            { Type = recordType
              Partition = [ kind ]
              Id = id }
    | _ -> Error(UnstorableRecord(item.Id, "the reference id is not a stable record id"))

/// The item's path inside its organization's folder.
let path (item: Item) : Result<RelativePath, Diagnostic> =
    key item
    |> Result.bind (Layout.recordPath >> Result.mapError (LocationError.describe >> InvalidDataLocation))

/// The folder of one kind of reference data.
let folder (kind: Kind) : Result<RelativePath, Diagnostic> =
    RelativePath.parse (String.concat "/" [ Layout.RecordsFolder; RecordType.value recordType; kindName kind ])
    |> Result.mapError (LocationError.describe >> InvalidDataLocation)

/// Every reference folder.
let folders () = kinds |> List.map folder

/// The item's record body.
let body (item: Item) =
    Json.objectOf
        [ "kind", Json.String(kindName item.Kind)
          "id", Json.String item.Id
          "name", Json.String item.Name
          "status",
          Json.String(
              match item.Status with
              | Active -> "active"
              | Archived -> "archived"
          )
          "ownedBy",
          (match item.Ownership with
           | Owned -> Json.Null
           | MirroredFrom system -> Json.String system)
          "revision", Json.Number(decimal item.Revision) ]

/// The item's canonical stored text.
let encode (item: Item) : Result<string, Diagnostic> =
    key item
    |> Result.bind (fun found ->
        { Id = found.Id
          Type = recordType
          SchemaVersion = schema.Current
          Mutability = Mutability.Mutable
          Body = body item }
        |> Record.encode Record.DefaultMaxBytes
        |> Result.mapError (fun _ -> UnstorableRecord(item.Id, "the reference record is too large")))

/// An item from its record body.
let ofBody (value: Json) : Decoded<Item> =
    closed [ "id"; "kind"; "name"; "ownedBy"; "revision"; "status" ] value
    |> Result.bind (fun () ->
        match text "kind" value, text "id" value, text "name" value, text "status" value, optionalText "ownedBy" value, integer "revision" value with
        | Ok kind, Ok id, Ok name, Ok status, Ok owner, Ok revision ->
            match kindOf kind, status with
            | None, _ -> Error $"'{kind}' is not a kind of reference data"
            | _, ("active" | "archived") when revision < 1 -> Error "'revision' must be at least 1"
            | Some kind, ("active" | "archived") ->
                Ok
                    { Kind = kind
                      Id = id
                      Name = name
                      Status = (if status = "active" then Active else Archived)
                      Ownership =
                        match owner with
                        | Some system -> MirroredFrom system
                        | None -> Owned
                      Revision = revision }
            | _, other -> Error $"'{other}' is not active or archived"
        | Error e, _, _, _, _, _
        | _, Error e, _, _, _, _
        | _, _, Error e, _, _, _
        | _, _, _, Error e, _, _
        | _, _, _, _, Error e, _
        | _, _, _, _, _, Error e -> Error e)
