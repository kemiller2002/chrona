/// One pass over the organization's inboxes (requirement 18), each file made
/// durable in one commit conditioned on the records as read.
module internal Chrona.Application.InboxPass

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
open Chrona.Application.InboxReading

/// How many inbox files one pass takes in (WI-0038): a pass holds the
/// store's turn, so a person's own changes wait at most this many commits.
[<Literal>]
let InboxBatch = 20

/// One inbox file, made durable in one commit conditioned on the records
/// as read: the candidate, its receipt and the file's removal land
/// together or not at all, so a receipt never claims a candidate that is
/// not stored (expansion 19). When the repository moved meanwhile, it is
/// read again and the file decided again on it, once.
let rec takeIn (page: Page) (state: Opened) (sourceSystem: string) (observationId: string) (path: RelativePath) (alreadyReceived: bool) (again: bool) =
    async {
        let! decided =
            if alreadyReceived then
                async {
                    match! state.Provider.Read state.Folder path with
                    | Error failure -> return Error failure
                    | Ok ReadOutcome.Absent
                    | Ok(ReadOutcome.Erased _) -> return Ok None
                    | Ok(ReadOutcome.Found found) -> return Ok(Some({ StoredChanges.nothing with Consumed = [ path, found.Revision ] }, TakenIn))
                }
            else
                receive page state sourceSystem observationId path

        match state.Basis, decided with
        | CachedAt _, _ -> return state, Error "GitHub cannot be reached."
        | _, Error failure -> return state, Error(describeFailure failure)
        | _, Ok None -> return state, Ok None
        | ReadAt token, Ok(Some(changed, taken)) ->
            match StoredChanges.changes state.Stored changed, operationContext state.Session (page.NewKey "inbox") (page.Now ()) with
            | Error diagnostics, _
            | _, Error diagnostics -> return state, Error(describeAll diagnostics)
            | Ok changes, Ok context ->
                let summary = $"receive observation {sourceSystem}/{observationId}"

                match Storage.operation state.Folder context summary changes with
                | Error diagnostics -> return state, Error(describeAll diagnostics)
                | Ok operation ->
                    match! state.Provider.Commit(require token operation) with
                    | Ok receipt ->
                        let! condition = conditionAfter state receipt

                        let next =
                            { state with
                                Basis = ReadAt condition
                                Stored = StoredOverlay.committed changed receipt state.Stored }

                        let! next = afterCommit page next operation receipt
                        page.Opened <- Some next
                        return next, Ok(Some taken)
                    | Error(StorageFailure.StaleChangeToken _)
                    | Error(StorageFailure.StaleNamespaceToken _)
                    | Error(StorageFailure.Conflicted _) when not again ->
                        match! refresh page state [] with
                        | Error reason -> return state, Error reason
                        | Ok fresh -> return! takeIn page fresh sourceSystem observationId path alreadyReceived true
                    | Error failure -> return state, Error(describeFailure failure)
    }

/// One pass over the organization's inboxes (requirement 18): at most
/// `InboxBatch` files, in path order; the rest wait for the next pass,
/// which follows at once. Only while the records are read from GitHub
/// and nothing of this page's own waits to be sent, so a pass never
/// moves the repository under a queued change.
let rec inboxJob (page: Page) () =
    async {
        match page.Opened with
        | Some({ Basis = ReadAt _ } as state) when not (state.Queue.Entries |> List.exists unsent) ->
            page.Bridge.Emit [ Update.InboxReconciled { page.InboxStatus with Running = true } ]

            // One pin for the pass (WI-0081): what is in the inboxes, which
            // receipts are stored and the receipt check are read at one
            // state observed now. Deciding a file reads what is current,
            // since this pass's own commits move the folder.
            let! observed = state.Provider.NamespaceState state.Folder

            let discovery =
                match observed with
                | Ok observed -> pinnedAt { Provider = state.Provider; PinAt = state.PinAt } observed.RepositoryToken
                | Error _ -> state.Provider

            let finish (state: Opened) (status: InboxStatus) (changed: bool) =
                page.InboxStatus <- { status with Running = false; LastRun = Some(page.Now ()) }
                (if changed then refreshed page state else []) @ [ Update.InboxReconciled page.InboxStatus ]

            let failed (reason: string) =
                finish state { page.InboxStatus with Failed = page.InboxStatus.Failed + 1 } false
                @ [ Update.StoreReadFailed $"The inboxes could not be read: {reason}" ]

            match RelativePath.parse Chrona.Integration.Inbox.Folder with
            | Error _ -> return finish state page.InboxStatus false
            | Ok root ->
                match! entriesOf discovery state root with
                | Error failure -> return failed (describeFailure failure)
                | Ok(_, sources) ->
                    // Every file in every source's inbox, with whether a
                    // receipt is stored for it already.
                    let mutable found = []
                    let mutable failure = None

                    for source in sources |> List.sortBy RelativePath.render do
                        if failure.IsNone then
                            let sourceSystem = lastSegment source

                            match! entriesOf discovery state source with
                            | Error error -> failure <- Some error
                            | Ok(files, _) ->
                                let! receipts =
                                    async {
                                        match ReceiptRecord.sourceFolder sourceSystem with
                                        | Error _ -> return Ok Set.empty
                                        | Ok folder ->
                                            match! entriesOf discovery state folder with
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
                                        match! discovery.Read state.Folder receiptPath with
                                        | Error error -> failure <- Some error
                                        | Ok ReadOutcome.Absent -> work <- work @ [ sourceSystem, observationId, file, false ]
                                        // Its receipt was erased for retention: it was
                                        // received; only the file is left to remove.
                                        | Ok(ReadOutcome.Erased _) -> work <- work @ [ sourceSystem, observationId, file, true ]
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
                                    let! next, outcome = takeIn page state sourceSystem observationId file alreadyReceived false
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

                            // Receipts whose candidate no folder holds
                            // (WI-0074); a read that fails keeps what the
                            // last pass found.
                            let! orphaned = receiptsWithoutCandidate page discovery state

                            let status =
                                { page.InboxStatus with
                                    Received = page.InboxStatus.Received + received
                                    Waiting = waiting
                                    Failed = failed
                                    Invalid = invalid
                                    Orphaned = orphaned |> Result.defaultValue page.InboxStatus.Orphaned }

                            let messages = finish state status changed

                            // More waits, and this pass got through: the next follows.
                            if waiting > 0 && changed && failed = 0 then
                                serial page (inboxJob page)

                            return messages
        | _ -> return []
    }
