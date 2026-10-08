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
      Commit: StoreRequest -> unit }

/// A store that keeps nothing: every commit is acknowledged at once. For a
/// deployment that configures no location.
let inMemory (bridge: Bridge) : StorePort =
    { Kind = InMemory
      Open = fun _ _ _ -> bridge.Start(async { return [ Update.StoreUnavailable "This deployment stores nothing." ] })
      Confirm = fun () -> ()
      Commit = fun request -> bridge.Start(async { return [ Update.StoreAnswered(request.CommitId, Committed) ] }) }

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
                    | Refused _ -> return HttpOutcome.Failed HttpFailure.InvalidResponse
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
      /// Why the queue is not kept in this browser, for the person.
      Note: string option }

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
      Removed = request.RemovedMembers }

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
        | Answered _ -> return LocalStorageOutcome.Failure LocalStorageFailure.Unavailable
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
            RemovedMembers = changed.Removed })

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
          Problems = Stored.problems state.Stored }

    /// Reads the state again: the same months, plus any these dates add.
    let refresh (state: Opened) (dates: DateOnly list) =
        async {
            let months = state.Months + (dates |> List.map monthOf |> Set.ofList)

            match! load state.Provider state.Folder state.Session.ActorId months with
            | Error reason -> return Error reason
            | Ok(token, stored) ->
                let next =
                    { state with
                        Token = token
                        Stored = stored
                        Months = months }

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

    /// Others' independent changes are shown once nothing of this page's own
    /// is waiting to be decided; unsent changes are shown over them.
    let refreshed (state: Opened) =
        if jobs.Count = 0 then [ Update.StoreOpened(contents state) ] else []

    let sync (state: Opened) (offline: bool) =
        Update.SyncChanged
            { Offline = offline
              KeptInBrowser = state.Keeper.IsSome
              Note = state.Note }

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
                    let note =
                        match failure with
                        // Arca refuses to keep anything that looks like a credential.
                        | QueueStoreFailure.Corrupt _ -> "A change holds text that looks like a credential, so this browser does not keep unsent changes."
                        | other -> describeQueueStore other

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
                                            Stored = Stored.committed changed receipt (Stored.overlay changed state.Stored) }

                                    let again = decided.ContainsKey entry.Sequence
                                    return! finish next (Ok queue) (fun saved -> (if again then refreshed saved else []) @ [ answer Committed ])
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
            let keeper = LocalStorageQueue.store (localStorage bridge) LocalStorageQueue.DefaultBudget state.Folder

            let! state =
                async {
                    match! keeper.Load() with
                    | Ok None -> return { state with Keeper = Some keeper }
                    | Ok(Some kept) when
                        kept.Entries |> List.exists (fun entry -> unsent entry && entry.Operation.ActorId <> state.Session.ActorId)
                        ->
                        return
                            { state with
                                Keeper = None
                                Note = Some "Another account left changes in this browser that have not been sent; they are kept for that account." }
                    | Ok(Some kept) -> return { state with Queue = OfflineQueue.recover kept; Keeper = Some keeper }
                    | Error failure -> return { state with Keeper = None; Note = Some(describeQueueStore failure) }
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
                                              Note = None }

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

    // Until the engine opens storage (a deployment that configures a
    // Until the engine opens storage (a deployment that configures a
    // location), commits are kept in memory and acknowledged at once.
    let memory = inMemory bridge
    let mutable requested = false

    { Kind = InMemory
      Open =
        fun config session dates ->
            requested <- true
            serial (openJob config session dates)
      Confirm = fun () -> serial confirmJob
      Commit =
        fun request ->
            if requested then
                serial (commitJob request)
            else
                memory.Commit request }
