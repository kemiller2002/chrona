/// The Chrona application's side of the Limen boundary (WI-0046), driven as
/// the browser kernel drives it: JSON in, JSON out, with a fixed clock, a
/// recording store and the Aegis boundary around every message.
module Chrona.Tests.AppBoundaryTests

open System
open System.Text.RegularExpressions
open System.Text.Json.Nodes
open Xunit
open Aegis
open Chrona.Engine.App.Model
open Chrona.Application
open Chrona.Tests.Support

let private collector () =
    let sink = Sinks.Collector()
    sink, { Boundary.configure [ sink.Sink() ] with Persistence = Blocking }

let private recordedCodes (sink: Sinks.Collector) =
    sink.Events |> List.map (fun event -> (JsonNode.Parse event).["code"] |> string)

let private now = DateTimeOffset(2026, 10, 8, 14, 10, 0, TimeSpan.FromHours -4.0)

/// The test's clock: tests move it forward explicitly.
let private clock = ref now

/// An environment whose store records what it was asked and answers as told.
let private envWith (answer: StoreRequest -> StoreOutcome) =
    clock.Value <- now
    let requests = ResizeArray<StoreRequest>()
    let ids = ref 0

    let env: App.Env =
        { Now = fun () -> clock.Value
          NewId = fun prefix -> $"{prefix}-{Threading.Interlocked.Increment ids}"
          Session = App.localSession
          StoreKind = InMemory
          Store =
            fun request ->
                requests.Add request
                answer request }

    env, requests

let private offer (capabilities: string) =
    $"""{{"protocol":{{"major":1,"minor":4}},"contract":{{"unit":"limen.core","version":1,"fingerprint":"{Limen.core.Fingerprint}"}},"capabilities":[{capabilities}]}}"""

let private schedule =
    $"""{{"id":"limen.schedule","version":1,"fingerprint":"{AppProtocol.schedule.Fingerprint}"}}"""

let private environment =
    $"""{{"id":"limen.environment","version":1,"fingerprint":"{AppProtocol.environment.Fingerprint}"}}"""

let private events = """["Http","Storage","Clipboard","Navigation"]"""

let private initializeWith (effects: string) (handshake: string) (hash: string) =
    $"""{{"kind":"Initialize","protocolVersion":1,"capabilities":{effects},"location":{{"origin":"http://127.0.0.1:4321","path":"/web/index.html","query":"","hash":"{hash}"}},"handshake":{handshake}}}"""

let private initialize = initializeWith events (offer $"{schedule},{environment}") "#/track"

let private event (name: string) (key: string option) (value: string) =
    let key = key |> Option.map (fun k -> $",\"key\":\"{k}\"") |> Option.defaultValue ""
    $"""{{"kind":"Event","event":{{"kind":"Event","name":"{name}"{key},"value":"{value}"}}}}"""

let private capabilityResult (id: string) (capability: string) (result: string) =
    $"""{{"kind":"EffectResult","result":{{"kind":"CapabilityResult","correlationId":"{id}","capability":"{capability}","version":1,"outcome":{{"kind":"Completed","result":{result}}}}}}}"""

let private run aegis env messages =
    messages |> List.fold (fun (state, _) message -> App.handle aegis env state message) (App.initial, "")

let private reply (text: string) = JsonNode.Parse text
let private viewText (name: string) (text: string) = (reply text).["view"].[name].GetValue<string>()
let private viewFlag (name: string) (text: string) = (reply text).["view"].[name].GetValue<bool>()
let private effects (text: string) = (reply text).["effects"].AsArray() |> Seq.toList

let private describedAs (zone: string) (state: App.State, text: string) =
    let id = (effects text).Head.["correlationId"].GetValue<string>()
    capabilityResult id "limen.environment" $"""{{"kind":"Described","environment":{{"locale":"en-US","languages":["en-US"],"timeZone":"{zone}","direction":"ltr","preferences":[]}}}}"""


