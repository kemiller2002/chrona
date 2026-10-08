/// Activities as stored, and the rules for loading and changing them
/// (requirements expansion 21, 22, 39, 41).
///
/// - **Loading** validates everything read (39): each object must be a
///   canonical `chrona.activity` record at a schema version this Chrona reads,
///   with the id and the actor/month folder its path names, and a possible
///   revision; across the objects read, ids are unique, recorded time does
///   not overlap, and split/merge lineage agrees in both directions.
///   Corruption is reported, never ignored: an unusable object is held aside
///   with its revision and never overwritten on a guess.
/// - **Manual edits** (41) are untrusted: a record whose latest commit was not
///   Arca's is held for review and joins the ledger only after it passes the
///   same rules as a recorded activity.
/// - **Changing** (21, 22): one ledger command is one Arca operation, one
///   commit. Each change names the revision last read, so a record changed
///   since is a conflict, never a blind overwrite; activities the command did
///   not touch are not written, so independent changes survive. After a
///   conflict the caller reloads and reruns the command: Chrona's own rules
///   decide again, because a clean Git merge is not proof.
///
/// Pure: the caller performs the reads and the commit through an Arca provider.
module Chrona.Domain.Persistence

open Arca
open Chrona.Domain.Diagnostics
open Chrona.Domain.Activity

/// An activity as read from storage.
type StoredActivity =
    { Activity: Activity
      Path: RelativePath
      Revision: Revision
      /// `sha256:` of the canonical record, to recognize later changes.
      ContentHash: string }

/// What a set of month folders holds, as far as Chrona may trust it.
[<NoComparison>]
type Snapshot =
    { /// Valid activities, by id: the ledger's input.
      Activities: Map<string, StoredActivity>
      /// Valid records last edited outside Chrona, by id, awaiting review (41).
      HeldForReview: Map<string, StoredActivity>
      /// Objects that cannot be used, by namespace-relative path, with the
      /// revision seen. They are never overwritten on a guess.
      Unusable: Map<string, Revision>
      /// Every integrity problem found, in a stable order.
      Problems: Diagnostic list }

let empty =
    { Activities = Map.empty
      HeldForReview = Map.empty
      Unusable = Map.empty
      Problems = [] }

// ---- Loading ------------------------------------------------------------------

let private describeIntegrity =
    function
    | IntegrityFailure.Invalid error -> Organization.describeDecode error
    | IntegrityFailure.IdentityMismatch(expected, found) -> $"the record says it is '{found}' but its file names '{expected}'"
    | IntegrityFailure.TypeMismatch(expected, found) -> $"a {found} record where a {expected} belongs"
    | IntegrityFailure.UnsupportedSchema access ->
        match access with
        | SchemaAccess.UnsupportedFuture(found, newest) -> $"schema version {found} is newer than {newest}"
        | SchemaAccess.UnsupportedPast(found, oldest) -> $"schema version {found} is older than {oldest}"
        | SchemaAccess.ReadWrite
        | SchemaAccess.ReadOnly -> "the schema version is not readable"
    | IntegrityFailure.HashMismatch _ -> "the record changed since it was read"
    | IntegrityFailure.ImmutableChanged _ -> "an immutable record changed"

/// One stored object as an activity, or why it cannot be one.
let private decode (stored: StoredObject) : Result<StoredActivity, Diagnostic> =
    let where = RelativePath.render stored.Path

    match Layout.keyOf stored.Path with
    | Some key when key.Type = ActivityRecord.recordType ->
        Integrity.validate key ActivityRecord.schema Record.DefaultMaxBytes stored
        |> Result.mapError (describeIntegrity >> fun detail -> InvalidStoredRecord(where, detail))
        |> Result.bind (fun valid ->
            ActivityRecord.ofBody valid.Record.Body
            |> Result.mapError (fun detail -> InvalidStoredRecord(where, detail))
            |> Result.map (fun activity ->
                { Activity = activity
                  Path = stored.Path
                  Revision = stored.Revision
                  ContentHash = valid.ContentHash }))
        |> Result.bind (fun found ->
            match ActivityRecord.path found.Activity with
            | Ok expected when expected = stored.Path -> Ok found
            | Ok _ -> Error(MisplacedRecord where)
            | Error diagnostic -> Error diagnostic)
        |> Result.bind (fun found ->
            let activity = found.Activity

            if activity.Revision < 1 || activity.LastChangedAt < activity.CreatedAt || activity.Minutes < 0 then
                Error(ImpossibleRevision activity.ActivityId)
            else
                Ok found)
    | _ -> Error(InvalidStoredRecord(where, "not an activity record's path"))

