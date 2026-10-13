/// What this device keeps when an account leaves it, and clearing Chrona's
/// data from the browser (WI-0057, WI-0058).
module internal Chrona.Application.StoreDevice

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
open Chrona.Application.StoreUnsentQueue
open Chrona.Application.StoreIndex
open Chrona.Application.StorePage

/// What leaving the device does with the account's read cache, under the
/// deployment's shared-device policy (LCP-070, LCP-086): it goes, unless
/// the person kept their unsent work here under `ask`, so that the
/// account opens offline and sees it in context.
let leaveJob (page: Page) (choice: Update.UnsentChoice) (unsent: int) () =
    async {
        match page.LastOpen with
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
                match! readCache page () with
                | Some cached -> do! cached.Store.Clear(CacheScope.Account session.ActorId) |> Async.Ignore
                | None -> ()
            | _ -> ()

            return []
    }

/// Clears Chrona's data from this browser: the queue and the read cache,
/// for every account (LimenDevice.clear). Refused while anything of
/// another account's waits unsent here; blocked while another tab holds
/// the databases open.
let clearJob (page: Page) () =
    async {
        let others =
            match page.Opened with
            | Some state -> state.Queue.Entries |> List.exists (fun entry -> earlier entry || othersOf state.Session entry)
            | None -> false

        let otherKept =
            match page.Opened with
            | Some state -> state.Note = Some "Another account left changes in this browser that have not been sent; they are kept for that account."
            | None -> false

        if others || otherKept then
            return
                [ Update.DeviceCleared(
                      Error "Another account's unsent changes are kept in this browser; clearing it would lose them. That account can send them first."
                  ) ]
        else
            // This tab lets go of what it holds first.
            for owned in List.ofSeq page.Held.Values do
                do! owned.Release()

            page.Held.Clear()

            match! Arca.Limen.LimenDevice.clear page.Host with
            | Ok() ->
                page.Cache <- None
                page.Opened <- page.Opened |> Option.map (fun state -> { state with Owned = None })
                return [ Update.DeviceCleared(Ok()) ]
            | Error(_, ReadCacheFailure.Blocked) ->
                return [ Update.DeviceCleared(Error "Another Chrona tab has this browser's data open. Close Chrona's other tabs, then try again.") ]
            | Error(name, _) -> return [ Update.DeviceCleared(Error $"This browser could not clear Chrona's data ({name}). Try again.") ]
    }