[<Fact>]
let ``Initialize accepts the core contract and selects the schedule and environment packs`` () =
    let _, aegis = collector ()
    let env, _ = envWith App.inMemoryStore
    let _, text = App.handle aegis env App.initial initialize
    let handshake = (reply text).["handshake"]
    Assert.Equal("Accepted", handshake.["kind"].GetValue<string>())
    Assert.Equal(4, handshake.["protocol"].["minor"].GetValue<int>())

    Assert.Equal<string list>(
        [ "limen.schedule"; "limen.environment" ],
        handshake.["capabilities"].AsArray() |> Seq.map (fun c -> c.["id"].GetValue<string>()) |> Seq.toList
    )

    // The first thing the engine asks is the browser's time zone.
    match effects text with
    | [ e ] ->
        Assert.Equal("Capability", e.["kind"].GetValue<string>())
        Assert.Equal("limen.environment", e.["capability"].GetValue<string>())
        Assert.Equal("describe", e.["request"].["operation"].GetValue<string>())
    | other -> failwith $"{other}"

    Assert.True(viewFlag "screenTrack" text)

[<Fact>]
let ``a pack offered at another fingerprint is not selected`` () =
    let _, aegis = collector ()
    let env, _ = envWith App.inMemoryStore
    let stale = """{"id":"limen.schedule","version":1,"fingerprint":"sha256:00"}"""
    let _, text = App.handle aegis env App.initial (initializeWith events (offer stale) "")
    Assert.Equal(0, (reply text).["handshake"].["capabilities"].AsArray().Count)
    // Without the environment pack the engine falls back to UTC at once.
    Assert.Equal("UTC", viewText "zoneId" text)
    Assert.Empty(effects text)

[<Fact>]
let ``a kernel without Navigation is refused as an unavailable capability`` () =
    let sink, aegis = collector ()
    let env, _ = envWith App.inMemoryStore
    let state, text = App.handle aegis env App.initial (initializeWith """["Storage"]""" (offer "") "")
    Assert.True(viewFlag "hasOperationalFault" text)
    Assert.Equal<string list>([ Boundary.CapabilityUnavailable ], recordedCodes sink)
    Assert.True(state.Model.IsNone)

[<Fact>]
let ``a contract mismatch is answered with the reason and leaves the page unbound`` () =
    let _, aegis = collector ()
    let env, _ = envWith App.inMemoryStore
    let wrong = """{"protocol":{"major":1,"minor":4},"contract":{"unit":"limen.core","version":1,"fingerprint":"sha256:00"},"capabilities":[]}"""
    let state, text = App.handle aegis env App.initial (initializeWith events wrong "")
    Assert.Equal("Rejected", (reply text).["handshake"].["kind"].GetValue<string>())
    Assert.Equal("ContractMismatch", (reply text).["handshake"].["reason"].["kind"].GetValue<string>())
    Assert.True(state.Model.IsNone)

[<Fact>]
let ``the described time zone becomes the business zone`` () =
    let _, aegis = collector ()
    let env, _ = envWith App.inMemoryStore
    let state, text = run aegis env [ initialize ]
    let id = (effects text).Head.["correlationId"].GetValue<string>()
    let _, text = App.handle aegis env state (capabilityResult id "limen.environment" """{"kind":"Described","environment":{"locale":"en-US","languages":["en-US"],"timeZone":"America/New_York","direction":"ltr","preferences":[]}}""")
    Assert.Equal("America/New_York", viewText "zoneId" text)

