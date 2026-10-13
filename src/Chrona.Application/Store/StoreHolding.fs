/// Holding this browser's queue of unsent changes (WI-0059, WI-0067): one
/// tab holds it; the records open with what it keeps laid under this page's
/// own changes; it moves between tabs, is opened again when the browser
/// closes it, and is discarded or sent as the person decides.
module internal Chrona.Application.StoreHolding

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
open Chrona.Application.StoreSync

/// The records are open: unsent changes this browser kept for this
/// person are laid over them and sent, in order, after the first render
/// (20, 23). Another account's unsent changes are never sent with this
/// person's credential: they stay, untouched, for that account.
let ready (page: Page) (state: Opened) =
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
                | Ok None -> return! persist page kept kept.Queue
                // From an earlier version, naming no one: nothing is sent
                // or discarded until the person decides.
                | Ok(Some queue) when queue.Entries |> List.exists earlier ->
                    page.EarlierWaiting <- queue.Entries |> List.filter earlier |> List.length

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
                    page.Decided.Clear()
                    return! persist page kept (laidOver (OfflineQueue.recover queue) mine)
                | Error failure -> return { state with Owned = None; Notice = notice; Note = Some(describeQueueStore failure) }
            }

        // One tab holds this browser's queue: only it loads, keeps and
        // sends what is kept (WI-0067, LCP-059). A queue this page already
        // holds is not asked for again (opening the records again would
        // otherwise read as another tab's).
        let lock = lockOf state.Folder
        page.EarlierWaiting <- 0

        let! state =
            async {
                match page.Held.TryGetValue lock with
                | true, owned -> return! keep owned state
                | _ ->
                    let! options = queueOptions page ()
                    match! Arca.Limen.LimenQueue.own page.Host options state.Folder with
                    | Arca.Limen.QueueOpening.Owned owned ->
                        page.Held[lock] <- owned
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

        page.Opened <- Some state
        page.RetryMs <- FirstRetryMs
        let resumed = state.Queue.Entries |> List.filter unsent |> List.choose (requestOf state.Folder)

        match state.Basis with
        // From the read cache: GitHub is tried again after a back-off.
        | CachedAt _ ->
            scheduleRetry page ()
            return [ Update.StoreOpened(contents state); Update.StoreResumed resumed; sync page state true ]
        | ReadAt _ when resumed.IsEmpty ->
            page.ReadInboxes ()
            return [ Update.StoreOpened(contents state); sync page state false ]
        | ReadAt _ ->
            page.InboxDeferred <- true
            serial page (drain page)
            return [ Update.StoreOpened(contents state); Update.StoreResumed resumed; sync page state false ]
    }

/// Signing out discards this account's unsent changes from this device,
/// as the person confirmed (WI-0058). Another account's are untouched,
/// and so is an entry that may have landed (in flight, outcome unknown):
/// it stays to be reconciled, never dropped (Arca's LCP-070 rule). The
/// account is Chrona's actor id, never a display name.
let discardJob (page: Page) () =
    async {
        match page.Opened with
        | None -> return [ Update.UnsentDiscarded 0 ]
        | Some state ->
            // Matched by the account's stable id (Arca 0.4.0): an entry
            // from an earlier version that names no one is never
            // discarded here, nor one in flight or of unknown outcome.
            match accountOf state.Session with
            | None -> return [ Update.UnsentDiscarded 0; sync page state false ]
            | Some who ->
                let queue = stamped state.Queue

                match state.Owned with
                | Some owned ->
                    match! owned.DiscardAccount who queue with
                    | Ok(kept, count) ->
                        let saved = { state with Queue = kept }
                        page.Opened <- Some saved
                        return [ Update.UnsentDiscarded count; sync page saved false ]
                    | Error failure ->
                        let saved = { state with Note = Some(describeQueueStore failure) }
                        page.Opened <- Some saved
                        return [ Update.UnsentDiscarded 0; sync page saved false ]
                | None ->
                    let kept, count = Arca.Limen.QueueSignOut.discardAccount who queue
                    let! saved = persist page state kept
                    page.Opened <- Some saved
                    return [ Update.UnsentDiscarded count; sync page saved false ]
    }

