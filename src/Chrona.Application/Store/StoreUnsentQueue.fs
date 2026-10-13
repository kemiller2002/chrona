/// The browser's queue of unsent changes (WI-0033, WI-0059): what Arca's
/// LimenQueue asks of the page, what the person is told about it, and whose
/// unsent changes are whose (Arca 0.4.0, ARCA-OFF-007).
module internal Chrona.Application.StoreUnsentQueue

open System
open System.Collections.Generic
open Arca
open Arca.GitHub
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.Model
open Chrona.Application.Bridge
open Chrona.Application.Store
open Chrona.Application.StoreState

/// Browser localStorage through Limen's Storage requests, as Arca's
/// LimenQueue asks for it: the fallback store, and where an older Chrona kept
/// the queue (migrated on first load).
let localStorage (bridge: Bridge) (request: LocalStorageRequest) =
    async {
        let call =
            match request with
            | LocalStorageRequest.Get key -> DeviceGet key
            | LocalStorageRequest.Set(key, value) -> DeviceSet(key, value)
            | LocalStorageRequest.Remove key -> DeviceRemove key

        match! bridge.Call call with
        | Read value -> return LocalStorageOutcome.Success value
        | Done -> return LocalStorageOutcome.Success None
        | Refused "quota-exceeded" -> return LocalStorageOutcome.Failure LocalStorageFailure.QuotaExceeded
        | Refused _
        | Answered _
        | Raw _
        | Missing -> return LocalStorageOutcome.Failure LocalStorageFailure.Unavailable
    }

/// Limen's coordination pack (Web Locks) through the bridge, as Arca's
/// LimenQueue asks for it. Without the pack, the answer is unsupported.
let coordinate (bridge: Bridge) (request: Limen.Contract.Coordination.Types.CoordinationRequest) =
    async {
        match! bridge.Call(Coordinate(Limen.Contract.Coordination.Codec.serializeCoordinationRequest request)) with
        | Raw result ->
            match Limen.Contract.Coordination.Codec.parseCoordinationResult result with
            | Ok decoded -> return decoded
            | Error _ -> return Limen.Contract.Coordination.Types.CoordinationResult.Unsupported
        | _ -> return Limen.Contract.Coordination.Types.CoordinationResult.Unsupported
    }

/// Limen's store pack (IndexedDB, in Chrona's namespace) through the bridge,
/// as Arca's LimenQueue asks for it. Without the pack, the answer is
/// unsupported, and Arca keeps the queue in localStorage instead.
let storeOperation (bridge: Bridge) (request: Limen.Contract.Store.Types.StoreRequest) =
    async {
        match! bridge.Call(StoreOperation(Limen.Contract.Store.Codec.serializeStoreRequest request)) with
        | Raw result ->
            match Limen.Contract.Store.Codec.parseStoreResult result with
            | Ok decoded -> return decoded
            | Error _ -> return Limen.Contract.Store.Types.StoreResult.Unavailable "the store pack's answer could not be read"
        | _ -> return Limen.Contract.Store.Types.StoreResult.Unsupported
    }

/// What Arca's LimenQueue asks of the page: Limen's store and coordination
/// packs, its Storage effect (localStorage), and a clock for diagnostics.
let limenHost (bridge: Bridge) (now: unit -> DateTimeOffset) : Arca.Limen.LimenHost =
    { Store = storeOperation bridge
      Lock = coordinate bridge
      LocalStorage = localStorage bridge
      Now = now }

/// The queue's stores in order of durability (WI-0059, Limen LCP-065):
/// IndexedDB, then localStorage, then this page's memory.
let queueOptions = Arca.Limen.QueueOptions.standard

/// Where a queue Chrona holds is kept, for the person.
let durabilityOf (queue: Arca.Limen.OwnedQueue) =
    match queue.Mode with
    | Arca.Limen.DurabilityMode.IndexedDb -> InIndexedDb
    | Arca.Limen.DurabilityMode.LocalStorage _ -> InLocalStorage
    | Arca.Limen.DurabilityMode.MemoryOnly -> InPage

/// What the person must be told about the kept changes (LCP-062, LCP-066).
let noticeText (notice: Arca.Limen.QueueNotice) =
    match notice with
    | Arca.Limen.QueueNotice.LocalQueueLost ->
        "This browser cleared the unsent changes it kept for Chrona (cleared site data or freed space). Those changes are gone; anything still in this page is sent."
    | Arca.Limen.QueueNotice.LegacyQueuePending entries ->
        let changes = if entries = 1 then "1 earlier unsent change waits" else $"{entries} earlier unsent changes wait"
        $"{changes} in this browser's older storage. They move here once the changes now kept are sent; none is merged or dropped."
    | Arca.Limen.QueueNotice.LegacyQueueUnreadable ->
        "Unsent changes kept in this browser's older storage cannot be read. They were left exactly as they are."
    | Arca.Limen.QueueNotice.LegacyQueueAdopted entries ->
        let changes = if entries = 1 then "1 unsent change" else $"{entries} unsent changes"
        $"{changes} kept in this browser's older storage moved to IndexedDB, unchanged."

