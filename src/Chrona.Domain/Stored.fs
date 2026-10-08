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

/// A membership as read from storage.
type StoredMember =
    { Membership: Access.Membership
      Path: RelativePath
      Revision: Revision }

[<NoComparison>]
type Stored =
    { Activities: Persistence.Snapshot
      /// Reference items, by `<kind>:<id>`.
      References: Map<string, StoredReference>
      /// Attestations, by namespace-relative path. They are immutable.
      Attestations: Map<string, Review.Attestation>
      /// The organization's members, by principal id.
      Members: Map<string, StoredMember>
      /// Integrity problems of reference items and attestations.
      Problems: Diagnostic list }

let empty =
    { Activities = Persistence.empty
      References = Map.empty
      Attestations = Map.empty
      Members = Map.empty
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

    let members, memberProblems =
        ofType MemberRecord.recordType
        |> List.map (fun (key, stored) ->
            decodeWith MemberRecord.schema MemberRecord.ofBody key stored
            |> Result.bind (fun membership ->
                match MemberRecord.path membership.Principal.PrincipalId with
                | Ok expected when expected = stored.Path ->
                    Ok
                        { Membership = membership
                          Path = stored.Path
                          Revision = stored.Revision }
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
                && key.Type <> MemberRecord.recordType
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
      Members = oks members |> List.map (fun found -> found.Membership.Principal.PrincipalId, found) |> Map.ofList
      Problems = errors referenceProblems @ errors attestationProblems @ errors memberProblems @ foreign }

/// The organization's roster, from what was read.
let roster (organizationId: string) (stored: Stored) : Access.Roster =
    { OrganizationId = organizationId
      Members = stored.Members |> Map.map (fun _ found -> found.Membership) }

/// The organization's reference catalogue, from what was read.
let catalogue (organizationId: string) (stored: Stored) : Reference.Catalogue =
    { OrganizationId = organizationId
      Items = stored.References |> Map.toList |> List.map (fun (_, found) -> (found.Item.Kind, found.Item.Id), found.Item) |> Map.ofList }

/// The attestations read, oldest first.
let attestations (stored: Stored) =
    stored.Attestations |> Map.toList |> List.map snd |> List.sortBy _.At

/// The records one command changes.
type Changed =
    { Activities: Activity.Activity list
      References: Reference.Item list
      Attestations: Review.Attestation list
      /// Memberships admitted or changed.
      Members: Access.Membership list
      /// Principals removed from the roster.
      Removed: string list }

/// Nothing changed.
let nothing =
    { Activities = []
      References = []
      Attestations = []
      Members = []
      Removed = [] }

/// The Arca changes that store a command's records on top of what was read:
/// activities as `Persistence.changes`, reference items and memberships
/// created or updated at the revision last read, removed members deleted
/// at it, and attestations created once (an attestation already stored is
/// never written again).
let changes (stored: Stored) (changed: Changed) : Result<Change list, Diagnostic list> =
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

    let memberChange (membership: Access.Membership) =
        match MemberRecord.path membership.Principal.PrincipalId, MemberRecord.encode membership with
        | Ok target, Ok content ->
            match stored.Members.TryFind membership.Principal.PrincipalId with
            | Some found -> Ok [ Change.Update(found.Path, content, found.Revision) ]
            | None -> Ok [ Change.Create(target, content) ]
        | Error diagnostic, _
        | _, Error diagnostic -> Error [ diagnostic ]

    let removal (principalId: string) =
        match stored.Members.TryFind principalId with
        | Some found -> Ok [ Change.Delete(found.Path, found.Revision) ]
        | None -> Error [ NotAMember(principalId, "") ]

    let results =
        [ Persistence.changes stored.Activities changed.Activities ]
        @ (changed.References |> List.map referenceChange)
        @ (changed.Attestations |> List.map attestationChange)
        @ (changed.Members |> List.map memberChange)
        @ (changed.Removed |> List.map removal)

    match results |> List.collect (function Error problems -> problems | Ok _ -> []) with
    | [] -> Ok(results |> List.collect (function Ok found -> found | Error _ -> []))
    | problems -> Error problems

/// The records an operation's changes carry, as a command's change (for a
/// change queued while offline and read back, WI-0033): what it creates or
/// updates, decoded by record type, and the members it deletes as removals.
/// An activity deleted to move it to another month is carried by its create.
let changedOf (changes: Change list) : Result<Changed, Diagnostic list> =
    let decoded (path: RelativePath) (content: string) =
        let where = RelativePath.render path

        match Layout.keyOf path, Record.decode Record.DefaultMaxBytes content with
        | Some key, Ok record ->
            let body = record.Body
            let invalid detail = InvalidStoredRecord(where, detail)

            if key.Type = ActivityRecord.recordType then
                ActivityRecord.ofBody body |> Result.map (fun a -> { nothing with Activities = [ a ] }) |> Result.mapError invalid
            elif key.Type = ReferenceRecord.recordType then
                ReferenceRecord.ofBody body |> Result.map (fun item -> { nothing with References = [ item ] }) |> Result.mapError invalid
            elif key.Type = AttestationRecord.recordType then
                AttestationRecord.ofBody body |> Result.map (fun a -> { nothing with Attestations = [ a ] }) |> Result.mapError invalid
            elif key.Type = MemberRecord.recordType then
                MemberRecord.ofBody body |> Result.map (fun m -> { nothing with Members = [ m ] }) |> Result.mapError invalid
            else
                Error(invalid "not a record Chrona keeps here")
        | _ -> Error(InvalidStoredRecord(where, "not a valid record"))

    let removal (path: RelativePath) =
        match Layout.keyOf path with
        | Some key when key.Type = MemberRecord.recordType ->
            match ActivityRecord.actorOfSegment (RecordId.value key.Id) with
            | Some principalId -> Ok { nothing with Removed = [ principalId ] }
            | None -> Error(InvalidStoredRecord(RelativePath.render path, "not a member's record id"))
        | Some key when key.Type = ActivityRecord.recordType -> Ok nothing
        | _ -> Error(InvalidStoredRecord(RelativePath.render path, "not a record Chrona removes"))

    // Derived state written with the records (the activity index) is not a record.
    let derived (path: RelativePath) =
        RelativePath.segments path |> List.tryHead |> Option.map Segment.value = Some "derived"

    let parts =
        changes
        |> List.filter (function
            | Change.Create(path, _)
            | Change.Update(path, _, _)
            | Change.Delete(path, _) -> not (derived path))
        |> List.map (function
            | Change.Create(path, content)
            | Change.Update(path, content, _) -> decoded path content
            | Change.Delete(path, _) -> removal path)

    match parts |> List.choose (function Error d -> Some d | Ok _ -> None) with
    | [] ->
        let found = parts |> List.choose (function Ok c -> Some c | Error _ -> None)

        Ok
            { Activities = found |> List.collect _.Activities
              References = found |> List.collect _.References
              Attestations = found |> List.collect _.Attestations
              Members = found |> List.collect _.Members
              Removed = found |> List.collect _.Removed }
    | problems -> Error problems

/// What is stored with changes not yet stored laid over it, as the person
/// sees it while those changes wait to be sent (WI-0033). Records keep the
/// path and revision of what is stored where there is one; a record not
/// stored yet has no revision (`Revision ""`). It is for deciding and
/// showing, never for building a commit.
let overlay (changed: Changed) (stored: Stored) =
    let unsent = Revision ""

    let activities =
        changed.Activities
        |> List.fold
            (fun (snapshot: Persistence.Snapshot) (activity: Activity.Activity) ->
                match ActivityRecord.path activity with
                | Error _ -> snapshot
                | Ok path ->
                    let revision =
                        snapshot.Activities.TryFind activity.ActivityId |> Option.map _.Revision |> Option.defaultValue unsent

                    // An accepted outside edit waiting to be sent is no longer held.
                    let held = snapshot.HeldForReview.TryFind activity.ActivityId

                    { snapshot with
                        Activities =
                            Map.add
                                activity.ActivityId
                                { Activity = activity
                                  Path = path
                                  Revision = held |> Option.map _.Revision |> Option.defaultValue revision
                                  ContentHash = "" }
                                snapshot.Activities
                        HeldForReview = snapshot.HeldForReview.Remove activity.ActivityId
                        Problems =
                            match held with
                            | Some found ->
                                snapshot.Problems
                                |> List.filter (fun problem -> problem <> ExternalEdit(RelativePath.render found.Path))
                            | None -> snapshot.Problems })
            stored.Activities

    let references =
        changed.References
        |> List.fold
            (fun map (item: Reference.Item) ->
                match ReferenceRecord.path item with
                | Error _ -> map
                | Ok path ->
                    let revision =
                        map |> Map.tryFind (referenceKey item) |> Option.map (fun (found: StoredReference) -> found.Revision) |> Option.defaultValue unsent

                    Map.add (referenceKey item) ({ Item = item; Path = path; Revision = revision }: StoredReference) map)
            stored.References

    let members =
        changed.Members
        |> List.fold
            (fun map (membership: Access.Membership) ->
                match MemberRecord.path membership.Principal.PrincipalId with
                | Error _ -> map
                | Ok path ->
                    let id = membership.Principal.PrincipalId
                    let revision = map |> Map.tryFind id |> Option.map (fun (found: StoredMember) -> found.Revision) |> Option.defaultValue unsent
                    Map.add id ({ Membership = membership; Path = path; Revision = revision }: StoredMember) map)
            stored.Members
        |> fun map -> changed.Removed |> List.fold (fun map id -> Map.remove id map) map

    let attestations =
        changed.Attestations
        |> List.fold
            (fun map (attestation: Review.Attestation) ->
                match AttestationRecord.path attestation with
                | Ok path -> Map.add (RelativePath.render path) attestation map
                | Error _ -> map)
            stored.Attestations

    { stored with
        Activities = activities
        References = references
        Members = members
        Attestations = attestations }

/// What was stored after a commit of these records landed with `receipt`.
let committed (changed: Changed) (receipt: CommitReceipt) (stored: Stored) =
    let revisionOf path =
        receipt.Revisions.TryFind(RelativePath.render path) |> Option.flatten

    let references' =
        changed.References
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
        changed.Attestations
        |> List.fold
            (fun map (attestation: Review.Attestation) ->
                match AttestationRecord.path attestation with
                | Ok target -> Map.add (RelativePath.render target) attestation map
                | Error _ -> map)
            stored.Attestations

    let members' =
        changed.Members
        |> List.fold
            (fun map (membership: Access.Membership) ->
                match MemberRecord.path membership.Principal.PrincipalId with
                | Ok target ->
                    match revisionOf target with
                    | Some revision ->
                        Map.add
                            membership.Principal.PrincipalId
                            { Membership = membership
                              Path = target
                              Revision = revision }
                            map
                    | None -> map
                | Error _ -> map)
            stored.Members
        |> fun map -> changed.Removed |> List.fold (fun map principalId -> Map.remove principalId map) map

    { stored with
        Activities = Persistence.committed changed.Activities receipt stored.Activities
        References = references'
        Attestations = attestations'
        Members = members' }

/// The folders the application reads for an actor and a set of dates: the
/// members, the reference folders, and the activity and attestation month
/// folders.
let folders (actorId: string) (dates: System.DateOnly list) : Result<RelativePath list, Diagnostic> =
    let months = dates |> List.distinctBy (fun date -> date.Year, date.Month) |> List.sort

    let all =
        [ MemberRecord.folder () ]
        @ ReferenceRecord.folders ()
        @ (months |> List.map (ActivityRecord.monthFolder actorId))
        @ (months |> List.map (AttestationRecord.monthFolder actorId))

    all
    |> List.fold (fun state next -> state |> Result.bind (fun found -> next |> Result.map (fun folder -> found @ [ folder ]))) (Ok [])