/// This tab now holds the queue: the person took it over from the tab
/// that held it, or that tab closed. What that tab kept is read back and
/// this tab's own unsent changes follow it, in order, renumbered after
/// it; nothing is sent twice, because an entry that tab was sending
/// becomes an outcome to reconcile, never a blind resend (ARCA-OFF-004,
/// LCP-060).
let adoptJob (page: Page) (owned: Arca.Limen.OwnedQueue) (lock: string) () =
    async {
        match page.Opened with
        | Some state when lockOf state.Folder = lock ->
            page.Held[lock] <- owned
            let mine = state.Queue.Entries |> List.filter unsent
            let! loaded = owned.Store.Load()
            let here = { state with Holder = HeldHere; Note = None; Notice = noticeOf owned }

            match loaded with
            | Error failure ->
                let state = { here with Owned = None; Note = Some(describeQueueStore failure) }
                page.Opened <- Some state
                return [ sync page state false ]
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

                page.Opened <- Some state
                return [ sync page state false ]
            | Ok kept ->
                let before = kept |> Option.map (stamped >> OfflineQueue.recover) |> Option.defaultValue (OfflineQueue.create OfflinePolicy.QueueWrites)
                let queue = laidOver before mine

                // Decisions were counted by the old numbers.
                page.Decided.Clear()

                let here =
                    { here with
                        Owned = Some owned
                        Note = (if owned.Mode = Arca.Limen.DurabilityMode.MemoryOnly then Some memoryOnlyNote else None) }

                let! state = persist page here queue
                page.Opened <- Some state
                let resumed = before.Entries |> List.filter unsent |> List.choose (requestOf state.Folder)

                if queue.Entries |> List.exists unsent then
                    serial page (drain page)

                return
                    [ Update.StoreOpened(contents state)
                      Update.StoreResumed resumed
                      sync page state false ]
        | _ ->
            // The records closed or moved on meanwhile: let it go.
            do! owned.Release()
            return []
    }

/// The person decided about unsent changes from an earlier version that
/// name no one: sent as theirs (each stamped with their account), or
/// discarded where they cannot have landed; one in flight or of unknown
/// outcome stays, to be reconciled. Then the queue is opened again.
let earlierJob (page: Page) (send: bool) () =
    async {
        match page.Opened with
        | Some state ->
            match page.Held.TryGetValue(lockOf state.Folder), accountOf state.Session with
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
                                            // Theirs, as they said: made by them, of their account.
                                            { entry with
                                                Operation =
                                                    { entry.Operation with
                                                        ActorId = state.Session.ActorId
                                                        AccountId = Some(AccountId.toWire who.Account) } }
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
                    | Ok() -> return! ready page state
                    | Error failure ->
                        let state = { state with Note = Some(describeQueueStore failure) }
                        page.Opened <- Some state
                        return [ sync page state false ]
                | _ -> return! ready page state
            | _ -> return! ready page state
        | None -> return []
    }

/// "Use this tab instead" (OQ-LIMEN-IDB-001): takes the queue over from
/// the tab holding it, outside the job queue. That tab hears LockLost and
/// its next save is fenced and writes nothing.
let takeOverJob (page: Page) () =
    async {
        match page.Opened with
        | Some state when state.Holder = HeldElsewhere false ->
            let state = { state with Holder = HeldElsewhere true }
            let lock = lockOf state.Folder
            page.Opened <- Some state

            page.Bridge.Start(
                async {
                    let! options = queueOptions page ()
                    match! Arca.Limen.LimenQueue.takeOver page.Host options state.Folder with
                    | Arca.Limen.QueueOpening.Owned owned -> serial page (adoptJob page owned lock)
                    | _ ->
                        serial page (fun () ->
                            async {
                                match page.Opened with
                                | Some state when state.Holder = HeldElsewhere true ->
                                    let state = { state with Holder = HeldElsewhere false }
                                    page.Opened <- Some state
                                    return [ sync page state false ]
                                | _ -> return []
                            })

                    return []
                }
            )

            return [ sync page state false ]
        | _ -> return []
    }

