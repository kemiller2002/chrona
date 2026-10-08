/// Everything an organization's folder holds for the application, as far as
/// Chrona may trust it: activities (`Persistence`), reference items and
/// attestations, loaded together, changed together in one commit, and
/// tracked with the revisions last read.
///
/// Pure.
module Chrona.Domain.Stored

open Arca
open Chrona.Domain.Diagnostics

/// A reference item as read from storage.
type StoredReference =
    { Item: Reference.Item
      Path: RelativePath
      Revision: Revision }

[<NoComparison>]
type Stored =
    { Activities: Persistence.Snapshot
      /// Reference items, by `<kind>:<id>`.
      References: Map<string, StoredReference>
      /// Attestations, by namespace-relative path. They are immutable.
      Attestations: Map<string, Review.Attestation>
      /// Integrity problems of reference items and attestations.
      Problems: Diagnostic list }

let empty =
    { Activities = Persistence.empty
      References = Map.empty
      Attestations = Map.empty
      Problems = [] }

/// The key a reference item is tracked by.
let referenceKey (item: Reference.Item) = $"{Reference.kindName item.Kind}:{item.Id}"

/// Every problem found, activities' first.
let problems (stored: Stored) = stored.Activities.Problems @ stored.Problems

let private decodeWith (schema: SchemaSupport) (ofBody: Json -> Codec.Decoded<'a>) (key: RecordKey) (stored: StoredObject) =
    let where = RelativePath.render stored.Path

    Integrity.validate key schema Record.DefaultMaxBytes stored
    |> Result.mapError (fun _ -> InvalidStoredRecord(where, "not a valid record of its type"))
    |> Result.bind (fun valid -> ofBody valid.Record.Body |> Result.mapError (fun detail -> InvalidStoredRecord(where, detail)))

/// Validates the objects read from the organization's folder: activity month
/// folders, reference folders and attestation month folders.
let load (objects: StoredObject list) : Stored =
    let typed (stored: StoredObject) =
        Layout.keyOf stored.Path |> Option.map (fun key -> key, stored)

    let ofType recordType =
        objects |> List.choose typed |> List.filter (fun (key, _) -> key.Type = recordType)

    let activities =
        objects
        |> List.filter (fun stored ->
            match Layout.keyOf stored.Path with
            | Some key -> key.Type = ActivityRecord.recordType
            | None -> false)

    let references, referenceProblems =
        ofType ReferenceRecord.recordType
        |> List.map (fun (key, stored) ->
            decodeWith ReferenceRecord.schema ReferenceRecord.ofBody key stored
            |> Result.bind (fun item ->
                match ReferenceRecord.path item with
                | Ok expected when expected = stored.Path ->
                    Ok
                        { Item = item
                          Path = stored.Path
                          Revision = stored.Revision }
                | Ok _ -> Error(MisplacedRecord(RelativePath.render stored.Path))
                | Error diagnostic -> Error diagnostic))
        |> List.partition Result.isOk

    let attestations, attestationProblems =
        ofType AttestationRecord.recordType
        |> List.map (fun (key, stored) ->
            decodeWith AttestationRecord.schema AttestationRecord.ofBody key stored
            |> Result.bind (fun attestation ->
                match AttestationRecord.path attestation with
                | Ok expected when expected = stored.Path -> Ok(RelativePath.render stored.Path, attestation)
                | Ok _ -> Error(MisplacedRecord(RelativePath.render stored.Path))
                | Error diagnostic -> Error diagnostic))
        |> List.partition Result.isOk

    let foreign =
        objects
        |> List.filter (fun stored ->
            match Layout.keyOf stored.Path with
            | Some key ->
                key.Type <> ActivityRecord.recordType
                && key.Type <> ReferenceRecord.recordType
                && key.Type <> AttestationRecord.recordType
            | None -> true)
        |> List.map (fun stored -> InvalidStoredRecord(RelativePath.render stored.Path, "not a record Chrona keeps here"))

    let oks results =
        results
        |> List.choose (function
            | Ok value -> Some value
            | Error _ -> None)

    let errors results =
        results
        |> List.choose (function
            | Error diagnostic -> Some diagnostic
            | Ok _ -> None)

    { Activities = Persistence.load activities
      References = oks references |> List.map (fun found -> referenceKey found.Item, found) |> Map.ofList
      Attestations = oks attestations |> Map.ofList
      Problems = errors referenceProblems @ errors attestationProblems @ foreign }

/// The organization's reference catalogue, from what was read.
let catalogue (organizationId: string) (stored: Stored) : Reference.Catalogue =
    { OrganizationId = organizationId
      Items = stored.References |> Map.toList |> List.map (fun (_, found) -> (found.Item.Kind, found.Item.Id), found.Item) |> Map.ofList }

/// The attestations read, oldest first.
let attestations (stored: Stored) =
    stored.Attestations |> Map.toList |> List.map snd |> List.sortBy _.At

/// The Arca changes that store a command's records on top of what was read:
/// activities as `Persistence.changes`, reference items created or updated
/// at the revision last read, and attestations created once (an attestation
/// already stored is never written again).
let changes
    (stored: Stored)
    (activities: Activity.Activity list)
    (references: Reference.Item list)
    (attestations: Review.Attestation list)
    : Result<Change list, Diagnostic list> =
    let referenceChange (item: Reference.Item) =
        match ReferenceRecord.path item, ReferenceRecord.encode item with
        | Ok target, Ok content ->
            match stored.References.TryFind(referenceKey item) with
            | Some found -> Ok [ Change.Update(found.Path, content, found.Revision) ]
            | None -> Ok [ Change.Create(target, content) ]
        | Error diagnostic, _
        | _, Error diagnostic -> Error [ diagnostic ]

    let attestationChange (attestation: Review.Attestation) =
        match AttestationRecord.path attestation, AttestationRecord.encode attestation with
        | Ok target, Ok _ when stored.Attestations.ContainsKey(RelativePath.render target) -> Ok []
        | Ok target, Ok content -> Ok [ Change.Create(target, content) ]
        | Error diagnostic, _
        | _, Error diagnostic -> Error [ diagnostic ]

    let results =
        [ Persistence.changes stored.Activities activities ]
        @ (references |> List.map referenceChange)
        @ (attestations |> List.map attestationChange)

    match results |> List.collect (function Error problems -> problems | Ok _ -> []) with
    | [] -> Ok(results |> List.collect (function Ok found -> found | Error _ -> []))
    | problems -> Error problems

/// What was stored after a commit of these records landed with `receipt`.
let committed
    (activities: Activity.Activity list)
    (references: Reference.Item list)
    (attestations: Review.Attestation list)
    (receipt: CommitReceipt)
    (stored: Stored)
    =
    let revisionOf path =
        receipt.Revisions.TryFind(RelativePath.render path) |> Option.flatten

    let references' =
        references
        |> List.fold
            (fun map (item: Reference.Item) ->
                match ReferenceRecord.path item with
                | Ok target ->
                    match revisionOf target with
                    | Some revision -> Map.add (referenceKey item) { Item = item; Path = target; Revision = revision } map
                    | None -> map
                | Error _ -> map)
            stored.References

    let attestations' =
        attestations
        |> List.fold
            (fun map (attestation: Review.Attestation) ->
                match AttestationRecord.path attestation with
                | Ok target -> Map.add (RelativePath.render target) attestation map
                | Error _ -> map)
            stored.Attestations

    { stored with
        Activities = Persistence.committed activities receipt stored.Activities
        References = references'
        Attestations = attestations' }

/// The folders the application reads for an actor and a set of dates: the
/// reference folders, and the activity and attestation month folders.
let folders (actorId: string) (dates: System.DateOnly list) : Result<RelativePath list, Diagnostic> =
    let months = dates |> List.distinctBy (fun date -> date.Year, date.Month) |> List.sort

    let all =
        ReferenceRecord.folders ()
        @ (months |> List.map (ActivityRecord.monthFolder actorId))
        @ (months |> List.map (AttestationRecord.monthFolder actorId))

    all
    |> List.fold (fun state next -> state |> Result.bind (fun found -> next |> Result.map (fun folder -> found @ [ folder ]))) (Ok [])
