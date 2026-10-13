/// Limen's packs the engine uses: the page's lifecycle and a newer Chrona
/// (WI-0063), coordination for the unsent changes (WI-0067), and the store
/// pack for the queue in IndexedDB (WI-0059).
[<Xunit.Collection("AppBoundary")>]
module Chrona.Tests.AppBoundaryPackTests

open System
open System.Text.RegularExpressions
open System.Text.Json.Nodes
open Xunit
open Aegis
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Application
open Chrona.Tests.Support
open Chrona.Tests.AppBoundaryFixture

// ---- the page's lifecycle and a newer Chrona (WI-0063) -------------------------------------

let private lifecycleOffer =
    $"""{{"id":"limen.lifecycle","version":1,"fingerprint":"{AppProtocol.lifecycle.Fingerprint}"}}"""

let private lifecycleFact (fact: string) =
    $"""{{"kind":"CapabilityFact","capability":"limen.lifecycle","version":1,"fact":{fact}}}"""

[<Fact>]
let ``when the kernel offers the lifecycle pack, the engine subscribes, and a page coming back catches up`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let state, text = handle aegis env AppState.initial (initializeWith events (offer $"{schedule},{environment},{lifecycleOffer}") "#/track")

    let subscription =
        effects text |> List.find (fun e -> e.["kind"].GetValue<string>() = "Capability" && e.["capability"].GetValue<string>() = "limen.lifecycle")

    Assert.Equal("subscribe", subscription.["request"].["operation"].GetValue<string>())

    Assert.Equal<string list>(
        [ "visibility"; "pageLifecycle"; "freezing"; "connectivity" ],
        subscription.["request"].["topics"].AsArray() |> Seq.map (fun t -> t.GetValue<string>()) |> Seq.toList
    )

    // Facts are typed; the kinds the engine does not act on change nothing,
    // and one it does not know is a fault, never ignored.
    let hidden, _ = App.handle aegis env state (lifecycleFact """{"kind":"VisibilityChanged","subscription":"lifecycle-1","visibility":"hidden"}""")
    Assert.True(hidden.Fault.IsNone)
    let back, _ = App.handle aegis env hidden (lifecycleFact """{"kind":"PageShown","subscription":"lifecycle-1","persisted":true}""")
    Assert.True(back.Fault.IsNone)
    let offline, text = App.handle aegis env back (lifecycleFact """{"kind":"ConnectivityChanged","subscription":"lifecycle-1","online":false}""")
    Assert.True(offline.Model.Value.Store.Sync.Offline)
    Assert.True(offline.Fault.IsNone)
    let odd, _ = App.handle aegis env offline (lifecycleFact """{"kind":"Teleported","subscription":"lifecycle-1"}""")
    Assert.True(odd.Fault.IsSome)

// ---- one tab holds the unsent changes: Limen's coordination pack (WI-0067) ----------------

let private coordinationOffer =
    $"""{{"id":"limen.coordination","version":1,"fingerprint":"{AppProtocol.coordination.Fingerprint}"}}"""

let private coordinationFact (body: string) =
    $"""{{"kind":"CapabilityFact","capability":"limen.coordination","version":1,"fact":{body}}}"""

/// Asks the bridge for the queue's lock, as Arca's LimenQueue.takeOver
/// does ("use this tab instead"), and records the answer.
let private askLock (env: AppState.Env) =
    let answers = ResizeArray<Bridge.KernelAnswer>()

    let request =
        Limen.Contract.Coordination.Codec.serializeCoordinationRequest (
            Limen.Contract.Coordination.Types.CoordinationRequest.Acquire(
                "arca.queue/chrona/org_acme",
                Limen.Contract.Coordination.Types.LockMode.Exclusive,
                false,
                true
            )
        )

    env.Bridge.Start(
        async {
            let! answer = env.Bridge.Call(Bridge.Coordinate request)
            answers.Add answer
            return []
        }
    )

    answers

[<Fact>]
let ``Arca's lock requests go through the coordination pack as written, the result comes back as written, and losing the lock is told to the store`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let lost = ref 0
    let env = { env with Store = { env.Store with Lost = fun () -> lost.Value <- lost.Value + 1 } }
    let state, _ = handle aegis env AppState.initial (initializeWith events (offer $"{schedule},{environment},{coordinationOffer}") "#/track")

    let answers = askLock env
    let state, text = App.handle aegis env state (event "goMore" None "")

    let acquire =
        effects text |> List.find (fun e -> e.["kind"].GetValue<string>() = "Capability" && e.["capability"].GetValue<string>() = "limen.coordination")

    Assert.Equal(1, acquire.["version"].GetValue<int>())
    let request = acquire.["request"]
    Assert.Equal("acquire", request.["operation"].GetValue<string>())
    Assert.Equal("arca.queue/chrona/org_acme", request.["name"].GetValue<string>())
    Assert.Equal("exclusive", request.["mode"].GetValue<string>())
    Assert.False(request.["wait"].GetValue<bool>())
    Assert.True(request.["steal"].GetValue<bool>())
    Assert.Empty answers

    let state, _ =
        App.handle aegis env state (capabilityResult (acquire.["correlationId"].GetValue<string>()) "limen.coordination" """{"kind":"Acquired","lock":"lock-1"}""")

    Assert.Equal<Bridge.KernelAnswer list>([ Bridge.Raw """{"kind":"Acquired","lock":"lock-1"}""" ], List.ofSeq answers)
    Assert.True(state.Fault.IsNone)

    let state, _ = App.handle aegis env state (coordinationFact """{"kind":"LockLost","lock":"lock-1"}""")
    Assert.Equal(1, lost.Value)
    Assert.True(state.Fault.IsNone)
    let odd, _ = App.handle aegis env state (coordinationFact """{"kind":"Received","channel":"x","from":"y","message":"z"}""")
    Assert.True(odd.Fault.IsSome)

