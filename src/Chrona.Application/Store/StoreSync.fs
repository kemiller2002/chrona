/// Sending the unsent changes (WI-0033): in order, one step at a time, each
/// step kept in this browser before and after it is taken (ARCA-OFF-004); a
/// change the repository moved under is decided again on what is stored now
/// (21), and GitHub out of reach is tried again after a back-off.
module internal Chrona.Application.StoreSync

open System
open System.Collections.Generic
open Arca
open Arca.GitHub
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Application.Bridge
open Chrona.Application.Store
open Chrona.Application.StoreState
open Chrona.Application.StoreUnsentQueue
open Chrona.Application.StoreIndex
open Chrona.Application.StorePage
open Chrona.Application.StoreReads

let sync (page: Page) (state: Opened) (offline: bool) =
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
          Earlier = page.EarlierWaiting }

/// Keeps the queue in this browser, write-ahead. When the browser
/// refuses, the queue goes on in this page only, and the person is told.
let persist (page: Page) (state: Opened) (queue: OfflineQueue) =
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
                page.Held.Remove(lockOf state.Folder) |> ignore
                let kept = state.Queue.Entries |> List.map _.Sequence |> Set.ofList
                page.Handed.AddRange(state.Queue.Entries |> List.filter unsent |> List.map _.Operation.IdempotencyKey)

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

/// How many times a queued change is sent before a moving repository
/// becomes a conflict for the person.
let attempts = 3

/// GitHub could not be reached: try again after a back-off, once.
let rec scheduleRetry (page: Page) () =
    if not page.Retrying then
        page.Retrying <- true
        let wait = page.RetryMs
        page.RetryMs <- min MaxRetryMs (page.RetryMs * 2)

        page.Bridge.Start(
            async {
                do! page.Bridge.Call(Sleep wait) |> Async.Ignore
                page.Retrying <- false
                serial page (drain page)
                return []
            }
        )

/// Sends the unsent changes in order, one step at a time, each step kept
/// in this browser before and after it is taken (ARCA-OFF-004). It stops
/// when nothing is left, or when GitHub cannot be reached (and tries
/// again later).
and drain (page: Page) () : Async<Update.Msg list> =
    let rec step (messages: Update.Msg list) =
        async {
            match page.Opened with
            | None -> return messages
            // Shown from the read cache: nothing is sent until the records
            // are read from GitHub again, and every change made meanwhile
            // is decided again on them first.
            | Some { Basis = CachedAt _ } ->
                let! reopened = page.Reopen ()
                return messages @ reopened
            | Some({ Basis = ReadAt token } as state) ->
                let save queue =
                    async {
                        let! saved = persist page state queue
                        page.Opened <- Some saved
                        return saved
                    }

                let offline (state: Opened) =
                    scheduleRetry page ()
                    messages @ [ sync page state true ]

                match OfflineQueue.next state.Queue with
                | None ->
                    page.RetryMs <- FirstRetryMs

                    if page.InboxDeferred then
                        page.InboxDeferred <- false
                        page.ReadInboxes ()

                    let! state = save (OfflineQueue.prune state.Queue)
                    return messages @ [ sync page state false ]
                | Some entry ->
                    let id = entry.Operation.IdempotencyKey
                    let answer outcome = Update.StoreAnswered(id, outcome)

                    /// The entry is done with: the queue is kept, then what
                    /// to tell the engine is decided on the state kept.
                    let finish (state: Opened) (queue: Result<OfflineQueue, QueueError>) (more: Opened -> Update.Msg list) =
                        async {
                            page.Decided.Remove entry.Sequence |> ignore

                            match queue with
                            | Ok queue ->
                                let! saved = persist page state queue
                                page.Opened <- Some saved
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
                                        Stored = StoredOverlay.committed changed receipt (StoredOverlay.overlay changed state.Stored)
                                        Index =
                                            match operation with
                                            | Ok operation -> indexAfter state.Index operation receipt
                                            | Error _ -> state.Index }

                                let! next =
                                    match operation with
                                    | Ok operation -> afterCommit page next operation receipt
                                    | Error _ -> async.Return next

                                let again = page.Decided.ContainsKey entry.Sequence

                                return!
                                    finish next (Ok queue) (fun saved ->
                                        (if again then refreshed page saved else [])
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
                        let tries = (match page.Decided.TryGetValue entry.Sequence with | true, n -> n | _ -> 0) + 1
                        page.Decided[entry.Sequence] <- tries

                        match recordsOf state.Folder entry with
                        | Error _ ->
                            return!
                                finish
                                    state
                                    (OfflineQueue.abandon entry.Sequence "its records cannot be read back" state.Queue)
                                    (fun _ -> [ answer (Failed "A queued change could not be read back and was set aside.") ])
                        | Ok changed ->
                            let dates = changed.Activities |> List.map (fun a -> a.Occurrence.LocalDate)

                            match! refresh page state dates with
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
                                            (fun saved -> refreshed page saved @ [ answer (Conflict divergences) ])
                                | Ok again when isEmpty again ->
                                    // An earlier attempt landed: nothing is left to send.
                                    return!
                                        finish
                                            fresh
                                            (OfflineQueue.abandon entry.Sequence "already stored" fresh.Queue)
                                            (fun saved -> refreshed page saved @ [ answer Committed ])
                                | Ok _ when tries >= attempts ->
                                    return!
                                        finish
                                            fresh
                                            (OfflineQueue.abandon entry.Sequence "the repository kept changing" fresh.Queue)
                                            (fun saved -> refreshed page saved @ [ answer (Conflict [ Reconcile.KeptChanging ]) ])
                                | Ok again ->
                                    let revised =
                                        StoredChanges.changes fresh.Stored again
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
                                        match OfflineQueue.revise entry.Sequence operation fresh.Queue with
                                        | Error _ -> return messages @ [ answer (Failed "The queued change could not be revised.") ]
                                        | Ok queue ->
                                            let! saved = persist page fresh queue
                                            page.Opened <- Some saved
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
