/// The application's side of the Limen boundary: the handshake and the packs
/// selected, navigation, wake-ups, focus, the store port, classified faults,
/// and telemetry that carries ids and measures only (42, WI-0040).
[<Xunit.Collection("AppBoundary")>]
module Chrona.Tests.AppBoundaryKernelTests

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

[<Fact>]
let ``Initialize accepts the core contract and selects the schedule and environment packs`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let _, text = handle aegis env AppState.initial initialize
    let handshake = (reply text).["handshake"]
    Assert.Equal("Accepted", handshake.["kind"].GetValue<string>())
    Assert.Equal(4, handshake.["protocol"].["minor"].GetValue<int>())

    Assert.Equal<string list>(
        [ "limen.schedule"; "limen.environment" ],
        handshake.["capabilities"].AsArray() |> Seq.map (fun c -> c.["id"].GetValue<string>()) |> Seq.toList
    )

    // The engine asks for the browser's time zone and reads the deployment's
    // configuration, beside the page.
    match effects text with
    | [ e; read ] ->
        Assert.Equal("Capability", e.["kind"].GetValue<string>())
        Assert.Equal("limen.environment", e.["capability"].GetValue<string>())
        Assert.Equal("describe", e.["request"].["operation"].GetValue<string>())
        Assert.Equal("Http", read.["kind"].GetValue<string>())
        Assert.Equal("GET", read.["method"].GetValue<string>())
        Assert.Equal("http://127.0.0.1:4321/web/chrona.deployment.json", read.["url"].GetValue<string>())
    | other -> failwith $"{other}"

    Assert.True(viewFlag "screenTrack" text)

[<Fact>]
let ``a pack offered at another fingerprint is not selected`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let stale = """{"id":"limen.schedule","version":1,"fingerprint":"sha256:00"}"""
    let _, text = handle aegis env AppState.initial (initializeWith events (offer stale) "")
    Assert.Equal(0, (reply text).["handshake"].["capabilities"].AsArray().Count)
    // Without the environment pack the engine falls back to UTC at once; it
    // asks only for the deployment's configuration.
    Assert.Equal("UTC", viewText "zoneId" text)
    Assert.Equal<string list>([ "Http" ], effects text |> List.map (fun e -> e.["kind"].GetValue<string>()))

[<Fact>]
let ``a kernel without Navigation is refused as an unavailable capability`` () =
    let sink, aegis = collector ()
    let env, _ = envWith committed
    let state, text = handle aegis env AppState.initial (initializeWith """["Storage"]""" (offer "") "")
    Assert.True(viewFlag "hasOperationalFault" text)
    Assert.Equal<string list>([ Boundary.CapabilityUnavailable ], recordedCodes sink)
    Assert.True(state.Model.IsNone)

[<Fact>]
let ``a kernel without Http or Storage is refused: configuration and sign-in need them`` () =
    let sink, aegis = collector ()
    let env, _ = envWith committed
    let state, text = App.handle aegis env AppState.initial (initializeWith """["Navigation","Clipboard"]""" (offer "") "")
    Assert.True(viewFlag "hasOperationalFault" text)
    Assert.Equal<string list>([ Boundary.CapabilityUnavailable ], recordedCodes sink)
    Assert.True(state.Model.IsNone)

[<Fact>]
let ``a contract mismatch is answered with the reason and leaves the page unbound`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let wrong = """{"protocol":{"major":1,"minor":4},"contract":{"unit":"limen.core","version":1,"fingerprint":"sha256:00"},"capabilities":[]}"""
    let state, text = handle aegis env AppState.initial (initializeWith events wrong "")
    Assert.Equal("Rejected", (reply text).["handshake"].["kind"].GetValue<string>())
    Assert.Equal("ContractMismatch", (reply text).["handshake"].["reason"].["kind"].GetValue<string>())
    Assert.True(state.Model.IsNone)

[<Fact>]
let ``the described time zone becomes the business zone`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let state, text = run aegis env [ initialize ]
    let id = (effects text).Head.["correlationId"].GetValue<string>()
    let _, text = handle aegis env state (capabilityResult id "limen.environment" """{"kind":"Described","environment":{"locale":"en-US","languages":["en-US"],"timeZone":"America/New_York","direction":"ltr","preferences":[]}}""")
    Assert.Equal("America/New_York", viewText "zoneId" text)

