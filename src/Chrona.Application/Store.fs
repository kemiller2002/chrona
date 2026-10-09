/// The store port (DF-CHRONA-2026-0003): what the engine asks to be made
/// durable, made durable through Arca (WI-0032).
///
/// The engine asks to open the organization's records (`OpenStore`) and to
/// commit a command's records (`Store`); this module answers with
/// `StoreOpened`, `StoreUnavailable` and `StoreAnswered`. It runs Arca's
/// provider-neutral `StorageProvider`: in the browser, Arca's GitHub adapter,
/// whose every request is a Limen Http request through the `Bridge` and
/// whose token comes from Fides' token provider (never from Chrona); in
/// tests, Arca's in-memory provider. The rules are the domain's
/// (`Storage`, `Organization`, `Stored`, `Persistence`); this module only
/// sequences them.
///
/// - Opening resolves the repository with the signed-in credential (what it
///   is, whether it is public, whether it may be written), refuses to start
///   production data in a public repository (2.7), initializes Chrona's and
///   the organization's folders when they are new (2.4 to 2.6), opens them
///   only when their manifests match the configuration, reads the reference
///   folders and the month folders the period needs (38), validates
///   everything read (39) and holds records edited outside Chrona (41).
/// - Committing turns the request into one Arca operation, one commit, every
///   change conditioned on the repository state last read. When that state
///   moved, the records are reloaded and Chrona's rules decide the change
///   again (`Reconcile`); what no longer fits is answered as a conflict
///   naming what diverged, for the person to resolve. An unknown outcome is
///   reconciled before anything is sent again (21, 31). Commits run one at
///   a time.
module Chrona.Application.Store

open System
open System.Collections.Generic
open Arca
open Arca.GitHub
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.Model
open Chrona.Application.Bridge

/// Why the records could not be read, for the person: GitHub could not be
/// reached (the read cache may stand in, WI-0057), or it refused (it never
/// may: a refusal is never answered with what was cached).
[<RequireQualifiedAccess>]
type Unopened =
    | Unreachable of reason: string
    | Refused of reason: string

/// The reason, for the person.
let reasonOf =
    function
    | Unopened.Unreachable reason
    | Unopened.Refused reason -> reason

/// What the store needs from where the data lives.
[<NoComparison; NoEquality>]
type Backend =
    { /// The provider serving a location.
      Provider: DataLocation -> StorageProvider
      /// The repository's facts for the signed-in credential: its visibility
      /// and whether it may be read and written (ARCA-AUTH-003, 2.7). A
      /// refusal says why, for the person.
      Resolve: DataLocation -> Async<Result<CapabilitySnapshot, Unopened>> }

/// The store port the application drives.
[<NoComparison; NoEquality>]
type StorePort =
    { Kind: StoreKind
      Open: Deployment.DeploymentConfig -> Session -> DateOnly list -> unit
      /// A listed administrator confirms an organization that has none.
      Confirm: unit -> unit
      Commit: StoreRequest -> unit
      /// Read the month folders these dates need.
      Read: DateOnly list -> unit
      /// Rebuild the activity index from the stored records.
      Rebuild: unit -> unit
      /// Read the producers' inboxes now (WI-0038).
      ReadInboxes: unit -> unit
      /// Send this account's unsent changes now.
      SendNow: unit -> unit
      /// Discard this account's unsent changes from this device.
      Discard: unit -> unit
      /// Take this browser's unsent changes over from the tab holding them
      /// ("use this tab instead"; WI-0059).
      TakeOver: unit -> unit
      /// Send the unsent changes from an earlier version, naming no one, as
      /// this person's (the person said they are theirs).
      SendEarlier: unit -> unit
      /// Discard the unsent changes from an earlier version that cannot have
      /// landed (the person confirmed).
      DiscardEarlier: unit -> unit
      /// Ask again to hold them: the tab that held them may have closed.
      Claim: unit -> unit
      /// The browser closed the queue's database under the page: open it again.
      Reconnect: unit -> unit
      /// This tab no longer holds this browser's unsent changes.
      Lost: unit -> unit
      /// The account leaves this device: its read cache goes unless its
      /// unsent work was kept here (WI-0057, Arca's SignOut.plan).
      SignedOut: Update.UnsentChoice -> int -> unit
      /// Clear Chrona's data from this browser, for every account.
      ClearDevice: unit -> unit }

/// A store that keeps nothing: every commit is acknowledged at once. For a
/// deployment that configures no location.
let inMemory (bridge: Bridge) : StorePort =
    { Kind = InMemory
      Open = fun _ _ _ -> bridge.Start(async { return [ Update.StoreUnavailable "This deployment stores nothing." ] })
      Confirm = fun () -> ()
      Commit = fun request -> bridge.Start(async { return [ Update.StoreAnswered(request.CommitId, Committed) ] })
      Read = fun _ -> ()
      Rebuild = fun () -> ()
      ReadInboxes = fun () -> ()
      SendNow = fun () -> ()
      Discard = fun () -> bridge.Start(async { return [ Update.UnsentDiscarded 0 ] })
      TakeOver = fun () -> ()
      SendEarlier = fun () -> ()
      DiscardEarlier = fun () -> ()
      Claim = fun () -> ()
      Reconnect = fun () -> ()
      Lost = fun () -> ()
      SignedOut = fun _ _ -> ()
      ClearDevice = fun () -> bridge.Start(async { return [ Update.DeviceCleared(Ok()) ] }) }

// ---- GitHub, through the bridge ----------------------------------------------------

let private methodName =
    function
    | HttpMethod.Get -> "GET"
    | HttpMethod.Post -> "POST"
    | HttpMethod.Patch -> "PATCH"
    | HttpMethod.Put -> "PUT"
    | HttpMethod.Delete -> "DELETE"

let private describeResolve =
    function
    | ResolveError.CredentialUnavailable _ -> Unopened.Refused "You are not signed in to GitHub any more. Sign in again."
    | ResolveError.CredentialRejected -> Unopened.Refused "GitHub refused your sign-in. Sign in again."
    | ResolveError.RepositoryNotFound repository -> Unopened.Refused $"Your GitHub account cannot see {repository}."
    | ResolveError.Call _ -> Unopened.Unreachable "GitHub could not be reached."

/// Arca's GitHub adapter, its requests sent as Limen Http requests, its waits
/// as `limen.schedule` timeouts, its tokens from Fides. A 401 is reported to
/// Fides, which then asks the person to sign in again.
let gitHub (bridge: Bridge) (tokens: unit -> TokenProvider option) (unauthorized: unit -> Async<unit>) : Backend =
    let host: Host =
        { Send =
            fun authorized ->
                async {
                    let request = authorized.Request
                    let credential = authorized.Credential |> Option.map AccessToken.authorization |> Option.toList

                    match!
                        bridge.Call(
                            Http(methodName request.Method, request.Url, request.Headers @ credential, request.Body, request.TimeoutMs, request.ResponseHeaders)
                        )
                    with
                    | Answered(AppProtocol.HttpSucceeded(status, headers, body)) ->
                        if status = 401 && authorized.Credential.IsSome then
                            do! unauthorized ()

                        return HttpOutcome.Response(status, Http.normalizeHeaders headers, body)
                    | Answered(AppProtocol.HttpFailed reason) ->
                        return
                            HttpOutcome.Failed(
                                match reason with
                                | "aborted" -> HttpFailure.Aborted
                                | "invalid-response" -> HttpFailure.InvalidResponse
                                | "too-large" -> HttpFailure.TooLarge
                                | _ -> HttpFailure.Network
                            )
                    | Answered AppProtocol.HttpCancelled -> return HttpOutcome.Cancelled
                    | Answered(AppProtocol.HttpUnknown reason) ->
                        return
                            HttpOutcome.OutcomeUnknown(
                                if reason = "connection-lost" then UnknownReason.ConnectionLost else UnknownReason.TimeoutAfterDispatch
                            )
                    | Read _
                    | Done
                    | Refused _
                    | Raw _
                    | Missing -> return HttpOutcome.Failed HttpFailure.InvalidResponse
                }
          Wait = fun delay -> bridge.Call(Sleep(int delay.TotalMilliseconds)) |> Async.Ignore
          Tokens =
            fun () ->
                match tokens () with
                | Some provider -> provider ()
                | None -> async.Return(Error TokenUnavailable.NoToken) }

    { Provider = fun location -> GitHubStorage.provider host (GitHubConfig.create location)
      Resolve =
        fun location ->
            async {
                match! Conversation.run host (Arca.GitHub.Identity.resolve (GitHubConfig.create location)) with
                | Ok snapshot -> return Ok snapshot
                | Error error -> return Error(describeResolve error)
            } }

// ---- The Arca store -----------------------------------------------------------------

/// The activity index as the store keeps it (WI-0034): derived state,
/// written in the same commit as the records it covers.
[<NoComparison; NoEquality>]
type private Indexed =
    { /// The index, when it is kept: None until it is built, or once it
      /// could not be kept.
      Index: DerivedIndex option
      /// Its stored revision; None when it is not stored yet.
      Revision: Revision option
      /// Why it is not kept, or where it is known to differ from the records.
      Note: string option }

/// What a write is conditioned on (Arca 0.4.0, ARCA-CON-005): the state of
/// Chrona's own folder, so another application's commit elsewhere in a shared
/// repository moves nothing under it. After a commit whose folder state
/// cannot be observed at exactly the commit (someone committed since), the
/// next write falls back to the repository's state at the commit.
[<NoComparison; NoEquality>]
type private Condition =
    | InNamespace of NamespaceState
    | InRepository of ChangeToken

/// The repository's change token a condition was observed at.
let private changeTokenOf =
    function
    | InNamespace state -> state.RepositoryToken
    | InRepository token -> token

/// Holds an operation to the condition.
let private require (condition: Condition) (operation: Operation) =
    match condition with
    | InNamespace state -> Operation.requireNamespaceToken state.NamespaceToken operation
    | InRepository token -> Operation.requireChangeToken token operation

/// What was read under the condition, for the read cache.
let private freshOf (condition: Condition) (objects: StoredObject list) =
    match condition with
    | InNamespace state -> Fresh.read state objects
    | InRepository token -> Fresh.readRepositoryWide token objects

/// What the records shown were read at.
[<NoComparison; NoEquality>]
type private Basis =
    /// Read from GitHub at this state of the folder: every write is
    /// conditioned on it.
    | ReadAt of Condition
    /// Shown from the read cache, as GitHub last gave them at this time.
    /// Nothing is conditioned on a cached value (LCP-085): changes made now
    /// are queued and decided again on what GitHub holds before they are
    /// sent.
    | CachedAt of DateTimeOffset

/// The organization's records as opened.
[<NoComparison; NoEquality>]
type private Opened =
    { Session: Session
      Folder: Namespace
      Provider: StorageProvider
      Name: string
      /// What the records were read at: GitHub's change token (21), or the
      /// read cache while GitHub cannot be reached (WI-0057).
      Basis: Basis
      Stored: Stored.Stored
      /// Each folder's records as GitHub holds them at the basis's token, for
      /// the read cache; empty while shown from the cache.
      Read: Map<string, StoredObject list>
      /// The months read so far.
      Months: Set<int * int>
      /// The organization as the deployment configures it.
      Organization: Deployment.OrganizationConfig
      /// This page's unsent changes, in order: Arca's offline queue (WI-0033).
      Queue: OfflineQueue
      /// The queue this tab holds in this browser, when it holds one it can
      /// keep (WI-0059: Arca's LimenQueue, in IndexedDB where it can be).
      Owned: Arca.Limen.OwnedQueue option
      /// Which tab holds this browser's queue (WI-0067).
      Holder: QueueHolder
      /// Why the queue is not kept in this browser, for the person.
      Note: string option
      /// What the person must be told about the kept changes (LCP-062, LCP-066).
      Notice: string option
      /// The activity index as last read or written (WI-0034).
      Index: Indexed }

/// What became of one inbox file (WI-0038).
type private Taken =
    /// Received: its candidate and receipt are stored, the file removed.
    | TakenIn
    /// Not an observation: its receipt says why, and the file stays.
    | NotObservation of reasons: string list

let private describeFailure =
    function
    | StorageFailure.Refused(WriteRefusal.CredentialUnavailable _) -> "You are not signed in to GitHub any more. Sign in again; nothing was saved."
    | StorageFailure.Refused WriteRefusal.ReadOnlyAccess -> "Your GitHub account can read this repository but not write to it."
    | StorageFailure.Refused WriteRefusal.BranchProtected -> "The data branch does not accept direct changes."
    | StorageFailure.Refused WriteRefusal.RepositoryArchived -> "The data repository is archived."
    | StorageFailure.Refused(WriteRefusal.RepositoryIdentityChanged _) -> "The configured repository is now a different repository."
    | StorageFailure.Refused(WriteRefusal.CapabilityUnavailable _) -> "The store cannot make this change."
    | StorageFailure.Conflicted _ -> "Something changed elsewhere."
    | StorageFailure.OutcomeUnknown _ -> "GitHub did not say whether the change was saved."
    | StorageFailure.ObjectTooLarge(path, _, _) -> $"{path} is too large to store."
    | StorageFailure.StaleChangeToken _ -> "The repository changed while saving."
    | StorageFailure.StaleNamespaceToken _ -> "The organization's records changed while saving."
    | StorageFailure.RateLimited _ -> "GitHub's rate limit is used up for now. Try again shortly."
    | StorageFailure.WrongLocation _ -> "The store is not serving the configured repository."
    | StorageFailure.IntegrityRefused(path, _) -> $"{path} could not be changed safely: it is not a valid record."
    | StorageFailure.ProviderFailed(_, _, detail) -> $"GitHub could not be reached ({detail})."