[<Fact>]
let ``navigation is a same-origin push of the page path and fragment; the result moves the route`` () =
    let _, aegis = collector ()
    let env, _ = envWith App.inMemoryStore
    let state, _ = run aegis env [ initialize ]
    let state, text = App.handle aegis env state (event "navigate" (Some "more") "")

    let push = (effects text).Head
    Assert.Equal("Navigation", push.["kind"].GetValue<string>())
    Assert.Equal("push", push.["operation"].GetValue<string>())
    Assert.Equal("/web/index.html#/more", push.["url"].GetValue<string>())
    Assert.True(viewFlag "screenTrack" text)

    let id = push.["correlationId"].GetValue<string>()
    let moved = $"""{{"kind":"EffectResult","result":{{"kind":"NavigationResult","correlationId":"{id}","outcome":{{"kind":"Success","location":{{"origin":"http://127.0.0.1:4321","path":"/web/index.html","query":"","hash":"#/more"}}}}}}}}"""
    let _, text = App.handle aegis env state moved
    Assert.True(viewFlag "screenMore" text)

    // Back and Forward arrive as LocationChanged.
    let back = """{"kind":"LocationChanged","location":{"origin":"http://127.0.0.1:4321","path":"/web/index.html","query":"","hash":"#/today"}}"""
    let _, text = App.handle aegis env state back
    Assert.True(viewFlag "screenToday" text)

[<Fact>]
let ``a running timer asks the schedule pack for one-second wake-ups, and a fired wake-up re-arms`` () =
    let _, aegis = collector ()
    let env, _ = envWith App.inMemoryStore

    let started = App.handle aegis env App.initial initialize
    let described = App.handle aegis env (fst started) (describedAs "America/New_York" started) |> fst

    let state, _ =
        List.fold
            (fun (s, _) m -> App.handle aegis env s m)
            (described, "")
            [ event "newProjectName" None "HelixNote"
              event "addProject" None ""
              event "newActivityTypeName" None "Research"
              event "addActivityType" None "" ]

    let model = state.Model.Value
    let project = (Chrona.Domain.Reference.selectable Chrona.Domain.Reference.Project model.References).Head.Id
    let activityType = (Chrona.Domain.Reference.selectable Chrona.Domain.Reference.ActivityType model.References).Head.Id

    let state, text =
        [ event "timerProject" None project; event "timerActivityType" None activityType; event "startTimer" None "" ]
        |> List.fold (fun (s, _) m -> App.handle aegis env s m) (state, "")

    let wake = (effects text).Head
    Assert.Equal("limen.schedule", wake.["capability"].GetValue<string>())
    Assert.Equal("timeout", wake.["request"].["operation"].GetValue<string>())
    Assert.Equal(1000, wake.["request"].["delayMs"].GetValue<int>())

    let id = wake.["correlationId"].GetValue<string>()
    let _, text = App.handle aegis env state (capabilityResult id "limen.schedule" """{"kind":"Fired","elapsedMs":1000}""")
    Assert.Equal("limen.schedule", (effects text).Head.["capability"].GetValue<string>())

[<Fact>]
let ``every commit goes through the store port, and its answer is shown`` () =
    let _, aegis = collector ()
    let env, requests = envWith (fun _ -> Conflict "changed on another device")
    let _, text = run aegis env [ initialize; event "newProjectName" None "HelixNote"; event "addProject" None "" ]
    let request = Seq.exactlyOne requests
    Assert.Equal<string list>([ "HelixNote" ], request.References |> List.map _.Name)
    Assert.Equal("Not saved: changed elsewhere", viewText "storeHeadline" text)

    let env, _ = envWith App.inMemoryStore
    let _, text = run aegis env [ initialize; event "newProjectName" None "HelixNote"; event "addProject" None "" ]
    Assert.Equal("Kept in this tab only", viewText "storeHeadline" text)

[<Fact>]
let ``a result nobody requested, or a result kind the app never asks for, is a classified fault`` () =
    let sink, aegis = collector ()
    let env, _ = envWith App.inMemoryStore
    let state, _ = run aegis env [ initialize ]
    let before = state.Model.Value.Route

    let state, text = App.handle aegis env state (capabilityResult "app-999" "limen.schedule" """{"kind":"Fired","elapsedMs":1}""")
    Assert.True(viewFlag "hasOperationalFault" text)
    Assert.Equal(before, state.Model.Value.Route)

    let storage = """{"kind":"EffectResult","result":{"kind":"StorageResult","correlationId":"x","outcome":{"kind":"Success"}}}"""
    let _, text = App.handle aegis env state storage
    Assert.True(viewFlag "hasOperationalFault" text)
    Assert.Equal<string list>([ Boundary.CapabilityUnavailable; Boundary.CapabilityUnavailable ], recordedCodes sink)

    // The fault is shown until the next message, then cleared.
    let _, text = App.handle aegis env state (event "goToday" None "")
    Assert.False(viewFlag "hasOperationalFault" text)