[<Fact>]
let ``navigation is a same-origin push or replace of the page path and fragment; Back and Forward are adopted`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let state, _ = run aegis env [ initialize ]
    let state, text = handle aegis env state (event "goMore" None "")

    // The engine moves at once and asks the browser to follow.
    let push = (effects text).Head
    Assert.Equal("Navigation", push.["kind"].GetValue<string>())
    Assert.Equal("push", push.["operation"].GetValue<string>())
    Assert.Equal("/web/index.html#/more", push.["url"].GetValue<string>())
    Assert.True(viewFlag "screenMore" text)

    // The browser following is not news.
    let id = push.["correlationId"].GetValue<string>()
    let moved = $"""{{"kind":"EffectResult","result":{{"kind":"NavigationResult","correlationId":"{id}","outcome":{{"kind":"Success","location":{{"origin":"http://127.0.0.1:4321","path":"/web/index.html","query":"","hash":"#/more"}}}}}}}}"""
    let state, text = handle aegis env state moved
    Assert.True(viewFlag "screenMore" text)
    Assert.Empty(effects text)

    // Back and Forward arrive as LocationChanged, and are adopted: an old
    // address is corrected with a replace, never a push.
    let back = """{"kind":"LocationChanged","location":{"origin":"http://127.0.0.1:4321","path":"/web/index.html","query":"","hash":"#/today"}}"""
    let _, text = handle aegis env state back
    Assert.True(viewFlag "screenToday" text)
    let replace = (effects text).Head
    Assert.Equal("replace", replace.["operation"].GetValue<string>())
    Assert.Equal("/web/index.html#/", replace.["url"].GetValue<string>())

[<Fact>]
let ``a running timer asks the schedule pack for one-second wake-ups, and a fired wake-up re-arms`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed

    let started = handle aegis env AppState.initial initialize
    let described = handle aegis env (fst started) (describedAs "America/New_York" started) |> fst

    let state, _ =
        List.fold
            (fun (s, _) m -> handle aegis env s m)
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
        |> List.fold (fun (s, _) m -> handle aegis env s m) (state, "")

    let wake = (effects text).Head
    Assert.Equal("limen.schedule", wake.["capability"].GetValue<string>())
    Assert.Equal("timeout", wake.["request"].["operation"].GetValue<string>())
    Assert.Equal(1000, wake.["request"].["delayMs"].GetValue<int>())

    let id = wake.["correlationId"].GetValue<string>()
    let _, text = handle aegis env state (capabilityResult id "limen.schedule" """{"kind":"Fired","elapsedMs":1000}""")
    Assert.Equal("limen.schedule", (effects text).Head.["capability"].GetValue<string>())

[<Fact>]
let ``focus moves to the timer control that took the pressed one's place, through chrona.host`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let hostOffer = $"""{{"id":"chrona.host","version":1,"fingerprint":"{AppProtocol.host.Fingerprint}"}}"""
    let started = handle aegis env AppState.initial (initializeWith events (offer $"{schedule},{environment},{hostOffer}") "#/track")
    let described = handle aegis env (fst started) (describedAs "America/New_York" started) |> fst

    let state, _ =
        List.fold
            (fun (s, _) m -> handle aegis env s m)
            (described, "")
            [ event "newProjectName" None "HelixNote"
              event "addProject" None ""
              event "newActivityTypeName" None "Research"
              event "addActivityType" None "" ]

    let model = state.Model.Value
    let project = (Chrona.Domain.Reference.selectable Chrona.Domain.Reference.Project model.References).Head.Id
    let activityType = (Chrona.Domain.Reference.selectable Chrona.Domain.Reference.ActivityType model.References).Head.Id

    let _, text =
        [ event "timerProject" None project; event "timerActivityType" None activityType; event "startTimer" None "" ]
        |> List.fold (fun (s, _) m -> handle aegis env s m) (state, "")

    let focus =
        effects text
        |> List.find (fun e -> e.["kind"].GetValue<string>() = "Capability" && e.["capability"].GetValue<string>() = "chrona.host")

    Assert.Equal("focus", focus.["request"].["operation"].GetValue<string>())
    Assert.Equal("pause-timer", focus.["request"].["target"].GetValue<string>())