/// Recorded time that overlaps, once per pair. Voided and superseded records
/// consume no time, so they overlap nothing.
let private overlaps (activities: Activity list) =
    let counted = activities |> List.filter consumesTime

    counted
    |> List.collect (fun activity ->
        Overlap.overlapping counted activity
        |> List.filter (fun other -> activity.ActivityId < other)
        |> List.map (fun other -> StoredOverlap(activity.ActivityId, other)))

/// Lineage must agree both ways where both ends were read: a superseded
/// record names its successors, and each successor names it as a source.
/// An end that was not read (another month) is not judged.
let private lineageProblems (activities: Map<string, Activity>) =
    activities
    |> Map.toList
    |> List.collect (fun (id, activity) ->
        let successors =
            match activity.Record with
            | Superseded by ->
                by
                |> List.choose (fun next ->
                    match activities.TryFind next with
                    | Some successor when not (List.contains id successor.Lineage) -> Some(InvalidLineage(id, next))
                    | _ -> None)
            | Recorded
            | Voided _ -> []

        let sources =
            activity.Lineage
            |> List.choose (fun source ->
                match activities.TryFind source with
                | Some(found: Activity) ->
                    match found.Record with
                    | Superseded by when List.contains id by -> None
                    | _ -> Some(InvalidLineage(id, source))
                | None -> None)

        successors @ sources)
    |> List.distinct

/// Validates the objects read from one or more month folders.
let load (objects: StoredObject list) : Snapshot =
    let decoded = objects |> List.map (fun stored -> stored, decode stored)

    let invalid =
        decoded
        |> List.choose (fun (stored, result) ->
            match result with
            | Error diagnostic -> Some(stored, diagnostic)
            | Ok _ -> None)

    let valid =
        decoded
        |> List.choose (fun (_, result) ->
            match result with
            | Ok found -> Some found
            | Error _ -> None)

    // An id at two paths: neither copy can be trusted to be the record.
    let duplicated =
        valid
        |> List.countBy _.Activity.ActivityId
        |> List.filter (fun (_, count) -> count > 1)
        |> List.map fst
        |> Set.ofList

    let unique = valid |> List.filter (fun found -> not (duplicated.Contains found.Activity.ActivityId))
    let activities = unique |> List.map (fun found -> found.Activity.ActivityId, found) |> Map.ofList
    let plain = activities |> Map.map (fun _ found -> found.Activity)

    { Activities = activities
      HeldForReview = Map.empty
      Unusable =
        (invalid |> List.map (fun (stored, _) -> RelativePath.render stored.Path, stored.Revision))
        @ (valid
           |> List.filter (fun found -> duplicated.Contains found.Activity.ActivityId)
           |> List.map (fun found -> RelativePath.render found.Path, found.Revision))
        |> Map.ofList
      Problems =
        (invalid |> List.map snd)
        @ (duplicated |> Set.toList |> List.map DuplicateActivityId)
        @ overlaps (plain |> Map.toList |> List.map snd)
        @ lineageProblems plain }

/// The listed objects of a month folder worth reading: its record files.
/// A listing the provider cut short is an integrity problem of its own:
/// what was not listed was not validated.
let recordFiles (folder: RelativePath) (listing: Listing) : RelativePath list * Diagnostic list =
    let files = listing.Entries |> List.filter (fun entry -> not entry.IsFolder) |> List.map _.Path

    files,
    (if listing.Complete then
         []
     else
         [ IncompleteRead(RelativePath.render folder) ])

/// The ledger the snapshot's valid activities make. Held and unusable
/// records are not in it.
let ledgerOf (snapshot: Snapshot) : Ledger.Ledger =
    { Activities = snapshot.Activities |> Map.map (fun _ found -> found.Activity)
      Audit = [] }

// ---- Manual edits (41) ---------------------------------------------------------

/// Holds for review every activity whose newest commit was made outside
/// Arca. `histories` is each read record's history, newest first.
let holdExternalEdits (histories: Map<string, HistoryEntry list>) (snapshot: Snapshot) =
    let edited =
        snapshot.Activities
        |> Map.filter (fun _ found ->
            match histories.TryFind(RelativePath.render found.Path) with
            | Some({ Origin = CommitOrigin.External } :: _) -> true
            | _ -> false)

    { snapshot with
        Activities = snapshot.Activities |> Map.filter (fun id _ -> not (edited.ContainsKey id))
        HeldForReview = Map.fold (fun held id found -> Map.add id found held) snapshot.HeldForReview edited
        Problems =
            snapshot.Problems
            @ (edited |> Map.toList |> List.map (fun (_, found) -> ExternalEdit(RelativePath.render found.Path))) }