[<Fact>]
let ``a malformed message is a classified fault that changes nothing`` () =
    let sink, aegis = collector ()
    let env, _ = envWith App.inMemoryStore
    let state, _ = run aegis env [ initialize ]
    let after, text = App.handle aegis env state """{"kind":"Event","event":{"kind":"Event"}}"""
    Assert.True(viewFlag "hasOperationalFault" text)
    Assert.Equal<string list>([ Boundary.MessageInvalid ], recordedCodes sink)
    Assert.Equal(state.Model.Value.Route, after.Model.Value.Route)

// ---- the page and the engine agree --------------------------------------------------

let private attributeValues (attribute: string) (html: string) =
    Regex.Matches(html, $"\\s{attribute}=\"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq

let private boundKeys (html: string) =
    let bindings =
        Regex.Matches(html, "\\sdata-bind-[a-z-]+=\"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq

    [ "data-text"; "data-if"; "data-each"; "data-key" ]
    |> List.map (fun attribute -> attributeValues attribute html)
    |> Set.unionMany
    |> Set.union bindings

/// Every list the page repeats, non-empty: a record today, reference data,
/// problems on every form and a stopped timer awaiting completion.
let private richView () =
    let _, aegis = collector ()
    let env, _ = envWith App.inMemoryStore
    let started = App.handle aegis env App.initial initialize
    let send (state: App.State) message = fst (App.handle aegis env state message)
    let state = send (fst started) (describedAs "America/New_York" started)

    let state =
        [ event "newProjectName" None "HelixNote"
          event "addProject" None ""
          event "newActivityTypeName" None "Research"
          event "addActivityType" None ""
          event "newTagName" None "Backend"
          event "addTag" None ""
          event "addTag" None "" ]
        |> List.fold send state

    let model = state.Model.Value
    let project = (Chrona.Domain.Reference.selectable Chrona.Domain.Reference.Project model.References).Head.Id
    let activityType = (Chrona.Domain.Reference.selectable Chrona.Domain.Reference.ActivityType model.References).Head.Id

    let state =
        [ event "manualActivityType" None activityType
          event "manualProject" None project
          event "manualStartDate" None "2026-10-08"
          event "manualStartTime" None "09:00"
          event "manualEndTime" None "10:00"
          event "manualDescription" None "Research"
          event "manualPurpose" None "Delivery"
          event "saveManual" None ""
          event "saveManual" None ""
          event "startTimer" None ""
          event "timerActivityType" None activityType
          event "timerProject" None project
          event "startTimer" None "" ]
        |> List.fold send state

    clock.Value <- now.AddMinutes 30.0
    let state = [ event "stopTimer" None ""; event "saveCompletion" None ""; event "pauseTimer" None "" ] |> List.fold send state
    Chrona.Engine.App.Project.project state.Model.Value

[<Fact>]
let ``the application page binds only what its engine projects and sends only what it handles`` () =
    let html = readRepoFile "web/index.html"
    let view = richView ()

    for name, value in view do
        match value with
        | Chrona.Engine.View.Items [] -> failwith $"{name} is empty, so its item fields are not checked"
        | _ -> ()

    let names =
        view
        |> List.collect (fun (name, value) ->
            match value with
            | Chrona.Engine.View.Value _ -> [ name ]
            | Chrona.Engine.View.Items items -> name :: (items |> List.collect (List.map fst)))
        |> Set.ofList
        |> Set.union (Boundary.faultView None |> List.map fst |> Set.ofList)

    Assert.Empty(Set.difference (boundKeys html) names)
    // Every event the page can send is one the engine handles, and every
    // event the engine handles is one the page can send.
    Assert.Equal<Set<string>>(Set.ofList Chrona.Engine.App.Update.eventNames, attributeValues "data-event" html)
