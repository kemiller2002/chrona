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

/// What the store needs from where the data lives.
[<NoComparison; NoEquality>]
type Backend =
    { /// The provider serving a location.
      Provider: DataLocation -> StorageProvider
      /// The repository's facts for the signed-in credential: its visibility
      /// and whether it may be read and written (ARCA-AUTH-003, 2.7). A
      /// refusal says why, for the person.
      Resolve: DataLocation -> Async<Result<CapabilitySnapshot, string>> }

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
      /// Send this account's unsent changes now.
      SendNow: unit -> unit
      /// Discard this account's unsent changes from this device.
      Discard: unit -> unit
      /// Take over this browser's unsent changes once the tab holding them
      /// closes (WI-0067).
      TakeOver: unit -> unit
      /// This tab no longer holds this browser's unsent changes.
      Lost: unit -> unit }

/// A store that keeps nothing: every commit is acknowledged at once. For a
/// deployment that configures no location.
let inMemory (bridge: Bridge) : StorePort =
    { Kind = InMemory
      Open = fun _ _ _ -> bridge.Start(async { return [ Update.StoreUnavailable "This deployment stores nothing." ] })
      Confirm = fun () -> ()
      Commit = fun request -> bridge.Start(async { return [ Update.StoreAnswered(request.CommitId, Committed) ] })
      Read = fun _ -> ()
      Rebuild = fun () -> ()
      SendNow = fun () -> ()
      Discard = fun () -> bridge.Start(async { return [ Update.UnsentDiscarded 0 ] })
      TakeOver = fun () -> ()
      Lost = fun () -> () }

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
    | ResolveError.CredentialUnavailable _ -> "You are not signed in to GitHub any more. Sign in again."
    | ResolveError.CredentialRejected -> "GitHub refused your sign-in. Sign in again."
    | ResolveError.RepositoryNotFound repository -> $"Your GitHub account cannot see {repository}."
    | ResolveError.Call _ -> "GitHub could not be reached."

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
                    | LockOutcome _ -> return HttpOutcome.Failed HttpFailure.InvalidResponse
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

/// The organization's records as opened.
[<NoComparison; NoEquality>]
type private Opened =
    { Session: Session
      Folder: Namespace
      Provider: StorageProvider
      Name: string
      /// The repository state the records were read at (21).
      Token: ChangeToken
      Stored: Stored.Stored
      /// The months read so far.
      Months: Set<int * int>
      /// The organization as the deployment configures it.
      Organization: Deployment.OrganizationConfig
      /// This page's unsent changes, in order: Arca's offline queue (WI-0033).
      Queue: OfflineQueue
      /// Where the queue is kept in this browser, when it can be.
      Keeper: QueueStore option
      /// Which tab holds this browser's queue (WI-0067).
      Holder: QueueHolder
      /// Why the queue is not kept in this browser, for the person.
      Note: string option
      /// The activity index as last read or written (WI-0034).
      Index: Indexed }

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
    | StorageFailure.RateLimited _ -> "GitHub's rate limit is used up for now. Try again shortly."
    | StorageFailure.WrongLocation _ -> "The store is not serving the configured repository."
    | StorageFailure.IntegrityRefused(path, _) -> $"{path} could not be changed safely: it is not a valid record."
    | StorageFailure.ProviderFailed(_, _, detail) -> $"GitHub could not be reached ({detail})."

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
      Audit = request.Audit }

/// Browser localStorage through Limen's Storage requests, as Arca's
/// LocalStorageQueue asks for it.
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
        | LockOutcome _ -> return LocalStorageOutcome.Failure LocalStorageFailure.Unavailable
    }

