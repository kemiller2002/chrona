/// Engine effects to Limen requests (DF-CHRONA-2026-0003): each effect a
/// request under a fresh correlation id, or work started in a port; and the
/// bridge's browser calls as kernel requests.
module Chrona.Application.AppRequests

open System
open System.Text.Json.Nodes
open Aegis
open Chrona.Engine.App
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Application.Json
open Chrona.Application.Limen
open Chrona.Application.Boundary
open Chrona.Application.AppProtocol
open Chrona.Application.AppState

/// A browser service the bridge's clients asked for, as a Limen request, or
/// the answer it gets at once: a wait the kernel cannot time (no schedule
/// pack) is over, and a pack the kernel does not offer is missing.
let internal kernelRequest (state: State) (id: string) (call: Bridge.KernelCall) =
    let hostCall operation arguments =
        if negotiated host state then
            Ok(Host(id, operation, arguments))
        else
            raise (CapabilityFailed("chrona.host", "Sign-in needs the chrona.host pack, which the kernel did not offer"))

    match call with
    | Bridge.Http(method, url, headers, body, timeoutMs, responseHeaders) -> Ok(Http(id, method, url, headers, body, timeoutMs, responseHeaders))
    | Bridge.DeviceGet key -> Ok(StorageGet(id, key))
    | Bridge.DeviceSet(key, value) -> Ok(StorageSet(id, key, value))
    | Bridge.DeviceRemove key -> Ok(StorageRemove(id, key))
    | Bridge.TabGet key -> hostCall "tabGet" [ "key", key ]
    | Bridge.TabSet(key, value) -> hostCall "tabSet" [ "key", key; "value", value ]
    | Bridge.TabRemove key -> hostCall "tabRemove" [ "key", key ]
    | Bridge.Leave url -> hostCall "leave" [ "url", url ]
    | Bridge.ReplaceAddress url -> hostCall "replaceAddress" [ "url", url ]
    | Bridge.Announce message -> hostCall "broadcast" [ "message", message ]
    | Bridge.Coordinate request when negotiated coordination state -> Ok(Contracted(id, coordination, request))
    | Bridge.StoreOperation request when negotiated store state -> Ok(Contracted(id, store, request))
    // Without the pack: answered at once, as missing.
    | Bridge.Coordinate _
    | Bridge.StoreOperation _ -> Error Bridge.Missing
    | Bridge.Sleep milliseconds when negotiated schedule state -> Ok(Wake(id, milliseconds))
    | Bridge.Sleep _ -> Error Bridge.Done