/// The notice of a queue just loaded: those at open, then the load's; a
/// legacy queue adopted on load is no longer pending.
let noticeOf (queue: Arca.Limen.OwnedQueue) =
    let notices = queue.Notices @ (queue.Diagnostics()).Notices |> List.distinct

    let adopted =
        notices |> List.exists (function Arca.Limen.QueueNotice.LegacyQueueAdopted _ -> true | _ -> false)

    notices
    |> List.filter (function Arca.Limen.QueueNotice.LegacyQueuePending _ -> not adopted | _ -> true)
    |> List.map noticeText
    |> function
        | [] -> None
        | texts -> Some(String.concat " " texts)

/// Why a queue this tab holds is kept in this page only.
/// Why unsent changes from an earlier version are neither sent nor held.
let earlierNote =
    "Unsent changes from an earlier version of Chrona are in this browser, and they do not say whose they are. Send them as yours, keep them, or discard them under More."

let memoryOnlyNote =
    "This browser offers no storage Chrona can use for unsent changes (IndexedDB and localStorage are unavailable or full), so they live in this page only."

/// The Web Lock that names a folder's queue: one tab holds it.
let lockOf (folder: Namespace) = Arca.Limen.QueueLock.name folder

let describeQueueStore =
    function
    | QueueStoreFailure.Unavailable -> "This browser does not let Chrona keep unsent changes."
    | QueueStoreFailure.QuotaExceeded _ -> "This browser's storage for Chrona is full."
    | QueueStoreFailure.Corrupt _ -> "The unsent changes this browser kept cannot be read; they were left as they are."

/// Whether a queue entry still waits to reach GitHub.
let unsent (entry: QueueEntry) =
    match entry.State with
    | EntryState.Synchronized _
    | EntryState.Abandoned _ -> false
    | _ -> true

/// The stable account of a Chrona actor id ("github:<numeric id>"), never a
/// display name: two people may share one.
let accountOfActor (actorId: string) =
    ActorId.create actorId |> Result.toOption |> Option.map AccountId.ofActor

/// Entries queued without an account id (before Arca 0.4.0) stamped with
/// the account of the actor each one records itself; never with whoever is
/// signed in now. An entry that records no usable actor stays unstamped.
let stamped (queue: OfflineQueue) =
    { queue with
        Entries =
            queue.Entries
            |> List.map (fun entry ->
                match entry.Operation.AccountId, accountOfActor entry.Operation.ActorId with
                | None, Some account -> { entry with Operation = { entry.Operation with AccountId = Some(AccountId.toWire account) } }
                | _ -> entry) }

/// The person signing out or working now, matched by account id only: an
/// entry from an earlier version that names no account is never theirs on a
/// guess (`Legacy = None`).
let accountOf (session: Session) : Arca.Limen.SignOutAccount option =
    accountOfActor session.ActorId |> Option.map (fun account -> { Account = account; Legacy = None })

/// An unsent entry that names no account: from an earlier version, with no
/// actor to attribute it to. The person decides what becomes of it.
let earlier (entry: QueueEntry) =
    unsent entry && entry.Operation.AccountId.IsNone

/// An unsent entry of another account than this session's.
let othersOf (session: Session) (entry: QueueEntry) =
    unsent entry
    && entry.Operation.AccountId.IsSome
    && (match accountOf session with
        | Some who -> not (Arca.Limen.QueueSignOut.belongsToAccount who entry)
        | None -> true)

/// The queue kept in this browser with this page's own unsent changes after
/// it, in order and renumbered after it. A change already kept (the same
/// change, by its idempotency key) is not doubled.
let laidOver (kept: OfflineQueue) (mine: QueueEntry list) =
    let known = kept.Entries |> List.map _.Operation.IdempotencyKey |> Set.ofList
    let added = mine |> List.filter (fun entry -> not (known.Contains entry.Operation.IdempotencyKey))

    { kept with
        NextSequence = kept.NextSequence + int64 added.Length
        Entries = kept.Entries @ (added |> List.mapi (fun index entry -> { entry with Sequence = kept.NextSequence + int64 index })) }

/// The records a queued entry carries.
let recordsOf (folder: Namespace) (entry: QueueEntry) =
    OfflineQueue.operationOf folder entry.Operation
    |> Result.mapError (fun _ -> [])
    |> Result.bind (fun operation -> Stored.changedOf operation.Changes)

/// A queued entry as the engine's request: its id is the operation's
/// idempotency key, which is the request's commit id.
let requestOf (folder: Namespace) (entry: QueueEntry) : StoreRequest option =
    recordsOf folder entry
    |> Result.toOption
    |> Option.map (fun changed ->
        { emptyRequest entry.Operation.IdempotencyKey with
            Activities = changed.Activities
            References = changed.References
            Attestations = changed.Attestations
            Members = changed.Members
            RemovedMembers = changed.Removed
            Audit = changed.Audit
            Periods = changed.Periods
            Reviews = changed.Reviews
            Candidates = changed.Candidates })