[<Fact>]
let ``every commit goes through the store port, and its answer is shown`` () =
    let _, aegis = collector ()
    let env, requests = envWith (fun _ -> Conflict [ Chrona.Domain.Reconcile.KeptChanging ])
    let _, text = run aegis env [ initialize; event "newProjectName" None "HelixNote"; event "addProject" None "" ]
    let request = Seq.exactlyOne requests
    Assert.Equal<string list>([ "HelixNote" ], request.References |> List.map _.Name)
    Assert.Equal("Not saved: changed elsewhere", viewText "storeHeadline" text)

    let env, _ = envWith committed
    let _, text = run aegis env [ initialize; event "newProjectName" None "HelixNote"; event "addProject" None "" ]
    Assert.Equal("Kept in this tab only", viewText "storeHeadline" text)

[<Fact>]
let ``a result nobody requested, or a result kind the app never asks for, is a classified fault`` () =
    let sink, aegis = collector ()
    let env, _ = envWith committed
    let state, _ = run aegis env [ initialize ]
    let before = state.Model.Value.Place

    let state, text = handle aegis env state (capabilityResult "app-999" "limen.schedule" """{"kind":"Fired","elapsedMs":1}""")
    Assert.True(viewFlag "hasOperationalFault" text)
    Assert.Equal(before, state.Model.Value.Place)

    let storage = """{"kind":"EffectResult","result":{"kind":"StorageResult","correlationId":"x","outcome":{"kind":"Success"}}}"""
    let _, text = handle aegis env state storage
    Assert.True(viewFlag "hasOperationalFault" text)
    Assert.Equal<string list>([ Boundary.CapabilityUnavailable; Boundary.CapabilityUnavailable ], recordedCodes sink)

    // The fault is shown until the next message, then cleared.
    let _, text = handle aegis env state (event "goToday" None "")
    Assert.False(viewFlag "hasOperationalFault" text)

[<Fact>]
let ``a malformed message is a classified fault that changes nothing`` () =
    let sink, aegis = collector ()
    let env, _ = envWith committed
    let state, _ = run aegis env [ initialize ]
    let after, text = handle aegis env state """{"kind":"Event","event":{"kind":"Event"}}"""
    Assert.True(viewFlag "hasOperationalFault" text)
    Assert.Equal<string list>([ Boundary.MessageInvalid ], recordedCodes sink)
    Assert.Equal(state.Model.Value.Place, after.Model.Value.Place)

// ---- Telemetry carries ids and measures only (42, WI-0040) --------------------------

/// What a page must never send to telemetry: a GitHub token, a private
/// client's description, business-purpose text, an artifact's contents and
/// a sensitive URL.
let private secrets =
    [ "ghp_0123456789abcdefghijklmnopqrstuvwxyzAB"
      "Merger talks with Northwind (confidential)"
      "Prepare the acquisition memo for the board"
      "BEGIN PRIVATE ARTIFACT CONTENT"
      "https://private.example/download?sig=abc123" ]

[<Fact>]
let ``a fault's telemetry carries its code, category and reference, never what the message held`` () =
    let sink, aegis = collector ()
    let env, _ = envWith committed
    let state, _ = run aegis env [ initialize ]
    let quoted = secrets |> List.map (fun s -> "\"" + s + "\"") |> String.concat ","

    // Malformed in several ways, each carrying the secrets where a careless
    // boundary would echo them: a value of the wrong type, an unknown kind,
    // text that is not JSON, and a result for nothing the engine asked for.
    let malformed =
        [ $"""{{"kind":"Event","event":{{"kind":"Event","name":{quoted.Split(',').[0]},"key":[{quoted}],"value":7}}}}"""
          $"""{{"kind":"{secrets[1]}","detail":[{quoted}]}}"""
          $"""{{"kind":"Event","event": {secrets[3]} {secrets[0]} }}"""
          $"""{{"kind":"EffectResult","result":{{"kind":"HttpResult","correlationId":"{secrets[4]}","status":200,"body":{quoted.Split(',').[2]}}}}}""" ]

    let mutable current = state

    for message in malformed do
        let next, text = handle aegis env current message
        current <- next

        for secret in secrets do
            Assert.DoesNotContain(secret, text)

    Assert.NotEmpty(sink.Events)
    for recorded in sink.Events do
        for secret in secrets do
            Assert.DoesNotContain(secret, recorded)

    // What it does carry: the stable code and the reference the page shows.
    for recorded in sink.Events do
        let node = JsonNode.Parse recorded
        Assert.StartsWith("CHRONA.BOUNDARY.", string node.["code"])