/// A Web Lock through Limen's coordination pack, as Arca's
/// LocalStorageQueue.own asks for it: at once, or once whoever holds it lets
/// go (`wait`). Without the pack, or Web Locks, the answer is unsupported.
let private lockWith (bridge: Bridge) (wait: bool) (QueueLockRequest.Acquire name) =
    async {
        match! bridge.Call(LockAcquire(name, wait)) with
        | LockOutcome "Acquired" -> return QueueLockOutcome.Acquired
        // A cancelled wait leaves the lock where it was.
        | LockOutcome("Busy" | "Cancelled") -> return QueueLockOutcome.Busy
        | _ -> return QueueLockOutcome.Unsupported
    }

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
            Audit = changed.Audit })

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

    /// Reads the folders these dates need.
    let read (provider: StorageProvider) (folder: Namespace) (actorId: string) (dates: DateOnly list) : Async<Result<StoredObject list * Diagnostic list, string>> =
        async {
            match Stored.folders actorId dates with
            | Error diagnostic -> return Error(code diagnostic)
            | Ok folders ->
                let mutable objects = []
                let mutable problems = []
                let mutable failure = None

                for path in folders do
                    if failure.IsNone then
                        match! provider.List folder path with
                        | Error error -> failure <- Some(describeFailure error)
                        | Ok listing ->
                            let files, incomplete = Persistence.recordFiles path listing
                            problems <- problems @ incomplete

                            for file in files do
                                if failure.IsNone then
                                    match! provider.Read folder file with
                                    | Ok(ReadOutcome.Found found) -> objects <- objects @ [ found ]
                                    | Ok ReadOutcome.Absent -> ()
                                    | Error error -> failure <- Some(describeFailure error)

                match failure with
                | Some reason -> return Error reason
                | None -> return Ok(objects, problems)
        }

    /// The repository state, then what the folders hold at it: validated,
    /// with records edited outside Chrona held for review.
    let load (provider: StorageProvider) (folder: Namespace) (actorId: string) (months: Set<int * int>) =
        async {
            match! provider.ChangeToken folder with
            | Error error -> return Error(describeFailure error)
            | Ok token ->
                match! read provider folder actorId (months |> Set.toList |> List.map firstOf) with
                | Error reason -> return Error reason
                | Ok(objects, incomplete) ->
                    let stored = Stored.load objects
                    let mutable histories = Map.empty
                    let mutable failure = None

                    for KeyValue(_, found) in stored.Activities.Activities do
                        if failure.IsNone then
                            match! provider.History folder found.Path with
                            | Ok history -> histories <- histories.Add(RelativePath.render found.Path, history)
                            | Error error -> failure <- Some(describeFailure error)

                    match failure with
                    | Some reason -> return Error reason
                    | None ->
                        let activities = Persistence.holdExternalEdits histories stored.Activities

                        return
                            Ok(
                                token,
                                { stored with
                                    Activities = { activities with Problems = incomplete @ activities.Problems } }
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

        { Name = state.Name
          Activities = state.Stored.Activities.Activities |> Map.toList |> List.map (fun (_, found) -> found.Activity)
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

    /// Reads the state again: the same months, plus any these dates add.
    let refresh (state: Opened) (dates: DateOnly list) =
        async {
            let months = state.Months + (dates |> List.map monthOf |> Set.ofList)

            match! load state.Provider state.Folder state.Session.ActorId months with
            | Error reason -> return Error reason
            | Ok(token, stored) ->
                let! indexed = readIndex state.Provider state.Folder state.Session.ActorId months stored state.Index

                let next =
                    { state with
                        Token = token
                        Stored = stored
                        Months = months
                        Index = indexed }

                opened <- Some next
                return Ok next
        }

    // ---- The queue of unsent changes (WI-0033) ----------------------------------

    let isEmpty (changed: Stored.Changed) =
        changed.Activities.IsEmpty
        && changed.References.IsEmpty
        && changed.Attestations.IsEmpty
        && changed.Members.IsEmpty
        && changed.Removed.IsEmpty
        && changed.Audit.IsEmpty

    /// Others' independent changes are shown once nothing of this page's own
    /// is waiting to be decided; unsent changes are shown over them.
    let refreshed (state: Opened) =
        if jobs.Count = 0 then [ Update.StoreOpened(contents state) ] else []

    let sync (state: Opened) (offline: bool) =
        Update.SyncChanged
            { Offline = offline
              KeptInBrowser = state.Keeper.IsSome
              Note = state.Note
              Holder = state.Holder }

    /// Keeps the queue in this browser, write-ahead. When the browser
    /// refuses, the queue goes on in this page only, and the person is told.
    let persist (state: Opened) (queue: OfflineQueue) =
        async {
            match state.Keeper with
            | None -> return { state with Queue = queue }
            | Some keeper ->
                match! keeper.Save queue with
                | Ok() -> return { state with Queue = queue }
                | Error failure ->
                    let! note =
                        async {
                            match failure with
                            // Arca refuses to keep anything that looks like a credential.
                            | QueueStoreFailure.Corrupt _ -> return "A change holds text that looks like a credential, so this browser does not keep unsent changes."
                            // A fenced save is refused when another tab saved
                            // since this one read: the browser still keeps
                            // changes, but this tab's would overwrite that
                            // tab's, so nothing was written.
                            | QueueStoreFailure.Unavailable ->
                                match! (LocalStorageQueue.store (localStorage bridge) LocalStorageQueue.DefaultBudget state.Folder).Load() with
                                | Ok _ -> return "Another Chrona tab saved its unsent changes in this browser, so this tab keeps its own in this page instead of overwriting them."
                                | Error other -> return describeQueueStore other
                            | other -> return describeQueueStore other
                        }

                    return
                        { state with
                            Queue = queue
                            Keeper = None
                            Note = Some note }
        }

    let mutable retryMs = FirstRetryMs
    let mutable retrying = false
    /// How many times each entry was decided again, by sequence.
    let decided = Dictionary<int64, int>()

    /// How many times a queued change is sent before a moving repository
    /// becomes a conflict for the person.
    let attempts = 3

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
                | Some state ->
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
                                    let next =
                                        { state with
                                            Token = receipt.ChangeToken
                                            Stored = Stored.committed changed receipt (Stored.overlay changed state.Stored)
                                            Index =
                                                match OfflineQueue.operationOf state.Folder entry.Operation with
                                                | Ok operation -> indexAfter state.Index operation receipt
                                                | Error _ -> state.Index }

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
                                            |> Result.map (Operation.requireChangeToken fresh.Token)

                                        match revised with
                                        | Error diagnostics ->
                                            return!
                                                finish
                                                    fresh
                                                    (OfflineQueue.abandon entry.Sequence "it cannot be stored" fresh.Queue)
                                                    (fun _ -> [ answer (Failed $"This change cannot be stored ({describeAll diagnostics}).") ])
                                        | Ok operation ->
                                            match OfflineQueue.revise entry.Sequence operation fresh.Queue with
                                            | Error _ -> return messages @ [ answer (Failed "The queued change could not be revised.") ]
                                            | Ok queue ->
                                                let! saved = persist fresh queue
                                                opened <- Some saved
                                                return! step messages
                        | _, Ok operation ->
                            // Write-ahead: in flight is kept before it is sent.
                            match OfflineQueue.markInFlight entry.Sequence state.Token state.Queue with
                            | Error _ -> return messages @ [ answer (Failed "The queued change could not be sent.") ]
                            | Ok inFlight ->
                                let! state = save inFlight
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
            /// The queue this browser keeps, laid under this tab's own.
            let keep (keeper: QueueStore) (holder: QueueHolder) (state: Opened) =
                async {
                    match! keeper.Load() with
                    | Ok None -> return { state with Keeper = Some keeper; Holder = holder }
                    | Ok(Some kept) when
                        kept.Entries |> List.exists (fun entry -> unsent entry && entry.Operation.ActorId <> state.Session.ActorId)
                        ->
                        return
                            { state with
                                Keeper = None
                                Holder = holder
                                Note = Some "Another account left changes in this browser that have not been sent; they are kept for that account." }
                    | Ok(Some kept) -> return { state with Queue = OfflineQueue.recover kept; Keeper = Some keeper; Holder = holder }
                    | Error failure -> return { state with Keeper = None; Holder = holder; Note = Some(describeQueueStore failure) }
                }

            // One tab holds this browser's queue: only it loads, keeps and
            // sends what is kept (WI-0067).
            let! state =
                async {
                    match! LocalStorageQueue.own (lockWith bridge false) (localStorage bridge) LocalStorageQueue.DefaultBudget state.Folder with
                    | QueueOwnership.Owned keeper -> return! keep keeper HeldHere state
                    | QueueOwnership.OwnedElsewhere -> return { state with Keeper = None; Holder = HeldElsewhere false; Note = None }
                    // No Web Locks: every save is fenced instead, so one tab's
                    // save never overwrites another's.
                    | QueueOwnership.OwnershipUnsupported ->
                        return! keep (LocalStorageQueue.store (localStorage bridge) LocalStorageQueue.DefaultBudget state.Folder) Unlocked state
                }

            opened <- Some state
            retryMs <- FirstRetryMs
            let resumed = state.Queue.Entries |> List.filter unsent |> List.choose (requestOf state.Folder)

            if resumed.IsEmpty then
                return [ Update.StoreOpened(contents state); sync state false ]
            else
                serial drain
                return [ Update.StoreOpened(contents state); Update.StoreResumed resumed; sync state false ]
        }

    /// Opens a folder, initializing it first when it is new.
    let ensure (provider: StorageProvider) (folder: Namespace) (initialize: unit -> Result<Operation, Diagnostic list>) =
        async {
            let readManifest () =
                async {
                    match Layout.manifestPath with
                    | Error error -> return Error(LocationError.describe error)
                    | Ok path ->
                        match! provider.Read folder path with
                        | Error failure -> return Error(describeFailure failure)
                        | Ok stored -> return Ok(Storage.openNamespace folder stored)
                }

            match! readManifest () with
            | Error reason -> return Error reason
            | Ok(Ok _) -> return Ok()
            | Ok(Error [ NamespaceNotInitialized _ ]) ->
                match initialize () with
                | Error [ PublicProductionRepository ] ->
                    return Error "Production records are never started in a public repository. Use a private repository."
                | Error diagnostics -> return Error($"The records could not be set up ({describeAll diagnostics}).")
                | Ok operation ->
                    match! provider.Commit operation with
                    | Ok _ -> return Ok()
                    | Error failure -> return Error(describeFailure failure)
            | Ok(Error diagnostics) -> return Error($"The configured folder cannot be used ({describeAll diagnostics}).")
        }

    /// The repository, if the signed-in account can keep records in it.
    let resolve (location: DataLocation) =
        async {
            match! backend.Resolve location with
            | Error reason -> return Error reason
            | Ok snapshot when not snapshot.CanRead -> return Error $"Your GitHub account cannot read {snapshot.Repository}."
            | Ok snapshot when not snapshot.CanWrite -> return Error $"Your GitHub account cannot write to {snapshot.Repository}."
            | Ok snapshot when snapshot.Archived -> return Error $"{snapshot.Repository} is archived."
            | Ok snapshot ->
                match snapshot.Branch with
                | BranchAccess.Writable -> return Ok snapshot
                | BranchAccess.Missing -> return Error $"The data branch {BranchName.value location.Branch} does not exist in {snapshot.Repository}."
                | BranchAccess.NotWritable _ -> return Error $"The data branch {BranchName.value location.Branch} does not accept direct changes."
        }

    /// Makes the session's person the organization's administrator: the
    /// founder of a new organization, or a listed account confirming one
    /// that has no listed administrator (Governance).
    let appoint (state: Opened) =
        async {
            let roster = Stored.roster state.Session.OrganizationId state.Stored
            let membership = Governance.administrator (principalOf state.Session) roster
            let changed = { Stored.nothing with Members = [ membership ] }

            match Stored.changes state.Stored changed, operationContext state.Session (newKey "appoint") (now ()) with
            | Ok changes, Ok context ->
                match Storage.operation state.Folder context "appoint an administrator" changes with
                | Error diagnostics -> return Error($"The administrator could not be recorded ({describeAll diagnostics}).")
                | Ok operation ->
                    match! state.Provider.Commit(Operation.requireChangeToken state.Token operation) with
                    | Ok receipt ->
                        return
                            Ok
                                { state with
                                    Token = receipt.ChangeToken
                                    Stored = Stored.committed changed receipt state.Stored }
                    | Error failure -> return Error(describeFailure failure)
            | Error diagnostics, _
            | _, Error diagnostics -> return Error($"The administrator could not be recorded ({describeAll diagnostics}).")
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

    let openJob (config: Deployment.DeploymentConfig) (session: Session) (dates: DateOnly list) () =
        async {
            let at = now ()

            let prepared =
                match Storage.binding config, Deployment.organization config session.OrganizationId with
                | Ok binding, Some organization ->
                    match Storage.applicationNamespace binding, Storage.organizationNamespace config binding organization.Id with
                    | Ok application, Ok folder -> Ok(binding, application, folder, organization)
                    | Error diagnostic, _
                    | _, Error diagnostic -> Error(code diagnostic)
                | Error diagnostic, _ -> Error(code diagnostic)
                | _, None -> Error $"the deployment does not serve {session.OrganizationId}"

            let context () = operationContext session (newKey "open") at

            match prepared with
            | Error reason -> return [ Update.StoreUnavailable $"This deployment's storage is not configured correctly ({reason})." ]
            | Ok(binding, application, folder, organization) ->
                match! resolve binding.Location with
                | Error reason -> return [ Update.StoreUnavailable reason ]
                | Ok home ->
                    let applicationProvider = backend.Provider binding.Location

                    match!
                        ensure applicationProvider application (fun () ->
                            context () |> Result.bind (Storage.initializeApplication binding home.Visibility None))
                    with
                    | Error reason -> return [ Update.StoreUnavailable reason ]
                    | Ok() ->
                        let separate = folder.Location <> binding.Location
                        let! own = if separate then resolve folder.Location else async.Return(Ok home)

                        match own with
                        | Error reason -> return [ Update.StoreUnavailable reason ]
                        | Ok repository ->
                            let provider = if separate then backend.Provider folder.Location else applicationProvider
                            let manifest = Organization.create organization.Id organization.DisplayName organization.Slug organization.TimeZone at

                            let! existing =
                                async {
                                    match Layout.manifestPath with
                                    | Error error -> return Error(LocationError.describe error)
                                    | Ok path ->
                                        match! provider.Read folder path with
                                        | Ok ReadOutcome.Absent -> return Ok false
                                        | Ok(ReadOutcome.Found _) -> return Ok true
                                        | Error failure -> return Error(describeFailure failure)
                                }

                            let decideFor isNew roster =
                                Governance.decide config.Environment organization isNew roster session.ActorId

                            let nobody: Access.Roster = { OrganizationId = organization.Id; Members = Map.empty }

                            match existing, decideFor true nobody with
                            | Error reason, _ -> return [ Update.StoreUnavailable reason ]
                            // Nothing is written for someone who may not set it up.
                            | Ok false, Governance.Refused reason -> return [ Update.StoreUnavailable reason ]
                            | Ok isExisting, _ ->
                                match!
                                    ensure provider folder (fun () ->
                                        context ()
                                        |> Result.bind (fun context -> Storage.initializeOrganization config binding repository.Visibility None context manifest))
                                with
                                | Error reason -> return [ Update.StoreUnavailable reason ]
                                | Ok() ->
                                    let months = dates |> List.map monthOf |> Set.ofList

                                    match! load provider folder session.ActorId months with
                                    | Error reason -> return [ Update.StoreUnavailable reason ]
                                    | Ok(token, stored) ->
                                        let state =
                                            { Session = session
                                              Folder = folder
                                              Provider = provider
                                              Name = string folder.Location.Repository
                                              Token = token
                                              Stored = stored
                                              Months = months
                                              Organization = organization
                                              Queue = OfflineQueue.create OfflinePolicy.QueueWrites
                                              Keeper = None
                                              Holder = HeldHere
                                              Note = None
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

                                        let roster = Stored.roster organization.Id stored

                                        match decideFor (not isExisting) roster with
                                        | Governance.Proceed -> return! ready state
                                        | Governance.Found ->
                                            match! appoint state with
                                            | Error reason -> return [ Update.StoreUnavailable reason ]
                                            | Ok state -> return! ready state
                                        | Governance.NeedsConfirmation canConfirm ->
                                            // Nothing is granted: the records stay closed
                                            // until a listed account confirms.
                                            opened <- None
                                            pending <- Some state
                                            return [ Update.StoreNeedsConfirmation(confirmationReason organization canConfirm, canConfirm) ]
                                        | Governance.Refused reason -> return [ Update.StoreUnavailable reason ]
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
                                match OfflineQueue.enqueue (now ()) (Operation.requireChangeToken state.Token operation) state.Queue with
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

    /// Signing out discards this account's unsent changes from this device,
    /// as the person confirmed (WI-0058). Another account's are untouched.
    let discardJob () =
        async {
            match opened with
            | None -> return [ Update.UnsentDiscarded 0 ]
            | Some state ->
                let mine, others =
                    state.Queue.Entries
                    |> List.partition (fun entry -> unsent entry && entry.Operation.ActorId = state.Session.ActorId)

                let! saved = persist state { state.Queue with Entries = others |> List.filter unsent }
                opened <- Some saved
                return [ Update.UnsentDiscarded mine.Length; sync saved false ]
        }

    /// The tab that held this browser's queue closed, and this tab took the
    /// lock: it now holds the queue. What that tab kept is read back and
    /// this tab's own unsent changes follow it, in order, renumbered after
    /// it; nothing is sent twice, because an entry that tab was sending
    /// becomes an outcome to reconcile, never a blind resend (ARCA-OFF-004).
    let adoptJob (keeper: QueueStore) (lock: string) () =
        async {
            match opened with
            | Some state when LocalStorageQueue.lockName state.Folder = lock ->
                let mine = state.Queue.Entries |> List.filter unsent
                let here = { state with Holder = HeldHere; Note = None }

                match! keeper.Load() with
                | Error failure ->
                    let state = { here with Keeper = None; Note = Some(describeQueueStore failure) }
                    opened <- Some state
                    return [ sync state false ]
                | Ok(Some kept) when kept.Entries |> List.exists (fun entry -> unsent entry && entry.Operation.ActorId <> state.Session.ActorId) ->
                    let state =
                        { here with
                            Keeper = None
                            Note = Some "Another account left changes in this browser that have not been sent; they are kept for that account." }

                    opened <- Some state
                    return [ sync state false ]
                | Ok kept ->
                    let before = kept |> Option.map OfflineQueue.recover |> Option.defaultValue (OfflineQueue.create OfflinePolicy.QueueWrites)

                    let queue =
                        { before with
                            NextSequence = before.NextSequence + int64 mine.Length
                            Entries = before.Entries @ (mine |> List.mapi (fun index entry -> { entry with Sequence = before.NextSequence + int64 index })) }

                    // Decisions were counted by the old numbers.
                    decided.Clear()
                    let! state = persist { here with Keeper = Some keeper } queue
                    opened <- Some state
                    let resumed = before.Entries |> List.filter unsent |> List.choose (requestOf state.Folder)

                    if queue.Entries |> List.exists unsent then
                        serial drain

                    return
                        [ Update.StoreOpened(contents state)
                          Update.StoreResumed resumed
                          sync state false ]
            | _ -> return []
        }

    /// Waits, outside the job queue, for the tab holding this browser's
    /// queue to close; the lock passes to this tab when it does.
    let takeOverJob () =
        async {
            match opened with
            | Some state when state.Holder = HeldElsewhere false ->
                let state = { state with Holder = HeldElsewhere true }
                let lock = LocalStorageQueue.lockName state.Folder
                opened <- Some state

                bridge.Start(
                    async {
                        match! LocalStorageQueue.own (lockWith bridge true) (localStorage bridge) LocalStorageQueue.DefaultBudget state.Folder with
                        | QueueOwnership.Owned keeper -> serial (adoptJob keeper lock)
                        | QueueOwnership.OwnedElsewhere
                        | QueueOwnership.OwnershipUnsupported ->
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

    /// Another context took the lock (Chrona never steals one): this tab no
    /// longer keeps the queue, and goes on with its own changes in the page.
    /// A change both tabs then send is found already stored, not doubled.
    let lostJob () =
        async {
            match opened with
            | Some state when state.Holder = HeldHere && state.Keeper.IsSome ->
                let state = { state with Keeper = None; Holder = HeldElsewhere false }
                opened <- Some state
                return [ sync state false ]
            | _ -> return []
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
            | Error _ -> return 0
            | Ok binding ->
                match Storage.organizationNamespace config binding session.OrganizationId with
                | Error _ -> return 0
                | Ok folder ->
                    let keeper = LocalStorageQueue.store (localStorage bridge) LocalStorageQueue.DefaultBudget folder

                    match! keeper.Load() with
                    | Ok(Some kept) ->
                        return kept.Entries |> List.filter (fun entry -> unsent entry && entry.Operation.ActorId = session.ActorId) |> List.length
                    | _ -> return 0
        }

    { Kind = InMemory
      Open =
        fun config session dates ->
            requested <- true

            serial (fun () ->
                async {
                    let! messages = openJob config session dates ()

                    if messages |> List.exists (function Update.StoreUnavailable _ -> true | _ -> false) then
                        let! count = waiting config session
                        return messages @ [ Update.UnsentWaiting count ]
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
      SendNow =
        fun () ->
            if requested then
                retryMs <- FirstRetryMs
                serial drain
      Discard = fun () -> if requested then serial discardJob else memory.Discard()
      TakeOver = fun () -> if requested then serial takeOverJob
      Lost = fun () -> if requested then serial lostJob }