/// A failure to read, as a reason the records could not be opened.
let private unopened (failure: StorageFailure) =
    match failure with
    | StorageFailure.ProviderFailed(_, true, _)
    | StorageFailure.RateLimited _ -> Unopened.Unreachable(describeFailure failure)
    | _ -> Unopened.Refused(describeFailure failure)

let private describeAll (diagnostics: Diagnostic list) =
    diagnostics |> List.map code |> List.distinct |> String.concat ", "

let private operationContext (session: Session) (key: string) (at: DateTimeOffset) : Result<Storage.OperationContext, Diagnostic list> =
    match ActorId.create session.ActorId, CorrelationId.create key, IdempotencyKey.create key with
    | Ok actor, Ok correlation, Ok idempotency ->
        Ok
            { Actor = { Kind = ActorKind.Human; Id = actor }
              ProviderIdentity = Some session.DisplayName
              CorrelationId = correlation
              IdempotencyKey = idempotency
              At = at }
    | _ -> Error [ StorageOperationRefused "the session's actor or the change's id is not a valid identifier" ]

let private monthOf (date: DateOnly) = date.Year, date.Month
let private firstOf (year: int, month: int) = DateOnly(year, month, 1)

/// The records a request asks to store.
let private changeOf (request: StoreRequest) : Stored.Changed =
    { Activities = request.Activities
      References = request.References
      Attestations = request.Attestations
      Members = request.Members
      Removed = request.RemovedMembers
      Audit = request.Audit
      Periods = request.Periods
      Reviews = request.Reviews
      Candidates = request.Candidates
      Receipts = []
      Consumed = [] }

/// Browser localStorage through Limen's Storage requests, as Arca's
/// LimenQueue asks for it: the fallback store, and where an older Chrona kept
/// the queue (migrated on first load).
let private localStorage (bridge: Bridge) (request: LocalStorageRequest) =
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
let private coordinate (bridge: Bridge) (request: Limen.Contract.Coordination.Types.CoordinationRequest) =
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
let private storeOperation (bridge: Bridge) (request: Limen.Contract.Store.Types.StoreRequest) =
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
let private limenHost (bridge: Bridge) (now: unit -> DateTimeOffset) : Arca.Limen.LimenHost =
    { Store = storeOperation bridge
      Lock = coordinate bridge
      LocalStorage = localStorage bridge
      Now = now }

/// The queue's stores in order of durability (WI-0059, Limen LCP-065):
/// IndexedDB, then localStorage, then this page's memory.
let private queueOptions = Arca.Limen.QueueOptions.standard

/// The version of Chrona's records in the read cache (WI-0057): raised when
/// a record format changes, so an entry of the old one is never shown.
[<Literal>]
let private CacheSchema = 1

/// The cache partition that lists a folder's records held for review.
[<Literal>]
let private HeldPrefix = "held-for-review:"

/// The cache partition that holds the activity index.
[<Literal>]
let private IndexPartition = "derived:activity-index"

/// Where a queue Chrona holds is kept, for the person.
let private durabilityOf (queue: Arca.Limen.OwnedQueue) =
    match queue.Mode with
    | Arca.Limen.DurabilityMode.IndexedDb -> InIndexedDb
    | Arca.Limen.DurabilityMode.LocalStorage _ -> InLocalStorage
    | Arca.Limen.DurabilityMode.MemoryOnly -> InPage

/// What the person must be told about the kept changes (LCP-062, LCP-066).
let private noticeText (notice: Arca.Limen.QueueNotice) =
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
let private noticeOf (queue: Arca.Limen.OwnedQueue) =
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
let private earlierNote =
    "Unsent changes from an earlier version of Chrona are in this browser, and they do not say whose they are. Send them as yours, keep them, or discard them under More."

let private memoryOnlyNote =
    "This browser offers no storage Chrona can use for unsent changes (IndexedDB and localStorage are unavailable or full), so they live in this page only."

/// The Web Lock that names a folder's queue: one tab holds it.
let private lockOf (folder: Namespace) = Arca.Limen.QueueLock.name folder

let private describeQueueStore =
    function
    | QueueStoreFailure.Unavailable -> "This browser does not let Chrona keep unsent changes."
    | QueueStoreFailure.QuotaExceeded _ -> "This browser's storage for Chrona is full."
    | QueueStoreFailure.Corrupt _ -> "The unsent changes this browser kept cannot be read; they were left as they are."

/// Whether a queue entry still waits to reach GitHub.
let private unsent (entry: QueueEntry) =
    match entry.State with
    | EntryState.Synchronized _
    | EntryState.Abandoned _ -> false
    | _ -> true

// ---- Whose unsent changes (Arca 0.4.0, ARCA-OFF-007) -------------------------------

/// The stable account of a Chrona actor id ("github:<numeric id>"), never a
/// display name: two people may share one.
let private accountOfActor (actorId: string) =
    ActorId.create actorId |> Result.toOption |> Option.map AccountId.ofActor

/// Entries queued without an account id (before Arca 0.4.0, or rebuilt by
/// Arca 0.4.0's `revise`, which drops it) stamped with the account of the
/// actor each one records itself; never with whoever is signed in now. An
/// entry that records no usable actor stays unstamped.
let private stamped (queue: OfflineQueue) =
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
let private accountOf (session: Session) : Arca.Limen.SignOutAccount option =
    accountOfActor session.ActorId |> Option.map (fun account -> { Account = account; Legacy = None })

/// An unsent entry that names no account: from an earlier version, with no
/// actor to attribute it to. The person decides what becomes of it.
let private earlier (entry: QueueEntry) =
    unsent entry && entry.Operation.AccountId.IsNone

/// An unsent entry of another account than this session's.
let private othersOf (session: Session) (entry: QueueEntry) =
    unsent entry
    && entry.Operation.AccountId.IsSome
    && (match accountOf session with
        | Some who -> not (Arca.Limen.QueueSignOut.belongsToAccount who entry)
        | None -> true)

/// The queue kept in this browser with this page's own unsent changes after
/// it, in order and renumbered after it. A change already kept (the same
/// change, by its idempotency key) is not doubled.
let private laidOver (kept: OfflineQueue) (mine: QueueEntry list) =
    let known = kept.Entries |> List.map _.Operation.IdempotencyKey |> Set.ofList
    let added = mine |> List.filter (fun entry -> not (known.Contains entry.Operation.IdempotencyKey))

    { kept with
        NextSequence = kept.NextSequence + int64 added.Length
        Entries = kept.Entries @ (added |> List.mapi (fun index entry -> { entry with Sequence = kept.NextSequence + int64 index })) }

/// The records a queued entry carries.
let private recordsOf (folder: Namespace) (entry: QueueEntry) =
    OfflineQueue.operationOf folder entry.Operation
    |> Result.mapError (fun _ -> [])
    |> Result.bind (fun operation -> Stored.changedOf operation.Changes)

/// A queued entry as the engine's request: its id is the operation's
/// idempotency key, which is the request's commit id.
let private requestOf (folder: Namespace) (entry: QueueEntry) : StoreRequest option =
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

/// The most text the activity index may take; past it, the index is no
/// longer kept with each change, and is rebuilt on request.
[<Literal>]
let IndexBudget = 512000

/// The change that keeps the activity index current with `changes`, when it
/// is kept and they change it.
let private indexChange (indexed: Indexed) (changes: Change list) =
    match indexed.Index with
    | None -> []
    | Some index ->
        let next = ActivityIndex.apply changes index

        if next.Entries = index.Entries && indexed.Revision.IsSome then
            []
        else
            let text = Derived.encode next

            if text.Length > IndexBudget then
                []
            else
                match indexed.Revision with
                | Some revision -> [ Change.Update(ActivityIndex.path, text, revision) ]
                | None -> [ Change.Create(ActivityIndex.path, text) ]

/// The index after an operation landed: what it wrote, or, when it changed
/// activities without writing the index, no longer kept.
let private indexAfter (indexed: Indexed) (operation: Operation) (receipt: CommitReceipt) =
    let written =
        operation.Changes
        |> List.tryPick (function
            | Change.Create(path, content)
            | Change.Update(path, content, _) when path = ActivityIndex.path -> Some content
            | _ -> None)

    let touchesActivities =
        operation.Changes
        |> List.exists (function
            | Change.Create(path, _)
            | Change.Update(path, _, _)
            | Change.Delete(path, _) -> Layout.keyOf path |> Option.exists (fun key -> key.Type = ActivityRecord.recordType))

    match written |> Option.map Derived.decode, receipt.Revisions.TryFind(RelativePath.render ActivityIndex.path) with
    | Some(Ok index), Some(Some revision) ->
        { indexed with
            Index = Some index
            Revision = Some revision }
    | _ when touchesActivities && indexed.Index.IsSome ->
        { Index = None
          Revision = None
          Note = Some "The activity index grew too large to keep with every change. Rebuild it to bring it up to date." }
    | _ -> indexed

/// The longest wait between attempts to reach GitHub.
[<Literal>]
let MaxRetryMs = 60000

/// How many inbox files one pass takes in (WI-0038): a pass holds the
/// store's turn, so a person's own changes wait at most this many commits.
[<Literal>]
let InboxBatch = 20

/// The first wait after GitHub could not be reached.
[<Literal>]
let FirstRetryMs = 5000