/// The tab holding the queue closed: this tab asks for it again when the
/// person returns to it, and holds it when no other tab does.
let claimJob (page: Page) () =
    async {
        match page.Opened with
        | Some state when state.Holder = HeldElsewhere false ->
            let! options = queueOptions page ()
            match! Arca.Limen.LimenQueue.own page.Host options state.Folder with
            | Arca.Limen.QueueOpening.Owned owned -> return! adoptJob page owned (lockOf state.Folder) ()
            | _ -> return []
        | _ -> return []
    }

/// The browser closed the queue's database under the page (cleared site
/// data, eviction): it is opened again, and what this tab holds is kept
/// there again. This tab's queue is the whole queue, so it is saved as
/// it stands; whatever the browser lost is said (LocalQueueLost).
let reconnectJob (page: Page) () =
    async {
        match page.Opened with
        | Some state when state.Holder = HeldHere ->
            let lock = lockOf state.Folder

            match page.Held.TryGetValue lock with
            | true, previous ->
                page.Held.Remove lock |> ignore
                do! previous.Release()
            | _ -> ()

            let! options = queueOptions page ()
            match! Arca.Limen.LimenQueue.own page.Host options state.Folder with
            | Arca.Limen.QueueOpening.Owned owned ->
                page.Held[lock] <- owned
                // Loaded first: the store reports what it found.
                let! _ = owned.Store.Load()

                let here =
                    { state with
                        Owned = Some owned
                        Note = (if owned.Mode = Arca.Limen.DurabilityMode.MemoryOnly then Some memoryOnlyNote else None)
                        Notice = noticeOf owned }

                let! state = persist page here state.Queue
                page.Opened <- Some state
                return [ sync page state false ]
            | Arca.Limen.QueueOpening.OwnedElsewhere ->
                let state = { state with Owned = None; Holder = HeldElsewhere false; Note = None }
                page.Opened <- Some state
                return [ sync page state false ]
            | _ ->
                let state = { state with Owned = None; Note = Some memoryOnlyNote }
                page.Opened <- Some state
                return [ sync page state false ]
        | _ -> return []
    }

/// Another tab took the queue over ("use this tab instead" there): this
/// tab no longer keeps it, and goes on with its own changes in the page.
/// What this tab was sending is reconciled by that tab, never sent twice.
let lostJob (page: Page) () =
    async {
        match page.Opened with
        | Some state when state.Holder = HeldHere && state.Owned.IsSome ->
            page.Held.Remove(lockOf state.Folder) |> ignore
            // Everything this tab held was kept: it is the other tab's now.
            page.Handed.AddRange(state.Queue.Entries |> List.filter unsent |> List.map _.Operation.IdempotencyKey)

            let state =
                { state with
                    Queue = { state.Queue with Entries = [] }
                    Owned = None
                    Holder = HeldElsewhere false
                    Note = None }
            page.Opened <- Some state
            return [ sync page state false ]
        | _ -> return []
    }

/// How many of this account's changes wait in this browser for the
/// organization's folder, when the records cannot be opened (WI-0055).
let waiting (page: Page) (config: Deployment.DeploymentConfig) (session: Session) =
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
                match page.Held.TryGetValue(lockOf folder) with
                | true, owned ->
                    let! kept = owned.Store.Load()
                    return count kept, None
                | _ ->
                    let! options = queueOptions page ()
                    match! Arca.Limen.LimenQueue.own page.Host options folder with
                    | Arca.Limen.QueueOpening.Owned owned ->
                        // The first load may move an older localStorage queue.
                        let! kept = owned.Store.Load()
                        let notice = noticeOf owned
                        do! owned.Release()
                        return count kept, notice
                    | _ -> return 0, None
    }