/// Accepts an externally edited activity into the ledger after it passes the
/// rules every recorded activity must: a complete classification, known
/// references, and no overlap with the trusted activities.
let acceptExternalEdit (context: Ledger.CommandContext) (activityId: string) (snapshot: Snapshot) : Result<Snapshot, Diagnostic list> =
    match snapshot.HeldForReview.TryFind activityId with
    | None -> Error [ UnknownActivity activityId ]
    | Some found ->
        let activity = found.Activity
        let trusted = snapshot.Activities |> Map.toList |> List.map (fun (_, other) -> other.Activity)

        let problems =
            if consumesTime activity then
                classificationProblems activity.Classification
                @ Reference.assignmentProblems context.References [] activity.Classification
                @ Overlap.check context.Zone trusted activity
            else
                []

        match problems with
        | [] ->
            Ok
                { snapshot with
                    Activities = Map.add activityId found snapshot.Activities
                    HeldForReview = Map.remove activityId snapshot.HeldForReview }
        | _ -> Error problems

// ---- Changing ---------------------------------------------------------------------

/// The activities `after` holds that are new or different from `before`.
let changedBetween (before: Ledger.Ledger) (after: Ledger.Ledger) =
    after.Activities
    |> Map.toList
    |> List.map snd
    |> List.filter (fun activity -> before.Activities.TryFind activity.ActivityId <> Some activity)

/// The Arca changes that store `changed` on top of `snapshot`: a create for
/// a new activity, an update at the revision last read, or, when its business
/// date moved to another month, a delete and a create in the same commit.
let changes (snapshot: Snapshot) (changed: Activity list) : Result<Change list, Diagnostic list> =
    let one (activity: Activity) =
        match ActivityRecord.path activity, ActivityRecord.encode activity with
        | Ok target, Ok content ->
            if snapshot.Unusable.ContainsKey(RelativePath.render target) || snapshot.HeldForReview.ContainsKey activity.ActivityId then
                Error [ UnstorableActivity(activity.ActivityId, "its stored record must be reviewed or repaired first") ]
            else
                match snapshot.Activities.TryFind activity.ActivityId with
                | None -> Ok [ Change.Create(target, content) ]
                | Some found when found.Path = target -> Ok [ Change.Update(target, content, found.Revision) ]
                | Some found -> Ok [ Change.Delete(found.Path, found.Revision); Change.Create(target, content) ]
        | Error diagnostic, _
        | _, Error diagnostic -> Error [ diagnostic ]

    let results = changed |> List.sortBy _.ActivityId |> List.map one

    let failures =
        results
        |> List.collect (function
            | Error problems -> problems
            | Ok _ -> [])

    match failures with
    | [] ->
        Ok(
            results
            |> List.collect (function
                | Ok found -> found
                | Error _ -> [])
        )
    | problems -> Error problems

/// One ledger command's result as one Arca operation in the organization's folder.
let operation
    (ns: Namespace)
    (context: Storage.OperationContext)
    (summary: string)
    (snapshot: Snapshot)
    (changed: Activity list)
    : Result<Operation, Diagnostic list> =
    changes snapshot changed |> Result.bind (Storage.operation ns context summary)

/// The snapshot after a commit of `changed` landed with `receipt`.
let committed (changed: Activity list) (receipt: CommitReceipt) (snapshot: Snapshot) =
    changed
    |> List.fold
        (fun current (activity: Activity) ->
            match ActivityRecord.path activity, ActivityRecord.toRecord activity with
            | Ok target, Ok record ->
                match receipt.Revisions.TryFind(RelativePath.render target) with
                | Some(Some revision) ->
                    { current with
                        Activities =
                            Map.add
                                activity.ActivityId
                                { Activity = activity
                                  Path = target
                                  Revision = revision
                                  ContentHash = Record.contentHash record }
                                current.Activities }
                | _ -> current
            | _ -> current)
        snapshot

/// After a conflict: the same command, decided again against freshly loaded
/// state. Changes others made to other activities are in `fresh` and are not
/// written again; a command decided against a revision that has since
/// changed is refused as a revision conflict, for the person to resolve.
let rerun (context: Ledger.CommandContext) (fresh: Snapshot) (command: Ledger.Command) : Result<Activity list, Diagnostic list> =
    let before = ledgerOf fresh
    Ledger.execute context before command |> Result.map (changedBetween before)