[<Fact>]
let ``without the coordination pack, a lock request is answered at once as missing, and Arca treats ownership as unsupported`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let state, _ = handle aegis env AppState.initial initialize
    let answers = askLock env
    let _, text = App.handle aegis env state (event "goMore" None "")
    Assert.DoesNotContain(effects text, fun e -> e.["kind"].GetValue<string>() = "Capability" && e.["capability"].GetValue<string>() = "limen.coordination")
    Assert.Equal<Bridge.KernelAnswer list>([ Bridge.Missing ], List.ofSeq answers)

// ---- the queue in IndexedDB: Limen's store pack, version 2 (WI-0059) ---------------------

let private storeOffer =
    $"""{{"id":"limen.store","version":2,"fingerprint":"{AppProtocol.store.Fingerprint}"}}"""

let private storeFact (body: string) =
    $"""{{"kind":"CapabilityFact","capability":"limen.store","version":2,"fact":{body}}}"""

let private storeResult (id: string) (result: string) =
    $"""{{"kind":"EffectResult","result":{{"kind":"CapabilityResult","correlationId":"{id}","capability":"limen.store","version":2,"outcome":{{"kind":"Completed","result":{result}}}}}}}"""

/// Asks the bridge to open Arca's queue database, as LimenQueue does.
let private askStore (env: AppState.Env) =
    let answers = ResizeArray<Bridge.KernelAnswer>()

    let request =
        """{"operation":"open","database":"arca-queue","version":1,"stores":[{"name":"records","indexes":[]}],"dropStores":[]}"""

    env.Bridge.Start(
        async {
            let! answer = env.Bridge.Call(Bridge.StoreOperation request)
            answers.Add answer
            return []
        }
    )

    answers

[<Fact>]
let ``the engine selects limen.store version 2, Arca's store requests go through it as written, and a closed database is opened again`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let reconnected = ref 0
    let env = { env with Store = { env.Store with Reconnect = fun () -> reconnected.Value <- reconnected.Value + 1 } }
    let state, text = handle aegis env AppState.initial (initializeWith events (offer $"{schedule},{environment},{storeOffer}") "#/track")
    Assert.True(state.Fault.IsNone, $"%A{state.Fault}")

    let answers = askStore env
    let state, text = App.handle aegis env state (event "goMore" None "")

    let opening =
        effects text |> List.find (fun e -> e.["kind"].GetValue<string>() = "Capability" && e.["capability"].GetValue<string>() = "limen.store")

    Assert.Equal(2, opening.["version"].GetValue<int>())
    Assert.Equal("open", opening.["request"].["operation"].GetValue<string>())
    Assert.Empty answers

    let opened = """{"kind":"Opened","version":1,"upgradedFrom":0,"created":true}"""
    let state, _ = App.handle aegis env state (storeResult (opening.["correlationId"].GetValue<string>()) opened)
    Assert.Equal<Bridge.KernelAnswer list>([ Bridge.Raw opened ], List.ofSeq answers)
    Assert.True(state.Fault.IsNone)

    // Another context upgraded the database: Arca's next request reports it.
    let state, _ = App.handle aegis env state (storeFact """{"kind":"VersionChanged","database":"arca-queue","newVersion":2}""")
    Assert.Equal(0, reconnected.Value)
    Assert.True(state.Fault.IsNone)
    let state, _ = App.handle aegis env state (storeFact """{"kind":"ConnectionLost","database":"arca-queue"}""")
    Assert.Equal(1, reconnected.Value)
    Assert.True(state.Fault.IsNone)
    let odd, _ = App.handle aegis env state (storeFact """{"kind":"Teleported"}""")
    Assert.True(odd.Fault.IsSome)

[<Fact>]
let ``without the store pack, a store request is answered at once as missing, and Arca keeps the queue in localStorage`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let state, _ = handle aegis env AppState.initial initialize
    let answers = askStore env
    let _, text = App.handle aegis env state (event "goMore" None "")
    Assert.DoesNotContain(effects text, fun e -> e.["kind"].GetValue<string>() = "Capability" && e.["capability"].GetValue<string>() = "limen.store")
    Assert.Equal<Bridge.KernelAnswer list>([ Bridge.Missing ], List.ofSeq answers)

[<Fact>]
let ``a page built for a deployment asks which build is served, and offers a reload when it is newer`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let env = { env with Build = "a1b2c3" }
    let state, text = handle aegis env AppState.initial initialize

    let check =
        effects text
        |> List.find (fun e -> e.["kind"].GetValue<string>() = "Http" && e.["url"].GetValue<string>().Contains "chrona-build.json")

    Assert.StartsWith("http://127.0.0.1:4321/build/wasm/wwwroot/chrona-build.json?at=", check.["url"].GetValue<string>())
    let same, text = App.handle aegis env state (httpResult (check.["correlationId"].GetValue<string>()) 200 """{"build":"a1b2c3"}""")
    Assert.False(viewFlag "shellUpdate" text)
    Assert.Equal(None, same.Model.Value.Shell.Newer)

    // The next check finds a newer build.
    let model = { same.Model.Value with Shell = { same.Model.Value.Shell with Newer = None } }
    let next, effects' = Chrona.Engine.App.UpdateDispatch.update { Now = DateTimeOffset.UtcNow; NewId = fun p -> p } (Chrona.Engine.App.Update.ShellChecked(Some "d4e5f6")) model
    Assert.Empty effects'
    Assert.Equal(Some "d4e5f6", next.Shell.Newer)
    Assert.Equal("A newer Chrona is ready. Reload to use it.", next.Announcement)
