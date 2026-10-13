/// What the engine asks of the store, each as one job in the store's turn:
/// open the records (and open them from GitHub again after the read cache
/// stood in), confirm an administrator, commit a change, read further months
/// or other people's, and rebuild the activity index.
module internal Chrona.Application.StoreJobs

open System
open System.Collections.Generic
open Arca
open Arca.GitHub
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.Model
open Chrona.Application.Bridge
open Chrona.Application.Store
open Chrona.Application.StoreState
open Chrona.Application.StoreUnsentQueue
open Chrona.Application.StoreIndex
open Chrona.Application.StorePage
open Chrona.Application.StoreReads
open Chrona.Application.StoreSync
open Chrona.Application.StoreHolding
open Chrona.Application.StoreOpening

/// This page's own unsent changes, carried into the records opened next:
/// where this tab keeps nothing in the browser, they live only here.
let carriedOver (page: Page) () =
    match page.Opened with
    | Some state when state.Owned.IsNone -> state.Queue.Entries |> List.filter unsent
    | _ -> []

let openJob (page: Page) (config: Deployment.DeploymentConfig) (session: Session) (dates: DateOnly list) () =
    async {
        let carried = carriedOver page ()

        let! fresh = openFresh page config session dates carried

        match fresh with
        | Ok messages -> return messages
        | Error(Unopened.Refused reason) -> return [ Update.StoreUnavailable reason ]
        | Error(Unopened.Unreachable reason) ->
            match! openCached page config session dates carried with
            | Some messages -> return messages
            | None -> return [ Update.StoreUnavailable reason ]
    }

// From the read cache, the records are opened from GitHub again once it
// can be reached; until then they stay as shown.
let reopenJob (page: Page) () =
    async {
        match page.LastOpen, page.Opened with
        | Some(config, session, dates), Some({ Basis = CachedAt _ } as shown) ->
            let dates = dates @ (shown.Months |> Set.toList |> List.map firstOf)

            match! openFresh page config session dates (carriedOver page ()) with
            | Ok messages -> return messages
            | Error(Unopened.Unreachable _) ->
                scheduleRetry page ()
                return [ sync page shown true ]
            | Error(Unopened.Refused reason) -> return [ Update.StoreUnavailable reason ]
        | _ -> return []
    }

/// A listed account confirms itself as the administrator of an
/// organization that has none from the configuration.
let confirmJob (page: Page) () =
    async {
        match page.Pending with
        | Some state when Deployment.isBootstrapAdministrator state.Organization state.Session.ActorId ->
            page.Pending <- None

            match! appoint page state with
            | Error reason -> return [ Update.StoreUnavailable reason ]
            | Ok state -> return! ready page state
        | Some state -> return [ Update.StoreNeedsConfirmation(confirmationReason state.Organization false, false) ]
        | None -> return [ Update.StoreUnavailable "There is nothing to confirm." ]
    }

let commitJob (page: Page) (request: StoreRequest) () =
    async {
        let answer outcome = [ Update.StoreAnswered(request.CommitId, outcome) ]

        match page.Opened with
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
                        match! refresh page state unread with
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
                | Error divergences -> return refreshed page state @ answer (Conflict divergences)
                | Ok changed when isEmpty changed -> return answer Committed
                | Ok changed ->
                    match Stored.changes real changed, operationContext state.Session request.CommitId (page.Now ()) with
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
                                | Some who -> OfflineQueue.enqueueFor who.Account (page.Now ()) queued state.Queue
                                | None -> OfflineQueue.enqueue (page.Now ()) queued state.Queue

                            match enqueued with
                            | Error _ -> return answer (Failed "This change could not be queued.")
                            | Ok(queued, _) ->
                                let! state = persist page { state with Stored = real } queued
                                page.Opened <- Some state
                                return! drain page ()
    }

/// Reads other people's months for a report across people (WI-0039):
/// one folder each, on demand, with what is already read.
let readOthersJob (page: Page) (wanted: (string * DateOnly) list) () =
    async {
        match page.Opened with
        | None -> return []
        | Some state ->
            let others = state.Others + (wanted |> List.map (fun (actorId, date) -> actorId, monthOf date) |> Set.ofList)

            match! refreshWith page state [] others with
            | Ok next -> return [ Update.StoreOpened(contents next) ]
            | Error reason -> return [ Update.StoreReadFailed reason ]
    }

/// Reads further month folders, when the person goes to a month not
/// read yet (38: one folder, on demand).
let readJob (page: Page) (dates: DateOnly list) () =
    async {
        match page.Opened with
        | None -> return []
        | Some state ->
            match! refresh page state dates with
            | Ok next -> return [ Update.StoreOpened(contents next) ]
            | Error reason ->
                // The month is asked for again when the person returns to it.
                return [ Update.StoreReadFailed reason ]
    }

/// Rebuilds the activity index from every stored record (40): the
/// recovery path, and how an index from before WI-0034 is first built.
/// Arca writes it only when it differs from what is stored, and only
/// while the records are still as read.
let rebuildJob (page: Page) () =
    async {
        match page.Opened with
        | None -> return [ Update.IndexRebuilt "The records are not open." ]
        | Some state ->
            match operationContext state.Session (page.NewKey "index") (page.Now ()) with
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

                    match! refresh page state [] with
                    | Error reason -> return [ Update.IndexRebuilt reason ]
                    | Ok next -> return refreshed page next @ [ Update.IndexRebuilt summary ]
    }
