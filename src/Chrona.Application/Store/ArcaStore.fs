/// The Arca store (WI-0032): the store port over a backend, its parts in
/// Store/.
module Chrona.Application.ArcaStore

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
open Chrona.Application.StoreJobs
open Chrona.Application.StoreElsewhere
open Chrona.Application.InboxPass
open Chrona.Application.StoreDevice

/// The Arca store over a backend. `now` is the clock; `newKey` mints the
/// idempotency keys of the operations the store starts itself. It keeps
/// commits in memory until the engine opens storage, which it does only for
/// a deployment that configures a location.
let arca (bridge: Bridge) (backend: Backend) (now: unit -> DateTimeOffset) (newKey: string -> string) : StorePort =
    let page = create bridge backend now newKey
    page.Reopen <- reopenJob page
    page.ReadInboxes <- fun () -> serial page (inboxJob page)

    // Until the engine opens storage (a deployment that configures a
    // location), commits are kept in memory and acknowledged at once.
    let memory = inMemory bridge
    let mutable requested = false

    { Kind = InMemory
      Open =
        fun config session dates ->
            requested <- true
            page.LastOpen <- Some(config, session, dates)

            serial page (fun () ->
                async {
                    let! messages = openJob page config session dates ()

                    if messages |> List.exists (function Update.StoreUnavailable _ -> true | _ -> false) then
                        let! count, notice = waiting page config session
                        return messages @ [ Update.UnsentWaiting(count, notice) ]
                    else
                        return messages
                })
      Confirm = fun () -> serial page (confirmJob page)
      Commit =
        fun request ->
            if requested then
                serial page (commitJob page request)
            else
                memory.Commit request
      Read = fun dates -> if requested then serial page (readJob page dates)
      ReadOthers = fun wanted -> if requested then serial page (readOthersJob page wanted)
      ReadElsewhere = fun wanted -> if requested then serial page (readElsewhereJob page wanted)
      Rebuild = fun () -> if requested then serial page (rebuildJob page)
      ReadInboxes = fun () -> if requested then page.ReadInboxes ()
      SendNow =
        fun () ->
            if requested then
                page.RetryMs <- FirstRetryMs
                serial page (drain page)
      Discard = fun () -> if requested then serial page (discardJob page) else memory.Discard()
      TakeOver = fun () -> if requested then serial page (takeOverJob page)
      SendEarlier = fun () -> if requested then serial page (earlierJob page true)
      DiscardEarlier = fun () -> if requested then serial page (earlierJob page false)
      Claim = fun () -> if requested then serial page (claimJob page)
      Reconnect = fun () -> if requested then serial page (reconnectJob page)
      Lost = fun () -> if requested then serial page (lostJob page)
      SignedOut = fun choice unsent -> if requested then serial page (leaveJob page choice unsent)
      ClearDevice = fun () -> if requested then serial page (clearJob page) else memory.ClearDevice() }