/// Engine effects to Limen requests, minting the correlation ids. Sign-in and
/// storage effects start work in their ports; their requests follow on `settle`.
let internal requests (env: Env) (state: State) (effects: Update.Effect list) =
    effects
    |> List.fold
        (fun (state: State, requests, immediate) effect ->
            let sequence = state.Sequence + 1
            let id = $"app-{sequence}"
            let minted purpose = { state with Sequence = sequence; Pending = state.Pending.Add(id, purpose) }

            match effect with
            // Relative to the page: its path, and the place in the fragment.
            | Update.Navigate(Limen.Routing.NavigationEffect.Push location) ->
                minted Navigation, requests @ [ Push(id, state.Path + Limen.Routing.Location.href Places.mode location) ], immediate
            | Update.Navigate(Limen.Routing.NavigationEffect.Replace location) ->
                minted Navigation, requests @ [ Replace(id, state.Path + Limen.Routing.Location.href Places.mode location) ], immediate
            // The return target lives in this tab's session storage (chrona.host).
            | Update.KeepReturn(Some target) when negotiated host state ->
                minted ReturnKeep, requests @ [ Host(id, "tabSet", [ "key", ReturnKey; "value", target ]) ], immediate
            | Update.KeepReturn None when negotiated host state ->
                minted ReturnKeep, requests @ [ Host(id, "tabRemove", [ "key", ReturnKey ]) ], immediate
            | Update.KeepReturn _ -> state, requests, immediate
            | Update.ReadReturn when negotiated host state -> minted ReturnLoad, requests @ [ Host(id, "tabGet", [ "key", ReturnKey ]) ], immediate
            | Update.ReadReturn -> state, requests, immediate @ [ Update.ReturnRead None ]
            | Update.Wake(generation, ms) when negotiated schedule state -> minted (Tick generation), requests @ [ Wake(id, ms) ], immediate
            // Without the schedule pack the display does not refresh between
            // events; timing itself is unaffected (it is timestamps).
            | Update.Wake _ -> state, requests, immediate
            | Update.DescribeEnvironment when negotiated environment state -> minted Environment, requests @ [ DescribeEnvironment id ], immediate
            | Update.DescribeEnvironment -> state, requests, immediate @ [ Update.EnvironmentUnavailable ]
            | Update.CopyText(purpose, text) when List.contains "Clipboard" state.Effects -> minted (Copying purpose), requests @ [ Copy(id, text) ], immediate
            | Update.CopyText(purpose, _) -> state, requests, immediate @ [ Update.Copied(purpose, false) ]
            | Update.Print when negotiated print state -> minted Printing, requests @ [ PrintPage id ], immediate
            // Without the print pack the browser's own Print still prints
            // the Folio document.
            | Update.Print -> state, requests, immediate
            | Update.ReadConfiguration when List.contains "Http" state.Effects ->
                minted Configuration, requests @ [ Http(id, "GET", configurationUrl state, [], None, RequestTimeoutMs, []) ], immediate
            | Update.ReadConfiguration -> state, requests, immediate @ [ Update.ConfigurationRead None ]
            | Update.BeginIdentity(config, callback) ->
                env.Identity.Begin config callback
                state, requests, immediate
            | Update.SignIn retention ->
                env.Identity.SignIn retention
                state, requests, immediate
            | Update.SignOut ->
                env.Identity.SignOut()
                state, requests, immediate
            | Update.LeaveDevice(choice, unsent) ->
                env.Store.SignedOut choice unsent
                state, requests, immediate
            | Update.ClearDevice ->
                env.Store.ClearDevice()
                state, requests, immediate
            | Update.Store request ->
                env.Store.Commit request
                state, requests, immediate
            | Update.OpenStore(config, session, dates) ->
                env.Store.Open config session dates
                state, requests, immediate
            | Update.ConfirmAdministrator ->
                env.Store.Confirm()
                state, requests, immediate
            | Update.ReadMonths dates ->
                env.Store.Read dates
                state, requests, immediate
            | Update.ReadOthers wanted ->
                env.Store.ReadOthers wanted
                state, requests, immediate
            | Update.ReadElsewhere wanted ->
                env.Store.ReadElsewhere wanted
                state, requests, immediate
            | Update.RebuildIndex ->
                env.Store.Rebuild()
                state, requests, immediate
            | Update.ReadInboxes ->
                env.Store.ReadInboxes()
                state, requests, immediate
            | Update.LoadTimer key ->
                minted (TimerLoad key), requests @ [ StorageGet(id, key) ], immediate
            | Update.SaveTimer(key, Some value) -> minted TimerSave, requests @ [ StorageSet(id, key, value) ], immediate
            | Update.SaveTimer(key, None) -> minted TimerSave, requests @ [ StorageRemove(id, key) ], immediate
            | Update.CheckShell when List.contains "Http" state.Effects ->
                minted ShellCheck, requests @ [ Http(id, "GET", buildUrl state (env.Now()), [], None, RequestTimeoutMs, []) ], immediate
            | Update.CheckShell -> state, requests, immediate
            | Update.ReloadPage when negotiated host state -> minted Reloading, requests @ [ Host(id, "reload", []) ], immediate
            | Update.ReloadPage -> state, requests, immediate
            | Update.FocusControl target when negotiated host state ->
                minted Focusing, requests @ [ Host(id, "focus", [ "target", target ]) ], immediate
            | Update.FocusControl _ -> state, requests, immediate
            | Update.TakeOverQueue ->
                env.Store.TakeOver()
                state, requests, immediate
            | Update.SendEarlier ->
                env.Store.SendEarlier()
                state, requests, immediate
            | Update.DiscardEarlier ->
                env.Store.DiscardEarlier()
                state, requests, immediate
            | Update.ClaimQueue ->
                env.Store.Claim()
                state, requests, immediate
            | Update.SendUnsent ->
                env.Store.SendNow()
                state, requests, immediate
            | Update.DiscardUnsent ->
                env.Store.Discard()
                state, requests, immediate)
        (state, [], [])