/// The Arca store over a backend. `now` is the clock; `newKey` mints the
/// idempotency keys of the operations the store starts itself. It keeps
/// commits in memory until the engine opens storage, which it does only for
/// a deployment that configures a location.
let arca (bridge: Bridge) (backend: Backend) (now: unit -> DateTimeOffset) (newKey: string -> string) : StorePort =
    let mutable opened: Opened option = None
    /// Records read but held until a listed administrator confirms.
    let mutable pending: Opened option = None
    let jobs = Queue<unit -> Async<Update.Msg list>>()
    let mutable busy = false
    /// Changes this tab kept that another tab took over, not told yet.
    let handed = ResizeArray<string>()

    /// Runs the queued jobs one at a time; each job's messages go to the
    /// engine as it finishes.
    let rec run () =
        async {
            if jobs.Count = 0 then
                busy <- false
            else
                let job = jobs.Dequeue()

                let! messages =
                    async {
                        try
                            return! job ()
                        with error ->
                            jobs.Clear()
                            busy <- false
                            return raise error
                    }

                let messages =
                    if handed.Count = 0 then
                        messages
                    else
                        let ids = List.ofSeq handed
                        handed.Clear()
                        messages @ [ Update.UnsentHandedOver ids ]

                bridge.Emit messages
                return! run ()
        }

    let serial (job: unit -> Async<Update.Msg list>) =
        jobs.Enqueue job

        if not busy then
            busy <- true

            bridge.Start(
                async {
                    do! run ()
                    return []
                }
            )

    /// What this page asks of Limen: the store and coordination packs.
    let host = limenHost bridge now

    /// The read cache in IndexedDB (WI-0057), once opened: None where the
    /// browser has none, and the records then simply need GitHub to open.
    let mutable cache: Arca.Limen.IndexedDbCache option option = None

    let readCache () =
        async {
            match cache with
            | Some opened -> return opened
            | None ->
                let! result = Arca.Limen.IndexedDbReadCache.openCache host Arca.Limen.IndexedDbReadCache.DefaultBudget
                let opened = Result.toOption result
                cache <- Some opened
                return opened
        }

    /// The queue's stores in order, freeing the read cache's space when a
    /// save meets the browser's quota (LCP-087).
    let queueOptions () =
        async {
            let! cached = readCache ()
            return { queueOptions with FreeSpace = cached |> Option.map _.FreeSpace }
        }

    /// Keeps what was just read from GitHub in the read cache, one entry per
    /// partition, best-effort: a failure never fails the read (LCP-083).
    let keepRead (account: string) (folder: Namespace) (token: Condition) (partitions: (string * StoredObject list) list) =
        async {
            match! readCache () with
            | None -> ()
            | Some cached ->
                for partition, objects in partitions do
                    match ReadCache.key account folder partition with
                    | Error _ -> ()
                    | Ok key -> do! cached.Keep(ReadCache.entry key CacheSchema (now ()) (freshOf token objects))
        }

    /// Reads these folders: each folder's records.
    let readFolders (provider: StorageProvider) (folder: Namespace) (paths: Result<RelativePath list, Diagnostic>) =
        async {
            match paths with
            | Error diagnostic -> return Error(Unopened.Refused(code diagnostic))
            | Ok folders ->
                let mutable read = []
                let mutable problems = []
                let mutable failure = None

                for path in folders do
                    if failure.IsNone then
                        match! provider.List folder path with
                        | Error error -> failure <- Some(unopened error)
                        | Ok listing ->
                            let files, incomplete = Persistence.recordFiles path listing
                            problems <- problems @ incomplete
                            let mutable objects = []

                            for file in files do
                                if failure.IsNone then
                                    match! provider.Read folder file with
                                    | Ok(ReadOutcome.Found found) -> objects <- objects @ [ found ]
                                    // Chrona erases nothing; a record erased by
                                    // another tool is gone, not a problem.
                                    | Ok ReadOutcome.Absent
                                    | Ok(ReadOutcome.Erased _) -> ()
                                    | Error error -> failure <- Some(unopened error)

                            read <- read @ [ path, objects ]

                match failure with
                | Some reason -> return Error reason
                | None -> return Ok(read, problems)
        }

    /// Reads the folders these dates need for this person.
    let read (provider: StorageProvider) (folder: Namespace) (actorId: string) (dates: DateOnly list) =
        readFolders provider folder (Stored.folders actorId dates)

    /// For someone who approves time where approval is required: the other
    /// members' review steps of periods in these months, and the time their
    /// submissions waiting for approval cover (WI-0036).
    let readReviewing (provider: StorageProvider) (folder: Namespace) (actorId: string) (dates: DateOnly list) (stored: Stored.Stored) =
        async {
            let mayApprove =
                stored.Members.TryFind actorId
                |> Option.exists (fun found ->
                    found.Membership.Capabilities.Contains Access.ApproveTime
                    || found.Membership.Capabilities.Contains Access.RejectTime)

            let approvalRequired = stored.Periods |> Option.exists _.Periods.Config.ApprovalRequired
            let others = stored.Members |> Map.toList |> List.map fst |> List.filter ((<>) actorId)

            if not mayApprove || not approvalRequired || others.IsEmpty then
                return Ok([], [])
            else
                let months =
                    dates
                    |> List.collect (fun date -> [ date.AddMonths -1; date ])
                    |> List.map (fun date -> DateOnly(date.Year, date.Month, 1))
                    |> List.distinct

                let all (paths: Result<RelativePath, Diagnostic> list) =
                    paths |> List.fold (fun state next -> state |> Result.bind (fun found -> next |> Result.map (fun one -> found @ [ one ]))) (Ok [])

                match! readFolders provider folder (all [ for other in others do for month in months -> ReviewRecord.monthFolder other month ]) with
                | Error reason -> return Error reason
                | Ok(reviewFolders, problems) ->
                    let reviews = Stored.load (reviewFolders |> List.collect snd) |> Stored.reviews

                    let covered =
                        PeriodReview.awaitingApproval true reviews
                        |> List.filter (fun submission -> submission.ActorId <> actorId)
                        |> List.collect (fun submission ->
                            [ submission.Period.Start; submission.Period.Finish ]
                            |> List.map (fun date -> submission.ActorId, DateOnly(date.Year, date.Month, 1)))
                        |> List.distinct

                    match! readFolders provider folder (all [ for other, month in covered -> ActivityRecord.monthFolder other month ]) with
                    | Error reason -> return Error reason
                    | Ok(activityFolders, more) -> return Ok(reviewFolders @ activityFolders, problems @ more)
        }

    /// The repository state, then what the folders hold at it: validated,
    /// with records edited outside Chrona held for review. What was read is
    /// kept in the read cache, with which records were held.
    let load (provider: StorageProvider) (folder: Namespace) (actorId: string) (months: Set<int * int>) =
        async {
            match! provider.NamespaceState folder with
            | Error error -> return Error(unopened error)
            | Ok observed ->
                let token = InNamespace observed
                let dates = months |> Set.toList |> List.map firstOf

                let! found =
                    async {
                        match! read provider folder actorId dates with
                        | Error reason -> return Error reason
                        | Ok(own, incomplete) ->
                            match! readReviewing provider folder actorId dates (Stored.load (own |> List.collect snd)) with
                            | Error reason -> return Error reason
                            | Ok(reviewing, more) -> return Ok(own @ reviewing, incomplete @ more)
                    }

                match found with
                | Error reason -> return Error reason
                | Ok(folders, incomplete) ->
                    let objects = folders |> List.collect snd
                    let stored = Stored.load objects
                    let mutable histories = Map.empty
                    let mutable failure = None

                    for KeyValue(_, found) in stored.Activities.Activities do
                        if failure.IsNone then
                            match! provider.History folder found.Path with
                            | Ok history -> histories <- histories.Add(RelativePath.render found.Path, history)
                            | Error error -> failure <- Some(unopened error)

                    match failure with
                    | Some reason -> return Error reason
                    | None ->
                        let activities = Persistence.holdExternalEdits histories stored.Activities

                        let held =
                            activities.HeldForReview |> Map.toList |> List.map (fun (_, found) -> RelativePath.render found.Path) |> Set.ofList

                        do!
                            keepRead
                                actorId
                                folder
                                token
                                (folders
                                 |> List.collect (fun (path, inFolder) ->
                                     [ RelativePath.render path, inFolder
                                       HeldPrefix + RelativePath.render path,
                                       inFolder |> List.filter (fun found -> held.Contains(RelativePath.render found.Path)) ]))

                        return
                            Ok(
                                token,
                                { stored with
                                    Activities = { activities with Problems = incomplete @ activities.Problems } },
                                folders |> List.map (fun (path, inFolder) -> RelativePath.render path, inFolder) |> Map.ofList
                            )
        }

    /// Each person's months that hold time, from the index.
    let history (indexed: Indexed) =
        indexed.Index |> Option.map ActivityIndex.totals |> Option.defaultValue []

    /// What the index covers, or why it is not kept, for the person.
    let indexText (indexed: Indexed) =
        match indexed with
        | { Note = Some note } -> note
        | { Index = Some index } ->
            let months = ActivityIndex.totals index |> List.length
            let monthsText = if months = 1 then "1 month" else $"{months} months"
            let recordsText = if index.Source.Count = 1 then "1 record" else $"{index.Source.Count} records"
            $"Kept with every change: {recordsText} in {monthsText} of time."
        | _ -> ""

    /// What is stored with this page's unsent changes laid over it.
    let shown (state: Opened) =
        state.Queue.Entries
        |> List.filter unsent
        |> List.choose (recordsOf state.Folder >> Result.toOption)
        |> List.fold (fun stored changed -> Stored.overlay changed stored) state.Stored

    let contents (state: Opened) : StoreContents =
        let state = { state with Stored = shown state }
        let actorId = state.Session.ActorId
        let all = state.Stored.Activities.Activities |> Map.toList |> List.map (fun (_, found) -> found.Activity)

        { Name = state.Name
          Cached =
            match state.Basis with
            | CachedAt at -> Some at
            | ReadAt _ -> None
          // The person's own time; others' is read only where it waits for
          // their approval (WI-0036).
          Activities = all |> List.filter (fun a -> a.ActorId = actorId)
          Reviewing = all |> List.filter (fun a -> a.ActorId <> actorId)
          Periods = state.Stored.Periods |> Option.map _.Periods
          Reviews = Stored.reviews state.Stored
          Candidates = state.Stored.Candidates |> Map.toList |> List.map (fun (_, found) -> found.Candidate)
          References = state.Stored.References |> Map.toList |> List.map (fun (_, found) -> found.Item)
          Attestations = Stored.attestations state.Stored
          Members = state.Stored.Members |> Map.toList |> List.map (fun (_, found) -> found.Membership)
          Held = state.Stored.Activities.HeldForReview |> Map.toList |> List.map (fun (_, found) -> found.Activity)
          Audit = Stored.audit state.Stored
          Problems = Stored.problems state.Stored
          Months = state.Months |> Set.toList
          History = history state.Index
          Index = indexText state.Index }

    /// The stored activity index, checked against the month folders read:
    /// where it disagrees with them (a record changed outside Chrona, for
    /// example), it is said to be out of date there until it is rebuilt.
    let readIndex (provider: StorageProvider) (folder: Namespace) (actorId: string) (months: Set<int * int>) (stored: Stored.Stored) (previous: Indexed) =
        async {
            match! Derived.read provider folder ActivityIndex.definition with
            | Ok(Some(index, revision)) when index.Version = ActivityIndex.definition.Version && index.Name = ActivityIndex.empty.Name ->
                let snapshot = stored.Activities

                let read =
                    (snapshot.Activities |> Map.toList |> List.map snd)
                    @ (snapshot.HeldForReview |> Map.toList |> List.map snd)
                    |> List.map (fun found -> RelativePath.render found.Path, found.ContentHash)

                let differing =
                    months
                    |> Set.toList
                    |> List.choose (fun month -> ActivityRecord.monthFolder actorId (firstOf month) |> Result.toOption)
                    |> List.collect (fun monthFolder ->
                        let prefix = RelativePath.render monthFolder
                        let inFolder = read |> List.filter (fun (path, _) -> path.StartsWith(prefix + "/", StringComparison.Ordinal))
                        ActivityIndex.disagreements index prefix inFolder)

                return
                    { Index = Some index
                      Revision = Some revision
                      Note =
                        match differing with
                        | [] -> None
                        | paths ->
                            let places = if paths.Length = 1 then "1 place" else $"{paths.Length} places"
                            Some $"The activity index differs from the records in {places} (changed outside Chrona?). Rebuild it from the records." }
            | Ok(Some _) ->
                return
                    { Index = None
                      Revision = None
                      Note = Some "The activity index was built by another version of Chrona. Rebuild it from the records." }
            // A new organization's index starts empty, and is written with its first activity.
            | Ok None when previous.Index.IsSome && previous.Revision.IsNone -> return previous
            | Ok None ->
                return
                    { Index = None
                      Revision = None
                      Note = Some "The activity index has not been built yet. An administrator can build it from the records under More." }
            | Error _ ->
                return
                    { Index = None
                      Revision = None
                      Note = Some "The activity index cannot be read. An administrator can rebuild it from the records under More." }
        }

    /// Keeps the activity index as read in the read cache, with the change
    /// token the folders were read at.
    let keepIndex (state: Opened) (token: Condition) =
        async {
            match state.Index.Index, state.Index.Revision, Derived.path ActivityIndex.definition with
            | Some index, Some revision, Ok path ->
                do!
                    keepRead
                        state.Session.ActorId
                        state.Folder
                        token
                        [ IndexPartition, [ ({ Path = path; Content = Derived.encode index; Revision = revision }: StoredObject) ] ]
            | _ -> ()
        }

    /// Reads the state again from GitHub: the same months, plus any these
    /// dates add. From the read cache, this is what makes the records
    /// current again.
    let refresh (state: Opened) (dates: DateOnly list) =
        async {
            let months = state.Months + (dates |> List.map monthOf |> Set.ofList)

            match! load state.Provider state.Folder state.Session.ActorId months with
            | Error reason -> return Error(reasonOf reason)
            | Ok(token, stored, read) ->
                let! indexed = readIndex state.Provider state.Folder state.Session.ActorId months stored state.Index

                let next =
                    { state with
                        Basis = ReadAt token
                        Stored = stored
                        Read = read
                        Months = months
                        Index = indexed }

                do! keepIndex next token
                opened <- Some next
                return Ok next
        }

    /// What the next write is conditioned on after a commit this page made:
    /// the folder's state at exactly that commit when GitHub's head is still
    /// the commit; otherwise someone committed since, and the next write is
    /// held to the repository at the commit, as before Arca 0.4.0, so it is
    /// never conditioned on a folder state this page did not read.
    let conditionAfter (state: Opened) (receipt: CommitReceipt) =
        async {
            match! state.Provider.NamespaceState state.Folder with
            | Ok observed when observed.RepositoryToken = receipt.ChangeToken -> return InNamespace observed
            | _ -> return InRepository receipt.ChangeToken
        }

    /// What GitHub holds after a commit this page made: the folders it
    /// touched, as read plus the commit's changes at the receipt's revisions,
    /// kept in the read cache under the condition the commit leaves
    /// (LCP-083). `state` is the state after the commit.
    let afterCommit (state: Opened) (operation: Operation) (receipt: CommitReceipt) =
        async {
            let revisionOf path =
                receipt.Revisions.TryFind(RelativePath.render path) |> Option.flatten

            let indexPath = Derived.path ActivityIndex.definition |> Result.toOption
            let mutable read = state.Read
            let mutable touched = Set.empty
            let mutable index = None

            for change in operation.Changes do
                let path = Change.path change
                let rendered = RelativePath.render path

                let stored =
                    match Change.content change, revisionOf path with
                    | Some content, Some revision -> Some({ Path = path; Content = content; Revision = revision }: StoredObject)
                    | _ -> None

                if Some path = indexPath then
                    index <- stored
                else
                    match read |> Map.tryFindKey (fun folder _ -> rendered.StartsWith(folder + "/", StringComparison.Ordinal)) with
                    | Some folder ->
                        let others = read[folder] |> List.filter (fun found -> found.Path <> path)
                        read <- read.Add(folder, others @ Option.toList stored)
                        touched <- touched.Add folder
                    | None -> ()

            let held =
                state.Stored.Activities.HeldForReview |> Map.toList |> List.map (fun (_, found) -> RelativePath.render found.Path) |> Set.ofList

            do!
                keepRead
                    state.Session.ActorId
                    state.Folder
                    (match state.Basis with
                     | ReadAt condition -> condition
                     | CachedAt _ -> InRepository receipt.ChangeToken)
                    ((touched
                      |> Set.toList
                      |> List.collect (fun folder ->
                          [ folder, read[folder]
                            HeldPrefix + folder, read[folder] |> List.filter (fun found -> held.Contains(RelativePath.render found.Path)) ]))
                     @ (index |> Option.map (fun found -> IndexPartition, [ found ]) |> Option.toList))

            return { state with Read = read }
        }

    // ---- The queue of unsent changes (WI-0033) ----------------------------------

    let isEmpty (changed: Stored.Changed) =
        changed.Activities.IsEmpty
        && changed.References.IsEmpty
        && changed.Attestations.IsEmpty
        && changed.Members.IsEmpty
        && changed.Removed.IsEmpty
        && changed.Audit.IsEmpty
        && changed.Periods.IsNone
        && changed.Reviews.IsEmpty
        && changed.Candidates.IsEmpty
        && changed.Receipts.IsEmpty
        && changed.Consumed.IsEmpty

    /// Others' independent changes are shown once nothing of this page's own
    /// is waiting to be decided; unsent changes are shown over them.
    let refreshed (state: Opened) =
        if jobs.Count = 0 then [ Update.StoreOpened(contents state) ] else []

    /// Unsent changes from an earlier version, naming no one, waiting for
    /// the person to send, keep or discard them.
    let mutable earlierWaiting = 0

    let sync (state: Opened) (offline: bool) =
        let durability = state.Owned |> Option.map durabilityOf |> Option.defaultValue InPage

        Update.SyncChanged
            { // Shown from the read cache: GitHub could not be reached.
              Offline =
                offline
                || (match state.Basis with
                    | CachedAt _ -> true
                    | ReadAt _ -> false)
              KeptInBrowser = state.Owned.IsSome && durability <> InPage
              Note = state.Note
              Holder = state.Holder
              Durability = durability
              Notice = state.Notice
              Earlier = earlierWaiting }

    /// The queues this page holds, by lock: held until the page goes, the
    /// person signs out, or another tab takes one over. A Web Lock is not
    /// re-entrant, so a queue this page holds is never asked for again.
    let held = Dictionary<string, Arca.Limen.OwnedQueue>()

    /// Keeps the queue in this browser, write-ahead. When the browser
    /// refuses, the queue goes on in this page only, and the person is told.
    let persist (state: Opened) (queue: OfflineQueue) =
        async {
            match state.Owned with
            | None -> return { state with Queue = queue }
            | Some owned ->
                match! owned.Store.Save queue with
                | Ok() -> return { state with Queue = queue }
                | Error QueueStoreFailure.Unavailable when (owned.Diagnostics()).Ownership = Arca.Limen.OwnershipState.OwnedElsewhere ->
                    // Another tab took the queue over ("use this tab
                    // instead"): this save was fenced and wrote nothing. What
                    // was kept is that tab's now, to send or reconcile, never
                    // sent from here too; what this save added stays here.
                    held.Remove(lockOf state.Folder) |> ignore
                    let kept = state.Queue.Entries |> List.map _.Sequence |> Set.ofList
                    handed.AddRange(state.Queue.Entries |> List.filter unsent |> List.map _.Operation.IdempotencyKey)

                    return
                        { state with
                            Queue = { queue with Entries = queue.Entries |> List.filter (fun entry -> not (kept.Contains entry.Sequence)) }
                            Owned = None
                            Holder = HeldElsewhere false
                            Note = None }
                | Error failure ->
                    let note =
                        match failure, (owned.Diagnostics()).Ownership with
                        // Arca refuses to keep anything that looks like a credential.
                        | QueueStoreFailure.Corrupt _, _ -> "A change holds text that looks like a credential, so this browser does not keep unsent changes."
                        // Opened again when the store pack reports it.
                        | _, Arca.Limen.OwnershipState.ConnectionLost ->
                            "The browser closed the storage that keeps unsent changes; Chrona is opening it again."
                        | other, _ -> describeQueueStore other

                    return
                        { state with
                            Queue = queue
                            Owned = None
                            Note = Some note }
        }

    let mutable retryMs = FirstRetryMs
    let mutable retrying = false
    /// How many times each entry was decided again, by sequence.
    let decided = Dictionary<int64, int>()

    /// How many times a queued change is sent before a moving repository
    /// becomes a conflict for the person.
    let attempts = 3

    /// Opens the records from GitHub again, in place of what the read cache
    /// showed: set once the opening jobs exist (below).
    let mutable reopen: unit -> Async<Update.Msg list> = fun () -> async.Return []

    /// Reads the producers' inboxes after the records open (WI-0038): set
    /// once the inbox job exists (below).
    let mutable readInboxes: unit -> unit = ignore

    /// The inboxes wait until this page's own unsent changes are sent.
    let mutable inboxDeferred = false

    /// GitHub could not be reached: try again after a back-off, once.
    let rec scheduleRetry () =
        if not retrying then
            retrying <- true
            let wait = retryMs
            retryMs <- min MaxRetryMs (retryMs * 2)

            bridge.Start(
                async {
                    do! bridge.Call(Sleep wait) |> Async.Ignore
                    retrying <- false
                    serial drain
                    return []
                }
            )

    /// Sends the unsent changes in order, one step at a time, each step kept
    /// in this browser before and after it is taken (ARCA-OFF-004). It stops
    /// when nothing is left, or when GitHub cannot be reached (and tries
    /// again later).
    and drain () : Async<Update.Msg list> =
        let rec step (messages: Update.Msg list) =
            async {
                match opened with
                | None -> return messages
                // Shown from the read cache: nothing is sent until the records
                // are read from GitHub again, and every change made meanwhile
                // is decided again on them first.
                | Some { Basis = CachedAt _ } ->
                    let! reopened = reopen ()
                    return messages @ reopened
                | Some({ Basis = ReadAt token } as state) ->
                    let save queue =
                        async {
                            let! saved = persist state queue
                            opened <- Some saved
                            return saved
                        }

                    let offline (state: Opened) =
                        scheduleRetry ()
                        messages @ [ sync state true ]

                    match OfflineQueue.next state.Queue with
                    | None ->
                        retryMs <- FirstRetryMs

                        if inboxDeferred then
                            inboxDeferred <- false
                            readInboxes ()

                        let! state = save (OfflineQueue.prune state.Queue)
                        return messages @ [ sync state false ]
                    | Some entry ->
                        let id = entry.Operation.IdempotencyKey
                        let answer outcome = Update.StoreAnswered(id, outcome)

                        /// The entry is done with: the queue is kept, then what
                        /// to tell the engine is decided on the state kept.
                        let finish (state: Opened) (queue: Result<OfflineQueue, QueueError>) (more: Opened -> Update.Msg list) =
                            async {
                                decided.Remove entry.Sequence |> ignore

                                match queue with
                                | Ok queue ->
                                    let! saved = persist state queue
                                    opened <- Some saved
                                    return! step (messages @ more saved)
                                | Error _ -> return messages @ more state @ [ answer (Failed "The queue of unsent changes could not be updated.") ]
                            }

                        let landed (state: Opened) (receipt: CommitReceipt) (queue: OfflineQueue) =
                            async {
                                match recordsOf state.Folder entry with
                                | Error _ -> return! finish state (Ok queue) (fun _ -> [ answer Committed ])
                                | Ok changed ->
                                    let operation = OfflineQueue.operationOf state.Folder entry.Operation
                                    let! condition = conditionAfter state receipt

                                    let next =
                                        { state with
                                            Basis = ReadAt condition
                                            Stored = Stored.committed changed receipt (Stored.overlay changed state.Stored)
                                            Index =
                                                match operation with
                                                | Ok operation -> indexAfter state.Index operation receipt
                                                | Error _ -> state.Index }

                                    let! next =
                                        match operation with
                                        | Ok operation -> afterCommit next operation receipt
                                        | Error _ -> async.Return next

                                    let again = decided.ContainsKey entry.Sequence

                                    return!
                                        finish next (Ok queue) (fun saved ->
                                            (if again then refreshed saved else [])
                                            @ [ Update.IndexChanged(history saved.Index, indexText saved.Index); answer Committed ])
                            }

                        match entry.State, OfflineQueue.operationOf state.Folder entry.Operation with
                        | _, Error _ ->
                            return!
                                finish
                                    state
                                    (OfflineQueue.abandon entry.Sequence "it no longer validates" state.Queue)
                                    (fun _ -> [ answer (Failed "A queued change no longer validates and was set aside.") ])
                        | EntryState.Refused reason, _ ->
                            return! finish state (OfflineQueue.abandon entry.Sequence reason state.Queue) (fun _ -> [ answer (Failed $"GitHub refused it: {reason}.") ])
                        | (EntryState.InFlight _ | EntryState.OutcomeUnknown _), Ok _ ->
                            // It may have landed: find out before anything else is sent.
                            let queue = OfflineQueue.recover state.Queue
                            let entry = queue.Entries |> List.find (fun e -> e.Sequence = entry.Sequence)

                            match OfflineQueue.pendingOf entry with
                            | None -> return offline state
                            | Some obligation ->
                                match! state.Provider.Reconcile state.Folder obligation with
                                | Ok(ReconcileOutcome.Landed receipt) ->
                                    match OfflineQueue.recordReconciliation entry.Sequence (ReconcileOutcome.Landed receipt) queue with
                                    | Ok queue -> return! landed state receipt queue
                                    | Error _ -> return offline state
                                | Ok ReconcileOutcome.NotLanded ->
                                    match OfflineQueue.recordReconciliation entry.Sequence ReconcileOutcome.NotLanded queue with
                                    | Ok queue ->
                                        let! _ = save queue
                                        return! step messages
                                    | Error _ -> return offline state
                                | Ok(ReconcileOutcome.StillUnknown _)
                                | Error _ ->
                                    return
                                        offline state
                                        @ [ answer (
                                                OutcomeUnknown
                                                    "GitHub did not say whether the change was saved. It will be checked before anything is sent again."
                                            ) ]
                        // Made while the records were shown from the read cache:
                        // conditioned on nothing, so it is decided again on what
                        // GitHub holds before it is sent, as a change the
                        // repository moved under is (WI-0057, LCP-085).
                        | EntryState.Pending, Ok operation when operation.ExpectedChangeToken.IsNone && operation.ExpectedNamespaceToken.IsNone ->
                            let paths = operation.Changes |> List.map (Change.path >> RelativePath.render)

                            let queue =
                                { state.Queue with
                                    Entries =
                                        state.Queue.Entries
                                        |> List.map (fun queued ->
                                            if queued.Sequence = entry.Sequence then
                                                { queued with State = EntryState.Conflicted paths }
                                            else
                                                queued) }

                            let! _ = save queue
                            return! step messages
                        | EntryState.Conflicted _, Ok operation ->
                            // The repository moved under it: Chrona's rules
                            // decide it again on what is stored now (21).
                            let tries = (match decided.TryGetValue entry.Sequence with | true, n -> n | _ -> 0) + 1
                            decided[entry.Sequence] <- tries

                            match recordsOf state.Folder entry with
                            | Error _ ->
                                return!
                                    finish
                                        state
                                        (OfflineQueue.abandon entry.Sequence "its records cannot be read back" state.Queue)
                                        (fun _ -> [ answer (Failed "A queued change could not be read back and was set aside.") ])
                            | Ok changed ->
                                let dates = changed.Activities |> List.map (fun a -> a.Occurrence.LocalDate)

                                match! refresh state dates with
                                | Error _ -> return offline state
                                | Ok fresh ->
                                    let freshToken =
                                        match fresh.Basis with
                                        | ReadAt token -> token
                                        | CachedAt _ -> token

                                    match Reconcile.decide fresh.Stored changed with
                                    | Error divergences ->
                                        return!
                                            finish
                                                fresh
                                                (OfflineQueue.abandon entry.Sequence "it no longer fits what is stored" fresh.Queue)
                                                (fun saved -> refreshed saved @ [ answer (Conflict divergences) ])
                                    | Ok again when isEmpty again ->
                                        // An earlier attempt landed: nothing is left to send.
                                        return!
                                            finish
                                                fresh
                                                (OfflineQueue.abandon entry.Sequence "already stored" fresh.Queue)
                                                (fun saved -> refreshed saved @ [ answer Committed ])
                                    | Ok _ when tries >= attempts ->
                                        return!
                                            finish
                                                fresh
                                                (OfflineQueue.abandon entry.Sequence "the repository kept changing" fresh.Queue)
                                                (fun saved -> refreshed saved @ [ answer (Conflict [ Reconcile.KeptChanging ]) ])
                                    | Ok again ->
                                        let revised =
                                            Stored.changes fresh.Stored again
                                            |> Result.map (fun changes -> changes @ indexChange fresh.Index changes)
                                            |> Result.bind (fun changes ->
                                                Operation.create fresh.Folder operation.Metadata changes
                                                |> Result.mapError (fun _ -> [ StorageOperationRefused "the revised change does not validate" ]))
                                            |> Result.map (require freshToken)

                                        match revised with
                                        | Error diagnostics ->
                                            return!
                                                finish
                                                    fresh
                                                    (OfflineQueue.abandon entry.Sequence "it cannot be stored" fresh.Queue)
                                                    (fun _ -> [ answer (Failed $"This change cannot be stored ({describeAll diagnostics}).") ])
                                        | Ok operation ->
                                            // Arca 0.4.0's revise drops the entry's account
                                            // id: stamped again from its actor (fixed in 0.4.1).
                                            match OfflineQueue.revise entry.Sequence operation fresh.Queue |> Result.map stamped with
                                            | Error _ -> return messages @ [ answer (Failed "The queued change could not be revised.") ]
                                            | Ok queue ->
                                                let! saved = persist fresh queue
                                                opened <- Some saved
                                                return! step messages
                        | _, Ok operation ->
                            // Write-ahead: in flight is kept before it is sent.
                            match OfflineQueue.markInFlight entry.Sequence (changeTokenOf token) state.Queue with
                            | Error _ -> return messages @ [ answer (Failed "The queued change could not be sent.") ]
                            | Ok inFlight ->
                                let! state = save inFlight

                                match state.Queue.Entries |> List.exists (fun e -> e.Sequence = entry.Sequence) with
                                // Taken over meanwhile: the entry is the other tab's to send.
                                | false -> return! step messages
                                | true ->
                                    let! result = state.Provider.Commit operation

                                    match OfflineQueue.recordResult entry.Sequence result state.Queue with
                                    | Error _ -> return messages @ [ answer (Failed "The queued change's result could not be recorded.") ]
                                    | Ok next ->
                                        match result with
                                        | Ok receipt -> return! landed state receipt next
                                        | Error failure ->
                                            let! state = save next
                                            let sent = next.Entries |> List.find (fun e -> e.Sequence = entry.Sequence)

                                            match sent.State with
                                            // Nothing was applied; GitHub could not be reached.
                                            | EntryState.Pending -> return offline state
                                            | EntryState.Refused _ ->
                                                return!
                                                    finish
                                                        state
                                                        (OfflineQueue.abandon entry.Sequence (describeFailure failure) next)
                                                        (fun _ -> [ answer (Failed(describeFailure failure)) ])
                                            | _ -> return! step messages
            }

        step []

    /// The records are open: unsent changes this browser kept for this
    /// person are laid over them and sent, in order, after the first render
    /// (20, 23). Another account's unsent changes are never sent with this
    /// person's credential: they stay, untouched, for that account.
    let ready (state: Opened) =
        async {
            /// The queue this browser keeps, laid under this tab's own. The
            /// first load moves a queue an older Chrona kept in localStorage
            /// into IndexedDB (copied, verified, then removed), or reports why
            /// it waits or was left in place.
            let keep (owned: Arca.Limen.OwnedQueue) (state: Opened) =
                async {
                    let! result = owned.Store.Load()
                    let state = { state with Holder = HeldHere }
                    let notice = noticeOf owned

                    let kept =
                        { state with
                            Owned = Some owned
                            Note = (if owned.Mode = Arca.Limen.DurabilityMode.MemoryOnly then Some memoryOnlyNote else None)
                            Notice = notice }

                    // This page's own unsent changes (made from the read cache,
                    // or in the page while another tab held the queue) follow
                    // what is kept.
                    let mine = state.Queue.Entries |> List.filter unsent

                    match result |> Result.map (Option.map stamped) with
                    | Ok None when mine.IsEmpty -> return kept
                    // Kept before anything is sent (write-ahead).
                    | Ok None -> return! persist kept kept.Queue
                    // From an earlier version, naming no one: nothing is sent
                    // or discarded until the person decides.
                    | Ok(Some queue) when queue.Entries |> List.exists earlier ->
                        earlierWaiting <- queue.Entries |> List.filter earlier |> List.length

                        return
                            { state with
                                Owned = None
                                Notice = notice
                                Note = Some earlierNote }
                    | Ok(Some queue) when queue.Entries |> List.exists (othersOf state.Session) ->
                        return
                            { state with
                                Owned = None
                                Notice = notice
                                Note = Some "Another account left changes in this browser that have not been sent; they are kept for that account." }
                    | Ok(Some queue) when mine.IsEmpty -> return { kept with Queue = OfflineQueue.recover queue }
                    | Ok(Some queue) ->
                        // Decisions were counted by the old numbers.
                        decided.Clear()
                        return! persist kept (laidOver (OfflineQueue.recover queue) mine)
                    | Error failure -> return { state with Owned = None; Notice = notice; Note = Some(describeQueueStore failure) }
                }

            // One tab holds this browser's queue: only it loads, keeps and
            // sends what is kept (WI-0067, LCP-059). A queue this page already
            // holds is not asked for again (opening the records again would
            // otherwise read as another tab's).
            let lock = lockOf state.Folder
            earlierWaiting <- 0

            let! state =
                async {
                    match held.TryGetValue lock with
                    | true, owned -> return! keep owned state
                    | _ ->
                        let! options = queueOptions ()
                        match! Arca.Limen.LimenQueue.own host options state.Folder with
                        | Arca.Limen.QueueOpening.Owned owned ->
                            held[lock] <- owned
                            return! keep owned state
                        | Arca.Limen.QueueOpening.OwnedElsewhere -> return { state with Owned = None; Holder = HeldElsewhere false; Note = None }
                        // No Web Locks and no page-only fallback: durable
                        // stores are never written by several tabs at once.
                        | Arca.Limen.QueueOpening.OwnershipUnsupported ->
                            return
                                { state with
                                    Owned = None
                                    Note = Some "This browser cannot tell Chrona's tabs apart, so unsent changes live in this page only." }
                        | Arca.Limen.QueueOpening.NothingUsable _ -> return { state with Owned = None; Note = Some memoryOnlyNote }
                }

            opened <- Some state
            retryMs <- FirstRetryMs
            let resumed = state.Queue.Entries |> List.filter unsent |> List.choose (requestOf state.Folder)

            match state.Basis with
            // From the read cache: GitHub is tried again after a back-off.
            | CachedAt _ ->
                scheduleRetry ()
                return [ Update.StoreOpened(contents state); Update.StoreResumed resumed; sync state true ]
            | ReadAt _ when resumed.IsEmpty ->
                readInboxes ()
                return [ Update.StoreOpened(contents state); sync state false ]
            | ReadAt _ ->
                inboxDeferred <- true
                serial drain
                return [ Update.StoreOpened(contents state); Update.StoreResumed resumed; sync state false ]
        }

    /// Opens a folder, initializing it first when it is new.
    let ensure (provider: StorageProvider) (folder: Namespace) (initialize: unit -> Result<Operation, Diagnostic list>) =
        async {
            let readManifest () =
                async {
                    match Layout.manifestPath with
                    | Error error -> return Error(Unopened.Refused(LocationError.describe error))
                    | Ok path ->
                        match! provider.Read folder path with
                        | Error failure -> return Error(unopened failure)
                        | Ok stored -> return Ok(Storage.openNamespace folder stored)
                }

            match! readManifest () with
            | Error reason -> return Error reason
            | Ok(Ok _) -> return Ok()
            | Ok(Error [ NamespaceNotInitialized _ ]) ->
                match initialize () with
                | Error [ PublicProductionRepository ] ->
                    return Error(Unopened.Refused "Production records are never started in a public repository. Use a private repository.")
                | Error diagnostics -> return Error(Unopened.Refused $"The records could not be set up ({describeAll diagnostics}).")
                | Ok operation ->
                    match! provider.Commit operation with
                    | Ok _ -> return Ok()
                    | Error failure -> return Error(unopened failure)
            | Ok(Error diagnostics) -> return Error(Unopened.Refused $"The configured folder cannot be used ({describeAll diagnostics}).")
        }

    /// The repository, if the signed-in account can keep records in it.
    let resolve (location: DataLocation) =
        async {
            match! backend.Resolve location with
            | Error reason -> return Error reason
            | Ok snapshot when not snapshot.CanRead -> return Error(Unopened.Refused $"Your GitHub account cannot read {snapshot.Repository}.")
            | Ok snapshot when not snapshot.CanWrite -> return Error(Unopened.Refused $"Your GitHub account cannot write to {snapshot.Repository}.")
            | Ok snapshot when snapshot.Archived -> return Error(Unopened.Refused $"{snapshot.Repository} is archived.")
            | Ok snapshot ->
                match snapshot.Branch with
                | BranchAccess.Writable -> return Ok snapshot
                | BranchAccess.Missing ->
                    return Error(Unopened.Refused $"The data branch {BranchName.value location.Branch} does not exist in {snapshot.Repository}.")
                | BranchAccess.NotWritable _ ->
                    return Error(Unopened.Refused $"The data branch {BranchName.value location.Branch} does not accept direct changes.")
        }

    /// Makes the session's person the organization's administrator: the
    /// founder of a new organization, or a listed account confirming one
    /// that has no listed administrator (Governance).
    let appoint (state: Opened) =
        async {
            let roster = Stored.roster state.Session.OrganizationId state.Stored
            let membership = Governance.administrator (principalOf state.Session) roster
            let changed = { Stored.nothing with Members = [ membership ] }

            match state.Basis, Stored.changes state.Stored changed, operationContext state.Session (newKey "appoint") (now ()) with
            // Appointing is a write: never from the read cache (LCP-085).
            | CachedAt _, _, _ -> return Error "An administrator can be appointed only while GitHub can be reached."
            | ReadAt token, Ok changes, Ok context ->
                match Storage.operation state.Folder context "appoint an administrator" changes with
                | Error diagnostics -> return Error($"The administrator could not be recorded ({describeAll diagnostics}).")
                | Ok operation ->
                    match! state.Provider.Commit(require token operation) with
                    | Ok receipt ->
                        let! condition = conditionAfter state receipt

                        let next =
                            { state with
                                Basis = ReadAt condition
                                Stored = Stored.committed changed receipt state.Stored }

                        let! next = afterCommit next operation receipt
                        return Ok next
                    | Error failure -> return Error(describeFailure failure)
            | _, Error diagnostics, _
            | _, _, Error diagnostics -> return Error($"The administrator could not be recorded ({describeAll diagnostics}).")
        }

    /// Why an organization waits for a listed administrator, for this person.
    let confirmationReason (organization: Deployment.OrganizationConfig) (canConfirm: bool) =
        let accounts = organization.Administrators |> String.concat ", "

        match canConfirm, organization.Administrators with
        | true, _ ->
            $"{organization.DisplayName} has no administrator from this deployment's configuration. You are one of its listed administrators: confirm to administer it."
        | false, [] ->
            $"{organization.DisplayName} has no administrator from this deployment's configuration, and the configuration lists none. Nothing can be done in it until the deployment lists one."
        | false, _ ->
            $"{organization.DisplayName} has no administrator from this deployment's configuration. One of its listed administrators (GitHub accounts {accounts}) must sign in and confirm first."

    /// Where the deployment keeps this session's organization.
    let prepare (config: Deployment.DeploymentConfig) (session: Session) =
        match Storage.binding config, Deployment.organization config session.OrganizationId with
        | Ok binding, Some organization ->
            match Storage.applicationNamespace binding, Storage.organizationNamespace config binding organization.Id with
            | Ok application, Ok folder -> Ok(binding, application, folder, organization)
            | Error diagnostic, _
            | _, Error diagnostic -> Error(code diagnostic)
        | Error diagnostic, _ -> Error(code diagnostic)
        | _, None -> Error $"the deployment does not serve {session.OrganizationId}"

    /// Opens the records from GitHub; what was read is kept in the read
    /// cache. This page's own unsent changes (`carried`) follow what this
    /// browser keeps.
    let openFresh (config: Deployment.DeploymentConfig) (session: Session) (dates: DateOnly list) (carried: QueueEntry list) =
        async {
            let at = now ()
            let context () = operationContext session (newKey "open") at

            match prepare config session with
            | Error reason -> return Error(Unopened.Refused $"This deployment's storage is not configured correctly ({reason}).")
            | Ok(binding, application, folder, organization) ->
                match! resolve binding.Location with
                | Error reason -> return Error reason
                | Ok home ->
                    let applicationProvider = backend.Provider binding.Location

                    match!
                        ensure applicationProvider application (fun () ->
                            context () |> Result.bind (Storage.initializeApplication binding home.Visibility None))
                    with
                    | Error reason -> return Error reason
                    | Ok() ->
                        let separate = folder.Location <> binding.Location
                        let! own = if separate then resolve folder.Location else async.Return(Ok home)

                        match own with
                        | Error reason -> return Error reason
                        | Ok repository ->
                            let provider = if separate then backend.Provider folder.Location else applicationProvider
                            let manifest = Organization.create organization.Id organization.DisplayName organization.Slug organization.TimeZone at

                            let! existing =
                                async {
                                    match Layout.manifestPath with
                                    | Error error -> return Error(Unopened.Refused(LocationError.describe error))
                                    | Ok path ->
                                        match! provider.Read folder path with
                                        | Ok ReadOutcome.Absent -> return Ok false
                                        | Ok(ReadOutcome.Found _)
                                        | Ok(ReadOutcome.Erased _) -> return Ok true
                                        | Error failure -> return Error(unopened failure)
                                }

                            let decideFor isNew roster =
                                Governance.decide config.Environment organization isNew roster session.ActorId

                            let nobody: Access.Roster = { OrganizationId = organization.Id; Members = Map.empty }

                            match existing, decideFor true nobody with
                            | Error reason, _ -> return Error reason
                            // Nothing is written for someone who may not set it up.
                            | Ok false, Governance.Refused reason -> return Error(Unopened.Refused reason)
                            | Ok isExisting, _ ->
                                match!
                                    ensure provider folder (fun () ->
                                        context ()
                                        |> Result.bind (fun context -> Storage.initializeOrganization config binding repository.Visibility None context manifest))
                                with
                                | Error reason -> return Error reason
                                | Ok() ->
                                    let months = dates |> List.map monthOf |> Set.ofList

                                    match! load provider folder session.ActorId months with
                                    | Error reason -> return Error reason
                                    | Ok(token, stored, read) ->
                                        let state =
                                            { Session = session
                                              Folder = folder
                                              Provider = provider
                                              Name = string folder.Location.Repository
                                              Basis = ReadAt token
                                              Stored = stored
                                              Read = read
                                              Months = months
                                              Organization = organization
                                              Queue = { OfflineQueue.create OfflinePolicy.QueueWrites with Entries = carried }
                                              Owned = None
                                              Holder = HeldHere
                                              Note = None
                                              Notice = None
                                              Index = { Index = None; Revision = None; Note = None } }

                                        // A new organization's activity index starts empty.
                                        let! indexed =
                                            readIndex
                                                provider
                                                folder
                                                session.ActorId
                                                months
                                                stored
                                                { state.Index with Index = (if isExisting then None else Some ActivityIndex.empty) }

                                        let state = { state with Index = indexed }
                                        do! keepIndex state token

                                        let roster = Stored.roster organization.Id stored

                                        match decideFor (not isExisting) roster with
                                        | Governance.Proceed ->
                                            let! messages = ready state
                                            return Ok messages
                                        | Governance.Found ->
                                            match! appoint state with
                                            | Error reason -> return Error(Unopened.Refused reason)
                                            | Ok state ->
                                                let! messages = ready state
                                                return Ok messages
                                        | Governance.NeedsConfirmation canConfirm ->
                                            // Nothing is granted: the records stay closed
                                            // until a listed account confirms.
                                            opened <- None
                                            pending <- Some state
                                            return Ok [ Update.StoreNeedsConfirmation(confirmationReason organization canConfirm, canConfirm) ]
                                        | Governance.Refused reason -> return Error(Unopened.Refused reason)
        }

    /// Opens the records from the read cache while GitHub cannot be reached
    /// (WI-0057, LCP-082..084): what GitHub last gave this account, shown as
    /// of when it was read, never as current. The organization's people and
    /// reference data must be cached; a month counts as read only when all
    /// of its folders are. None when the cache cannot stand in.
    let openCached (config: Deployment.DeploymentConfig) (session: Session) (dates: DateOnly list) (carried: QueueEntry list) =
        async {
            match prepare config session, Stored.folders session.ActorId [] with
            | Error _, _
            | _, Error _ -> return None
            | Ok(_, _, folder, organization), Ok common ->
                match! readCache () with
                | None -> return None
                | Some cached ->
                    // A partition as GitHub last gave it: still shown, as of
                    // its token, while GitHub cannot be reached (LCP-084).
                    let show (partition: string) =
                        async {
                            match ReadCache.key session.ActorId folder partition with
                            | Error _ -> return None
                            | Ok key ->
                                match! cached.Show key with
                                | Ok(Some entry) ->
                                    match ReadCache.revalidate ProviderObservation.Unreachable entry with
                                    | Revalidation.Unverified shown -> return Some shown
                                    | _ -> return None
                                | _ -> return None
                        }

                    // One after another, on this page's own turn: never
                    // Async.Parallel or Sequential, which leave the bridge's
                    // thread.
                    let showAll (partitions: string list) =
                        async {
                            let mutable shown = []

                            for partition in partitions do
                                let! one = show partition
                                shown <- shown @ [ one ]

                            return shown
                        }

                    let isHeld (entry: Cached<Arca.CacheEntry>) =
                        (Cached.key entry).Partition.StartsWith(HeldPrefix, StringComparison.Ordinal)

                    let objectsOf (entry: Cached<Arca.CacheEntry>) =
                        (Cached.value entry).Records
                        |> List.choose (fun (record: CachedRecord) ->
                            RelativePath.parse record.Path
                            |> Result.toOption
                            // A cached record carries no revision: it is never
                            // the basis of a write (LCP-085).
                            |> Option.map (fun path ->
                                ({ Path = path
                                   Content = record.Content
                                   Revision = Revision("cached:" + record.ContentHash) }: StoredObject)))

                    let! shared = common |> List.map RelativePath.render |> showAll

                    // Candidates kept by a Chrona before WI-0038 are not
                    // cached yet: shown once GitHub is read again.
                    let optional = CandidateRecord.openFolder () |> Result.toOption

                    let missing =
                        List.zip common shared |> List.exists (fun (path, shown) -> shown.IsNone && Some path <> optional)

                    if missing then
                        return None
                    else
                        let mutable entries = shared |> List.choose id
                        let mutable months = Set.empty

                        for month in dates |> List.map monthOf |> List.distinct do
                            match Stored.folders session.ActorId [ firstOf month ] with
                            | Ok all ->
                                let own = all |> List.filter (fun path -> not (List.contains path common)) |> List.map RelativePath.render
                                let! found = showAll own
                                let! held = own |> List.map (fun path -> HeldPrefix + path) |> showAll

                                if found |> List.forall Option.isSome then
                                    months <- months.Add month
                                    entries <- entries @ (found |> List.choose id) @ (held |> List.choose id)
                            | Error _ -> ()

                        let heldPaths =
                            entries |> List.filter isHeld |> List.collect objectsOf |> List.map (fun found -> RelativePath.render found.Path) |> Set.ofList

                        let stored = Stored.load (entries |> List.filter (isHeld >> not) |> List.collect objectsOf)
                        let stored = { stored with Activities = Persistence.holdPaths heldPaths stored.Activities }
                        let! index = show IndexPartition
                        let unread = "The activity index is read again when GitHub can be reached."

                        let indexed =
                            match index |> Option.map objectsOf with
                            | Some [ found ] ->
                                match Derived.decode found.Content with
                                | Ok index -> { Index = Some index; Revision = None; Note = None }
                                | Error _ -> { Index = None; Revision = None; Note = Some unread }
                            | _ -> { Index = None; Revision = None; Note = Some unread }

                        let state =
                            { Session = session
                              Folder = folder
                              Provider = backend.Provider folder.Location
                              Name = string folder.Location.Repository
                              // Shown as of the oldest partition it holds.
                              Basis = CachedAt(entries |> List.map Cached.readAt |> List.min)
                              Stored = stored
                              Read = Map.empty
                              Months = months
                              Organization = organization
                              Queue = { OfflineQueue.create OfflinePolicy.QueueWrites with Entries = carried }
                              Owned = None
                              Holder = HeldHere
                              Note = None
                              Notice = None
                              Index = indexed }

                        // Only an organization this person already works in
                        // opens from the cache: setting one up, or confirming
                        // an administrator, needs GitHub.
                        match Governance.decide config.Environment organization false (Stored.roster organization.Id stored) session.ActorId with
                        | Governance.Proceed ->
                            let! messages = ready state
                            return Some messages
                        | _ -> return None
        }

    /// This page's own unsent changes, carried into the records opened next:
    /// where this tab keeps nothing in the browser, they live only here.
    let carriedOver () =
        match opened with
        | Some state when state.Owned.IsNone -> state.Queue.Entries |> List.filter unsent
        | _ -> []

    /// The deployment, session and dates the records were last opened for.
    let mutable lastOpen: (Deployment.DeploymentConfig * Session * DateOnly list) option = None

    let openJob (config: Deployment.DeploymentConfig) (session: Session) (dates: DateOnly list) () =
        async {
            let carried = carriedOver ()

            let! fresh = openFresh config session dates carried

            match fresh with
            | Ok messages -> return messages
            | Error(Unopened.Refused reason) -> return [ Update.StoreUnavailable reason ]
            | Error(Unopened.Unreachable reason) ->
                match! openCached config session dates carried with
                | Some messages -> return messages
                | None -> return [ Update.StoreUnavailable reason ]
        }

    // From the read cache, the records are opened from GitHub again once it
    // can be reached; until then they stay as shown.
    reopen <-
        fun () ->
            async {
                match lastOpen, opened with
                | Some(config, session, dates), Some({ Basis = CachedAt _ } as shown) ->
                    let dates = dates @ (shown.Months |> Set.toList |> List.map firstOf)

                    match! openFresh config session dates (carriedOver ()) with
                    | Ok messages -> return messages
                    | Error(Unopened.Unreachable _) ->
                        scheduleRetry ()
                        return [ sync shown true ]
                    | Error(Unopened.Refused reason) -> return [ Update.StoreUnavailable reason ]
                | _ -> return []
            }

    /// A listed account confirms itself as the administrator of an
    /// organization that has none from the configuration.
    let confirmJob () =
        async {
            match pending with
            | Some state when Deployment.isBootstrapAdministrator state.Organization state.Session.ActorId ->
                pending <- None

                match! appoint state with
                | Error reason -> return [ Update.StoreUnavailable reason ]
                | Ok state -> return! ready state
            | Some state -> return [ Update.StoreNeedsConfirmation(confirmationReason state.Organization false, false) ]
            | None -> return [ Update.StoreUnavailable "There is nothing to confirm." ]
        }

    let commitJob (request: StoreRequest) () =
        async {
            let answer outcome = [ Update.StoreAnswered(request.CommitId, outcome) ]

            match opened with
            | None -> return answer (Failed "The records are not open.")
            | Some state ->
                // A change is never checked against less than what is stored:
                // months it touches that were not read yet are read first.
                let unread =
                    request.Activities
                    |> List.map (fun activity -> activity.Occurrence.LocalDate)
                    |> List.filter (fun date -> not (state.Months.Contains(monthOf date)))

                let! current =
                    if unread.IsEmpty then
                        async.Return(Ok state)
                    else
                        async {
                            match! refresh state unread with
                            | Ok wider -> return Ok wider
                            | Error _ ->
                                return
                                    Error
                                        "This month's records have not been read yet, and GitHub cannot be reached to read them. Nothing was saved; try again when you are online."
                        }

                match current with
                | Error reason -> return answer (Failed reason)
                | Ok state ->
                    // Decided on what is stored with this page's unsent changes
                    // over it, and with the outside edits the person accepted
                    // trusted again (41).
                    let release (stored: Stored.Stored) =
                        { stored with Activities = Persistence.release request.Accepted stored.Activities }

                    let real = release state.Stored

                    match Reconcile.decide (release (shown state)) (changeOf request) with
                    | Error divergences -> return refreshed state @ answer (Conflict divergences)
                    | Ok changed when isEmpty changed -> return answer Committed
                    | Ok changed ->
                        match Stored.changes real changed, operationContext state.Session request.CommitId (now ()) with
                        | Error diagnostics, _
                        | _, Error diagnostics -> return answer (Failed $"This change cannot be stored ({describeAll diagnostics}).")
                        | Ok [], _ -> return answer Committed
                        | Ok changes, Ok context ->
                            // The activity index is kept current in the same commit.
                            let changes = changes @ indexChange state.Index changes

                            match Storage.operation state.Folder context "save changes" changes with
                            | Error diagnostics -> return answer (Failed $"This change cannot be stored ({describeAll diagnostics}).")
                            | Ok operation ->
                                // Conditioned on the repository state the records
                                // were read at, and queued before it is sent.
                                // From the read cache it is conditioned on
                                // nothing, and decided again on what GitHub
                                // holds before it is sent (LCP-085).
                                let queued =
                                    match state.Basis with
                                    | ReadAt token -> require token operation
                                    | CachedAt _ -> operation

                                let enqueued =
                                    match accountOf state.Session with
                                    | Some who -> OfflineQueue.enqueueFor who.Account (now ()) queued state.Queue
                                    | None -> OfflineQueue.enqueue (now ()) queued state.Queue

                                match enqueued with
                                | Error _ -> return answer (Failed "This change could not be queued.")
                                | Ok(queued, _) ->
                                    let! state = persist { state with Stored = real } queued
                                    opened <- Some state
                                    return! drain ()
        }

    /// Reads further month folders, when the person goes to a month not
    /// read yet (38: one folder, on demand).
    let readJob (dates: DateOnly list) () =
        async {
            match opened with
            | None -> return []
            | Some state ->
                match! refresh state dates with
                | Ok next -> return [ Update.StoreOpened(contents next) ]
                | Error reason ->
                    // The month is asked for again when the person returns to it.
                    return [ Update.StoreReadFailed reason ]
        }

    let describeDerived =
        function
        | DerivedError.Corrupt _ -> "The stored activity index could not be read."
        | DerivedError.Snapshot _ -> "The records kept changing while they were read. Try again."
        | DerivedError.InvalidSource(path, _) -> $"{path} is not a valid record, so the index cannot be built until it is repaired."
        | DerivedError.Provider failure -> describeFailure failure
        | DerivedError.InvalidDefinition reason -> $"The activity index is not defined correctly ({reason})."

    /// Rebuilds the activity index from every stored record (40): the
    /// recovery path, and how an index from before WI-0034 is first built.
    /// Arca writes it only when it differs from what is stored, and only
    /// while the records are still as read.
    let rebuildJob () =
        async {
            match opened with
            | None -> return [ Update.IndexRebuilt "The records are not open." ]
            | Some state ->
                match operationContext state.Session (newKey "index") (now ()) with
                | Error _ -> return [ Update.IndexRebuilt "The activity index could not be rebuilt." ]
                | Ok context ->
                    match! Derived.rebuild state.Provider state.Folder (Storage.metadata context "rebuild the activity index") ActivityIndex.definition with
                    | Error error -> return [ Update.IndexRebuilt(describeDerived error) ]
                    | Ok(index, receipt) ->
                        let differed =
                            state.Index.Index
                            |> Option.map (fun previous ->
                                let difference = Derived.compare previous index
                                difference.Removed.Length + difference.Added.Length)

                        let records = if index.Source.Count = 1 then "1 record" else $"{index.Source.Count} records"

                        let summary =
                            match receipt, differed with
                            | None, _ -> $"The activity index already matched the records ({records})."
                            | Some _, None -> $"The activity index was built from {records}."
                            | Some _, Some 0 -> $"The activity index was rewritten from {records}; its entries were already right."
                            | Some _, Some count -> $"The activity index was rebuilt from {records}; {count} entries had differed."

                        match! refresh state [] with
                        | Error reason -> return [ Update.IndexRebuilt reason ]
                        | Ok next -> return refreshed next @ [ Update.IndexRebuilt summary ]
        }

    // ---- Producers' inboxes (WI-0038) -------------------------------------------

    /// What reading the inboxes last found on this page.
    let mutable inboxStatus = idleInbox

    /// A folder's files and subfolders; a folder that does not exist has none.
    let entriesOf (state: Opened) (path: RelativePath) =
        async {
            match! state.Provider.List state.Folder path with
            | Error failure -> return Error failure
            | Ok listing ->
                let files = listing.Entries |> List.filter (fun entry -> not entry.IsFolder) |> List.map _.Path
                let folders = listing.Entries |> List.filter _.IsFolder |> List.map _.Path
                return Ok(files, folders)
        }

    let lastSegment (path: RelativePath) =
        let rendered = RelativePath.render path
        rendered.Substring(rendered.LastIndexOf '/' + 1)

    let receive (state: Opened) (sourceSystem: string) (observationId: string) (path: RelativePath) =
        async {
            match! state.Provider.Read state.Folder path with
            | Error failure -> return Error failure
            // Taken away meanwhile: by its producer, or by another page's pass.
            | Ok ReadOutcome.Absent -> return Ok None
            | Ok(ReadOutcome.Found found) ->
                let known: Observations.Inbox =
                    { Observations.empty with Candidates = state.Stored.Candidates |> Map.map (fun _ stored -> stored.Candidate) }

                let consumed = [ path, found.Revision ]

                return
                    Ok(
                        Some(
                            match Intake.decide (now ()) state.Organization.Id known sourceSystem observationId found.Content with
                            // Its file stays, for its producer to see beside the receipt.
                            | Intake.Refused receipt -> { Stored.nothing with Receipts = [ receipt ] }, NotObservation(Intake.reasonsOf receipt |> Option.defaultValue [])
                            | Intake.Received(Observations.NewCandidate(candidate, receipt)) ->
                                { Stored.nothing with
                                    Candidates = [ candidate ]
                                    Receipts = [ receipt ]
                                    Consumed = consumed },
                                TakenIn
                            | Intake.Received(Observations.ReceiptRepair receipt) ->
                                { Stored.nothing with
                                    Receipts = [ receipt ]
                                    Consumed = consumed },
                                TakenIn
                            | Intake.Received(Observations.AlreadyReceived _) -> { Stored.nothing with Consumed = consumed }, TakenIn
                        )
                    )
        }

    /// One inbox file, made durable in one commit conditioned on the records
    /// as read: the candidate, its receipt and the file's removal land
    /// together or not at all, so a receipt never claims a candidate that is
    /// not stored (expansion 19). When the repository moved meanwhile, it is
    /// read again and the file decided again on it, once.
    let rec takeIn (state: Opened) (sourceSystem: string) (observationId: string) (path: RelativePath) (alreadyReceived: bool) (again: bool) =
        async {
            let! decided =
                if alreadyReceived then
                    async {
                        match! state.Provider.Read state.Folder path with
                        | Error failure -> return Error failure
                        | Ok ReadOutcome.Absent -> return Ok None
                        | Ok(ReadOutcome.Found found) -> return Ok(Some({ Stored.nothing with Consumed = [ path, found.Revision ] }, TakenIn))
                    }
                else
                    receive state sourceSystem observationId path

            match state.Basis, decided with
            | CachedAt _, _ -> return state, Error "GitHub cannot be reached."
            | _, Error failure -> return state, Error(describeFailure failure)
            | _, Ok None -> return state, Ok None
            | ReadAt token, Ok(Some(changed, taken)) ->
                match Stored.changes state.Stored changed, operationContext state.Session (newKey "inbox") (now ()) with
                | Error diagnostics, _
                | _, Error diagnostics -> return state, Error(describeAll diagnostics)
                | Ok changes, Ok context ->
                    let summary = $"receive observation {sourceSystem}/{observationId}"

                    match Storage.operation state.Folder context summary changes with
                    | Error diagnostics -> return state, Error(describeAll diagnostics)
                    | Ok operation ->
                        match! state.Provider.Commit(Operation.requireChangeToken token operation) with
                        | Ok receipt ->
                            let next =
                                { state with
                                    Basis = ReadAt receipt.ChangeToken
                                    Stored = Stored.committed changed receipt state.Stored }

                            let! next = afterCommit next operation receipt
                            opened <- Some next
                            return next, Ok(Some taken)
                        | Error(StorageFailure.StaleChangeToken _)
                        | Error(StorageFailure.Conflicted _) when not again ->
                            match! refresh state [] with
                            | Error reason -> return state, Error reason
                            | Ok fresh -> return! takeIn fresh sourceSystem observationId path alreadyReceived true
                        | Error failure -> return state, Error(describeFailure failure)
        }

    /// One pass over the organization's inboxes (requirement 18): at most
    /// `InboxBatch` files, in path order; the rest wait for the next pass,
    /// which follows at once. Only while the records are read from GitHub
    /// and nothing of this page's own waits to be sent, so a pass never
    /// moves the repository under a queued change.
    let rec inboxJob () =
        async {
            match opened with
            | Some({ Basis = ReadAt _ } as state) when not (state.Queue.Entries |> List.exists unsent) ->
                bridge.Emit [ Update.InboxReconciled { inboxStatus with Running = true } ]

                let finish (state: Opened) (status: InboxStatus) (changed: bool) =
                    inboxStatus <- { status with Running = false; LastRun = Some(now ()) }
                    (if changed then refreshed state else []) @ [ Update.InboxReconciled inboxStatus ]

                let failed (reason: string) =
                    finish state { inboxStatus with Failed = inboxStatus.Failed + 1 } false
                    @ [ Update.StoreReadFailed $"The inboxes could not be read: {reason}" ]

                match RelativePath.parse Chrona.Integration.Inbox.Folder with
                | Error _ -> return finish state inboxStatus false
                | Ok root ->
                    match! entriesOf state root with
                    | Error failure -> return failed (describeFailure failure)
                    | Ok(_, sources) ->
                        // Every file in every source's inbox, with whether a
                        // receipt is stored for it already.
                        let mutable found = []
                        let mutable failure = None

                        for source in sources |> List.sortBy RelativePath.render do
                            if failure.IsNone then
                                let sourceSystem = lastSegment source

                                match! entriesOf state source with
                                | Error error -> failure <- Some error
                                | Ok(files, _) ->
                                    let! receipts =
                                        async {
                                            match ReceiptRecord.sourceFolder sourceSystem with
                                            | Error _ -> return Ok Set.empty
                                            | Ok folder ->
                                                match! entriesOf state folder with
                                                | Error error -> return Error error
                                                | Ok(stored, _) -> return Ok(stored |> List.map lastSegment |> Set.ofList)
                                        }

                                    match receipts with
                                    | Error error -> failure <- Some error
                                    | Ok receipts ->
                                        for file in files |> List.sortBy RelativePath.render do
                                            let name = lastSegment file
                                            found <- found @ [ sourceSystem, name, file, receipts.Contains name ]

                        match failure with
                        | Some error -> return failed (describeFailure error)
                        | None ->
                            let named =
                                found
                                |> List.map (fun (sourceSystem, name, file, received) ->
                                    match Chrona.Integration.Inbox.ofPath (RelativePath.render file) with
                                    | Some(_, observationId) when (ReceiptRecord.keyOf sourceSystem observationId |> Result.isOk) ->
                                        Ok(sourceSystem, observationId, file, received)
                                    | _ -> Error(sourceSystem, name, [ "the file's name cannot name an observation: use <observationId>.json" ]))

                            // A file with a receipt is either received already
                            // (its removal did not land) or not an observation,
                            // kept for its producer with the receipt's reasons.
                            let mutable invalid = named |> List.choose (function Error unnamed -> Some unnamed | Ok _ -> None)
                            let mutable work = []
                            let mutable failure = None

                            for sourceSystem, observationId, file, received in named |> List.choose Result.toOption do
                                if failure.IsNone then
                                    if not received then
                                        work <- work @ [ sourceSystem, observationId, file, false ]
                                    else
                                        match ReceiptRecord.pathOf sourceSystem observationId with
                                        | Error _ -> ()
                                        | Ok receiptPath ->
                                            match! state.Provider.Read state.Folder receiptPath with
                                            | Error error -> failure <- Some error
                                            | Ok ReadOutcome.Absent -> work <- work @ [ sourceSystem, observationId, file, false ]
                                            | Ok(ReadOutcome.Found stored) ->
                                                match Record.decode Record.DefaultMaxBytes stored.Content |> Result.toOption with
                                                | Some record ->
                                                    match ReceiptRecord.ofBody record.Body with
                                                    | Ok receipt ->
                                                        match Intake.reasonsOf receipt with
                                                        | Some reasons -> invalid <- invalid @ [ sourceSystem, observationId, reasons ]
                                                        | None -> work <- work @ [ sourceSystem, observationId, file, true ]
                                                    | Error reason -> invalid <- invalid @ [ sourceSystem, observationId, [ $"its receipt cannot be read: {reason}" ] ]
                                                | None -> invalid <- invalid @ [ sourceSystem, observationId, [ "its receipt cannot be read" ] ]

                            match failure with
                            | Some error -> return failed (describeFailure error)
                            | None ->
                                let batch = work |> List.truncate InboxBatch
                                let mutable state = state
                                let mutable received = 0
                                let mutable failed = 0
                                let mutable changed = false
                                let mutable stop = false

                                for sourceSystem, observationId, file, alreadyReceived in batch do
                                    if not stop then
                                        let! next, outcome = takeIn state sourceSystem observationId file alreadyReceived false
                                        state <- next

                                        match outcome with
                                        | Ok None -> ()
                                        | Ok(Some TakenIn) ->
                                            changed <- true
                                            received <- received + 1
                                        | Ok(Some(NotObservation reasons)) ->
                                            changed <- true
                                            invalid <- invalid @ [ sourceSystem, observationId, reasons ]
                                        | Error _ ->
                                            failed <- failed + 1
                                            // GitHub out of reach: the rest wait for the next pass.
                                            stop <- (match state.Basis with
                                                     | CachedAt _ -> true
                                                     | ReadAt _ -> false)

                                let waiting = work.Length - batch.Length

                                let status =
                                    { inboxStatus with
                                        Received = inboxStatus.Received + received
                                        Waiting = waiting
                                        Failed = failed
                                        Invalid = invalid }

                                let messages = finish state status changed

                                // More waits, and this pass got through: the next follows.
                                if waiting > 0 && changed && failed = 0 then
                                    serial inboxJob

                                return messages
            | _ -> return []
        }

    readInboxes <- fun () -> serial inboxJob

    /// Signing out discards this account's unsent changes from this device,
    /// as the person confirmed (WI-0058). Another account's are untouched,
    /// and so is an entry that may have landed (in flight, outcome unknown):
    /// it stays to be reconciled, never dropped (Arca's LCP-070 rule). The
    /// account is Chrona's actor id, never a display name.
    let discardJob () =
        async {
            match opened with
            | None -> return [ Update.UnsentDiscarded 0 ]
            | Some state ->
                // Matched by the account's stable id (Arca 0.4.0): an entry
                // from an earlier version that names no one is never
                // discarded here, nor one in flight or of unknown outcome.
                match accountOf state.Session with
                | None -> return [ Update.UnsentDiscarded 0; sync state false ]
                | Some who ->
                    let queue = stamped state.Queue

                    match state.Owned with
                    | Some owned ->
                        match! owned.DiscardAccount who queue with
                        | Ok(kept, count) ->
                            let saved = { state with Queue = kept }
                            opened <- Some saved
                            return [ Update.UnsentDiscarded count; sync saved false ]
                        | Error failure ->
                            let saved = { state with Note = Some(describeQueueStore failure) }
                            opened <- Some saved
                            return [ Update.UnsentDiscarded 0; sync saved false ]
                    | None ->
                        let kept, count = Arca.Limen.QueueSignOut.discardAccount who queue
                        let! saved = persist state kept
                        opened <- Some saved
                        return [ Update.UnsentDiscarded count; sync saved false ]
        }

    /// This tab now holds the queue: the person took it over from the tab
    /// that held it, or that tab closed. What that tab kept is read back and
    /// this tab's own unsent changes follow it, in order, renumbered after
    /// it; nothing is sent twice, because an entry that tab was sending
    /// becomes an outcome to reconcile, never a blind resend (ARCA-OFF-004,
    /// LCP-060).
    let adoptJob (owned: Arca.Limen.OwnedQueue) (lock: string) () =
        async {
            match opened with
            | Some state when lockOf state.Folder = lock ->
                held[lock] <- owned
                let mine = state.Queue.Entries |> List.filter unsent
                let! loaded = owned.Store.Load()
                let here = { state with Holder = HeldHere; Note = None; Notice = noticeOf owned }

                match loaded with
                | Error failure ->
                    let state = { here with Owned = None; Note = Some(describeQueueStore failure) }
                    opened <- Some state
                    return [ sync state false ]
                | Ok(Some kept) when kept.Entries |> List.exists earlier || (stamped kept).Entries |> List.exists (othersOf state.Session) ->
                    let state =
                        { here with
                            Owned = None
                            Note =
                                Some(
                                    if (stamped kept).Entries |> List.exists earlier then
                                        earlierNote
                                    else
                                        "Another account left changes in this browser that have not been sent; they are kept for that account."
                                ) }

                    opened <- Some state
                    return [ sync state false ]
                | Ok kept ->
                    let before = kept |> Option.map (stamped >> OfflineQueue.recover) |> Option.defaultValue (OfflineQueue.create OfflinePolicy.QueueWrites)
                    let queue = laidOver before mine

                    // Decisions were counted by the old numbers.
                    decided.Clear()

                    let here =
                        { here with
                            Owned = Some owned
                            Note = (if owned.Mode = Arca.Limen.DurabilityMode.MemoryOnly then Some memoryOnlyNote else None) }

                    let! state = persist here queue
                    opened <- Some state
                    let resumed = before.Entries |> List.filter unsent |> List.choose (requestOf state.Folder)

                    if queue.Entries |> List.exists unsent then
                        serial drain

                    return
                        [ Update.StoreOpened(contents state)
                          Update.StoreResumed resumed
                          sync state false ]
            | _ ->
                // The records closed or moved on meanwhile: let it go.
                do! owned.Release()
                return []
        }

    /// "Use this tab instead" (OQ-LIMEN-IDB-001): takes the queue over from
    /// the tab holding it, outside the job queue. That tab hears LockLost and
    /// its next save is fenced and writes nothing.
    /// The person decided about unsent changes from an earlier version that
    /// name no one: sent as theirs (each stamped with their account), or
    /// discarded where they cannot have landed; one in flight or of unknown
    /// outcome stays, to be reconciled. Then the queue is opened again.
    let earlierJob (send: bool) () =
        async {
            match opened with
            | Some state ->
                match held.TryGetValue(lockOf state.Folder), accountOf state.Session with
                | (true, owned), Some who ->
                    match! owned.Store.Load() with
                    | Ok(Some kept) ->
                        let decided =
                            if send then
                                { kept with
                                    Entries =
                                        kept.Entries
                                        |> List.map (fun entry ->
                                            if earlier entry then
                                                { entry with Operation = { entry.Operation with AccountId = Some(AccountId.toWire who.Account) } }
                                            else
                                                entry) }
                            else
                                let mayHaveLanded (entry: QueueEntry) =
                                    match entry.State with
                                    | EntryState.InFlight _
                                    | EntryState.OutcomeUnknown _ -> true
                                    | _ -> false

                                { kept with Entries = kept.Entries |> List.filter (fun entry -> not (earlier entry) || mayHaveLanded entry) }

                        match! owned.Store.Save decided with
                        | Ok() -> return! ready state
                        | Error failure ->
                            let state = { state with Note = Some(describeQueueStore failure) }
                            opened <- Some state
                            return [ sync state false ]
                    | _ -> return! ready state
                | _ -> return! ready state
            | None -> return []
        }

    let takeOverJob () =
        async {
            match opened with
            | Some state when state.Holder = HeldElsewhere false ->
                let state = { state with Holder = HeldElsewhere true }
                let lock = lockOf state.Folder
                opened <- Some state

                bridge.Start(
                    async {
                        let! options = queueOptions ()
                        match! Arca.Limen.LimenQueue.takeOver host options state.Folder with
                        | Arca.Limen.QueueOpening.Owned owned -> serial (adoptJob owned lock)
                        | _ ->
                            serial (fun () ->
                                async {
                                    match opened with
                                    | Some state when state.Holder = HeldElsewhere true ->
                                        let state = { state with Holder = HeldElsewhere false }
                                        opened <- Some state
                                        return [ sync state false ]
                                    | _ -> return []
                                })

                        return []
                    }
                )

                return [ sync state false ]
            | _ -> return []
        }

    /// The tab holding the queue closed: this tab asks for it again when the
    /// person returns to it, and holds it when no other tab does.
    let claimJob () =
        async {
            match opened with
            | Some state when state.Holder = HeldElsewhere false ->
                let! options = queueOptions ()
                match! Arca.Limen.LimenQueue.own host options state.Folder with
                | Arca.Limen.QueueOpening.Owned owned -> return! adoptJob owned (lockOf state.Folder) ()
                | _ -> return []
            | _ -> return []
        }

    /// The browser closed the queue's database under the page (cleared site
    /// data, eviction): it is opened again, and what this tab holds is kept
    /// there again. This tab's queue is the whole queue, so it is saved as
    /// it stands; whatever the browser lost is said (LocalQueueLost).
    let reconnectJob () =
        async {
            match opened with
            | Some state when state.Holder = HeldHere ->
                let lock = lockOf state.Folder

                match held.TryGetValue lock with
                | true, previous ->
                    held.Remove lock |> ignore
                    do! previous.Release()
                | _ -> ()

                let! options = queueOptions ()
                match! Arca.Limen.LimenQueue.own host options state.Folder with
                | Arca.Limen.QueueOpening.Owned owned ->
                    held[lock] <- owned
                    // Loaded first: the store reports what it found.
                    let! _ = owned.Store.Load()

                    let here =
                        { state with
                            Owned = Some owned
                            Note = (if owned.Mode = Arca.Limen.DurabilityMode.MemoryOnly then Some memoryOnlyNote else None)
                            Notice = noticeOf owned }

                    let! state = persist here state.Queue
                    opened <- Some state
                    return [ sync state false ]
                | Arca.Limen.QueueOpening.OwnedElsewhere ->
                    let state = { state with Owned = None; Holder = HeldElsewhere false; Note = None }
                    opened <- Some state
                    return [ sync state false ]
                | _ ->
                    let state = { state with Owned = None; Note = Some memoryOnlyNote }
                    opened <- Some state
                    return [ sync state false ]
            | _ -> return []
        }

    /// Another tab took the queue over ("use this tab instead" there): this
    /// tab no longer keeps it, and goes on with its own changes in the page.
    /// What this tab was sending is reconciled by that tab, never sent twice.
    let lostJob () =
        async {
            match opened with
            | Some state when state.Holder = HeldHere && state.Owned.IsSome ->
                held.Remove(lockOf state.Folder) |> ignore
                // Everything this tab held was kept: it is the other tab's now.
                handed.AddRange(state.Queue.Entries |> List.filter unsent |> List.map _.Operation.IdempotencyKey)

                let state =
                    { state with
                        Queue = { state.Queue with Entries = [] }
                        Owned = None
                        Holder = HeldElsewhere false
                        Note = None }
                opened <- Some state
                return [ sync state false ]
            | _ -> return []
        }

    /// What leaving the device does with the account's read cache, under the
    /// deployment's shared-device policy (LCP-070, LCP-086): it goes, unless
    /// the person kept their unsent work here under `ask`, so that the
    /// account opens offline and sees it in context.
    let leaveJob (choice: Update.UnsentChoice) (unsent: int) () =
        async {
            match lastOpen with
            | None -> return []
            | Some(config, session, _) ->
                let policy =
                    match config.SharedDevice with
                    | Deployment.Ask -> SharedDevicePolicy.Ask
                    | Deployment.DiscardOnSignOut -> SharedDevicePolicy.DiscardOnSignOut

                let count, chosen =
                    match choice with
                    | Update.NothingUnsent
                    | Update.SentUnsent -> 0, None
                    | Update.KeptUnsent -> max 1 unsent, Some SignOutChoice.Keep
                    | Update.DiscardedUnsent -> max 1 unsent, Some SignOutChoice.Discard

                match SignOut.plan policy count chosen with
                | Ok plan when plan.ClearCache ->
                    match! readCache () with
                    | Some cached -> do! cached.Store.Clear(CacheScope.Account session.ActorId) |> Async.Ignore
                    | None -> ()
                | _ -> ()

                return []
        }

    /// Clears Chrona's data from this browser: the queue and the read cache,
    /// for every account (LimenDevice.clear). Refused while anything of
    /// another account's waits unsent here; blocked while another tab holds
    /// the databases open.
    let clearJob () =
        async {
            let others =
                match opened with
                | Some state -> state.Queue.Entries |> List.exists (fun entry -> earlier entry || othersOf state.Session entry)
                | None -> false

            let otherKept =
                match opened with
                | Some state -> state.Note = Some "Another account left changes in this browser that have not been sent; they are kept for that account."
                | None -> false

            if others || otherKept then
                return
                    [ Update.DeviceCleared(
                          Error "Another account's unsent changes are kept in this browser; clearing it would lose them. That account can send them first."
                      ) ]
            else
                // This tab lets go of what it holds first.
                for owned in List.ofSeq held.Values do
                    do! owned.Release()

                held.Clear()

                match! Arca.Limen.LimenDevice.clear host with
                | Ok() ->
                    cache <- None
                    opened <- opened |> Option.map (fun state -> { state with Owned = None })
                    return [ Update.DeviceCleared(Ok()) ]
                | Error(_, ReadCacheFailure.Blocked) ->
                    return [ Update.DeviceCleared(Error "Another Chrona tab has this browser's data open. Close Chrona's other tabs, then try again.") ]
                | Error(name, _) -> return [ Update.DeviceCleared(Error $"This browser could not clear Chrona's data ({name}). Try again.") ]
        }

    // Until the engine opens storage (a deployment that configures a
    // location), commits are kept in memory and acknowledged at once.
    let memory = inMemory bridge
    let mutable requested = false

    /// How many of this account's changes wait in this browser for the
    /// organization's folder, when the records cannot be opened (WI-0055).
    let waiting (config: Deployment.DeploymentConfig) (session: Session) =
        async {
            match Storage.binding config with
            | Error _ -> return 0, None
            | Ok binding ->
                match Storage.organizationNamespace config binding session.OrganizationId with
                | Error _ -> return 0, None
                | Ok folder ->
                    let count (kept: Result<OfflineQueue option, QueueStoreFailure>) =
                        match kept with
                        | Ok(Some kept) ->
                            match accountOf session with
                            | Some who -> Arca.Limen.QueueSignOut.unsentOfAccount who (stamped kept)
                            | None -> 0
                        | _ -> 0

                    // Read under the queue's lock, and let go again: the tab
                    // holding it, if another does, shows its own count.
                    match held.TryGetValue(lockOf folder) with
                    | true, owned ->
                        let! kept = owned.Store.Load()
                        return count kept, None
                    | _ ->
                        let! options = queueOptions ()
                        match! Arca.Limen.LimenQueue.own host options folder with
                        | Arca.Limen.QueueOpening.Owned owned ->
                            // The first load may move an older localStorage queue.
                            let! kept = owned.Store.Load()
                            let notice = noticeOf owned
                            do! owned.Release()
                            return count kept, notice
                        | _ -> return 0, None
        }

    { Kind = InMemory
      Open =
        fun config session dates ->
            requested <- true
            lastOpen <- Some(config, session, dates)

            serial (fun () ->
                async {
                    let! messages = openJob config session dates ()

                    if messages |> List.exists (function Update.StoreUnavailable _ -> true | _ -> false) then
                        let! count, notice = waiting config session
                        return messages @ [ Update.UnsentWaiting(count, notice) ]
                    else
                        return messages
                })
      Confirm = fun () -> serial confirmJob
      Commit =
        fun request ->
            if requested then
                serial (commitJob request)
            else
                memory.Commit request
      Read = fun dates -> if requested then serial (readJob dates)
      Rebuild = fun () -> if requested then serial rebuildJob
      ReadInboxes = fun () -> if requested then readInboxes ()
      SendNow =
        fun () ->
            if requested then
                retryMs <- FirstRetryMs
                serial drain
      Discard = fun () -> if requested then serial discardJob else memory.Discard()
      TakeOver = fun () -> if requested then serial takeOverJob
      SendEarlier = fun () -> if requested then serial (earlierJob true)
      DiscardEarlier = fun () -> if requested then serial (earlierJob false)
      Claim = fun () -> if requested then serial claimJob
      Reconnect = fun () -> if requested then serial reconnectJob
      Lost = fun () -> if requested then serial lostJob
      SignedOut = fun choice unsent -> if requested then serial (leaveJob choice unsent)
      ClearDevice = fun () -> if requested then serial clearJob else memory.ClearDevice() }
