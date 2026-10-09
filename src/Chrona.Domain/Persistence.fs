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

/// Holds the activities at these paths for review: the records a read from
/// GitHub found edited outside Chrona. Kept with the read cache, so that a
/// start from the cache holds them too (WI-0057).
let holdPaths (paths: Set<string>) (snapshot: Snapshot) =
    let edited =
        snapshot.Activities |> Map.filter (fun _ found -> paths.Contains(RelativePath.render found.Path))

    { snapshot with
        Activities = snapshot.Activities |> Map.filter (fun id _ -> not (edited.ContainsKey id))
        HeldForReview = Map.fold (fun held id found -> Map.add id found held) snapshot.HeldForReview edited
        Problems =
            snapshot.Problems
            @ (edited |> Map.toList |> List.map (fun (_, found) -> ExternalEdit(RelativePath.render found.Path))) }

/// Holds for review every activity whose newest commit was made outside
/// Arca. `histories` is each read record's history, newest first.
let holdExternalEdits (histories: Map<string, HistoryEntry list>) (snapshot: Snapshot) =
    let edited =
        histories
        |> Map.filter (fun _ history ->
            match history with
            | { Origin = CommitOrigin.External } :: _ -> true
            | _ -> false)
        |> Map.keys
        |> Set.ofSeq

    holdPaths edited snapshot

/// A state an outside edit claims that only Chrona's own transitions give:
/// a review or publication state comes with its submission, approval or
/// publication records, and a superseded record with its split or merge.
/// An outside edit is never a way to reach one (41).
let private claimedState (activity: Activity) =
    [ match activity.Record with
      | Superseded _ -> recordStateName activity.Record
      | Recorded
      | Voided _ -> ()
      match activity.Review with
      | Unsubmitted -> ()
      | Submitted -> "Submitted"
      | Approved -> "Approved"
      | Rejected _ -> "Rejected"
      | Reopened -> "Reopened"
      match activity.Publication with
      | NotBillable
      | Unpublished -> ()
      | ReadyForPublication -> "ReadyForPublication"
      | Published -> "Published"
      | InvoicedExternally -> "InvoicedExternally"
      | AdjustmentRequired -> "AdjustmentRequired" ]
    |> List.map (fun state -> ExternalStateClaim(activity.ActivityId, state))

/// What keeps an outside edit out of the ledger: a state only Chrona gives,
/// and, for time it consumes, the rules every recorded activity must pass:
/// a complete classification, known references, and no overlap with the
/// trusted activities.
let private reviewProblems (context: Ledger.CommandContext) (trusted: Activity list) (activity: Activity) =
    claimedState activity
    @ (if consumesTime activity then
           classificationProblems activity.Classification
           @ Reference.assignmentProblems context.References [] activity.Classification
           @ Overlap.check context.Zone trusted activity
       else
           [])

/// Accepts an externally edited activity into the ledger after it passes the
/// rules every recorded activity must (`reviewProblems`).
let acceptExternalEdit (context: Ledger.CommandContext) (activityId: string) (snapshot: Snapshot) : Result<Snapshot, Diagnostic list> =
    match snapshot.HeldForReview.TryFind activityId with
    | None -> Error [ UnknownActivity activityId ]
    | Some found ->
        let trusted = snapshot.Activities |> Map.toList |> List.map (fun (_, other) -> other.Activity)

        match reviewProblems context trusted found.Activity with
        | [] ->
            Ok
                { snapshot with
                    Activities = Map.add activityId found snapshot.Activities
                    HeldForReview = Map.remove activityId snapshot.HeldForReview }
        | problems -> Error problems

/// A person accepting, in the application, a record of theirs edited outside
/// Chrona (41, WI-0035). Only the record's own person, holding
/// `AmendOwnTime`, accepts it, and only after it passes `reviewProblems`
/// against their trusted activities. The result is the record as Chrona
/// stores it from then on: its next revision, changed now, so its newest
/// commit is Chrona's and it is no longer held.
let acceptance
    (context: Ledger.CommandContext)
    (roster: Access.Roster)
    (trusted: Activity list)
    (held: Activity)
    : Result<Activity, Diagnostic list> =
    let ownership =
        [ if held.OrganizationId <> roster.OrganizationId then OrganizationMismatch
          if held.ActorId <> context.Performer then ActorMismatch
          if not (Access.permits roster context.Performer Access.AmendOwnTime) then
              UnauthorizedCapability(Access.capabilityName Access.AmendOwnTime) ]

    match ownership @ reviewProblems context (trusted |> List.filter (fun a -> a.ActivityId <> held.ActivityId)) held with
    | [] ->
        Ok
            { held with
                Revision = held.Revision + 1
                LastChangedAt = max context.At held.LastChangedAt }
    | problems -> Error problems

/// The snapshot once the person accepted these held records, as they
/// reviewed them (`acceptance` decided they may be): they are trusted
/// again, and the external-edit problems that held them are gone. A held
/// record that changed again since it was reviewed stays held.
let release (reviewed: Activity list) (snapshot: Snapshot) =
    let released =
        snapshot.HeldForReview
        |> Map.filter (fun id found -> reviewed |> List.exists (fun activity -> activity.ActivityId = id && activity = found.Activity))

    let paths = released |> Map.toList |> List.map (fun (_, found) -> RelativePath.render found.Path) |> Set.ofList

    { snapshot with
        Activities = Map.fold (fun activities id found -> Map.add id found activities) snapshot.Activities released
        HeldForReview = snapshot.HeldForReview |> Map.filter (fun id _ -> not (released.ContainsKey id))
        Problems =
            snapshot.Problems
            |> List.filter (function
                | ExternalEdit path -> not (paths.Contains path)
                | _ -> true) }

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
