/// What the application boundary's tests share: an Aegis collector, the
/// environment with a store port, Limen's handshake and messages, and
/// reading a reply's view and effects.
module Chrona.Tests.AppBoundaryFixture

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

let collector () =
    let sink = Sinks.Collector()
    sink, { Boundary.configure [ sink.Sink() ] with Persistence = Blocking }

let recordedCodes (sink: Sinks.Collector) =
    sink.Events |> List.map (fun event -> (JsonNode.Parse event).["code"] |> string)

let now = DateTimeOffset(2026, 10, 8, 14, 10, 0, TimeSpan.FromHours -4.0)

/// The test's clock: tests move it forward explicitly.
let clock = ref now

/// Every commit is acknowledged, as a store that keeps nothing does.
let committed (_: StoreRequest) = Committed

/// An environment whose store records what it was asked and answers as told.
let envWith (answer: StoreRequest -> StoreOutcome) =
    clock.Value <- now
    let requests = ResizeArray<StoreRequest>()
    let ids = ref 0

    let bridge = Bridge.Bridge()

    let env: AppState.Env =
        { Now = fun () -> clock.Value
          NewId = fun prefix -> $"{prefix}-{Threading.Interlocked.Increment ids}"
          Session = AppState.localSession
          Bridge = bridge
          Identity = Identity.create bridge (fun () -> clock.Value) (fun count -> Array.create count 7uy)
          Store =
            { Kind = InMemory
              Open = fun _ _ _ -> ()
              Confirm = fun () -> ()
              Commit =
                fun request ->
                    requests.Add request
                    bridge.Start(async { return [ Chrona.Engine.App.Update.StoreAnswered(request.CommitId, answer request) ] })
              Read = fun _ -> ()
              Rebuild = fun () -> ()
              ReadOthers = fun _ -> ()
              ReadElsewhere = fun _ -> ()
              ReadInboxes = fun () -> ()
              SendNow = fun () -> ()
              Discard = fun () -> ()
              TakeOver = fun () -> ()
              SendEarlier = fun () -> ()
              DiscardEarlier = fun () -> ()
              Claim = fun () -> ()
              Reconnect = fun () -> ()
              Lost = fun () -> ()
              SignedOut = fun _ _ -> ()
              ClearDevice = fun () -> () }
          Build = Chrona.Engine.App.Model.Development }

    env, requests

let offer (capabilities: string) =
    $"""{{"protocol":{{"major":1,"minor":4}},"contract":{{"unit":"limen.core","version":1,"fingerprint":"{Limen.core.Fingerprint}"}},"capabilities":[{capabilities}]}}"""

let schedule =
    $"""{{"id":"limen.schedule","version":1,"fingerprint":"{AppProtocol.schedule.Fingerprint}"}}"""

let environment =
    $"""{{"id":"limen.environment","version":1,"fingerprint":"{AppProtocol.environment.Fingerprint}"}}"""

let events = """["Http","Storage","Clipboard","Navigation"]"""

let initializeWith (effects: string) (handshake: string) (hash: string) =
    $"""{{"kind":"Initialize","protocolVersion":1,"capabilities":{effects},"location":{{"origin":"http://127.0.0.1:4321","path":"/web/index.html","query":"","hash":"{hash}"}},"handshake":{handshake}}}"""

let initialize = initializeWith events (offer $"{schedule},{environment}") "#/track"

let event (name: string) (key: string option) (value: string) =
    let key = key |> Option.map (fun k -> $",\"key\":\"{k}\"") |> Option.defaultValue ""
    $"""{{"kind":"Event","event":{{"kind":"Event","name":"{name}"{key},"value":"{value}"}}}}"""

let capabilityResult (id: string) (capability: string) (result: string) =
    $"""{{"kind":"EffectResult","result":{{"kind":"CapabilityResult","correlationId":"{id}","capability":"{capability}","version":1,"outcome":{{"kind":"Completed","result":{result}}}}}}}"""

/// A deployment that configures neither storage nor sign-in: a local session.
let localConfiguration = """{"environment":"local","environmentName":"local"}"""

let httpResult (id: string) (status: int) (body: string) =
    let body = Text.Json.JsonSerializer.Serialize body
    $"""{{"kind":"EffectResult","result":{{"kind":"HttpResult","correlationId":"{id}","outcome":{{"kind":"Success","status":{status},"body":{body}}}}}}}"""

/// `App.handle`, and when the reply asks for the deployment's configuration,
/// the local configuration's answer too. The reply returned is the first one.
let handle aegis env state message =
    let state, text = App.handle aegis env state message

    let read =
        (JsonNode.Parse text).["effects"].AsArray()
        |> Seq.tryFind (fun e -> e.["kind"].GetValue<string>() = "Http" && e.["url"].GetValue<string>().EndsWith "chrona.deployment.json")

    match read with
    | Some request -> fst (App.handle aegis env state (httpResult (request.["correlationId"].GetValue<string>()) 200 localConfiguration)), text
    | None -> state, text

let run aegis env messages =
    messages |> List.fold (fun (state, _) message -> handle aegis env state message) (AppState.initial, "")

let reply (text: string) = JsonNode.Parse text

let viewText (name: string) (text: string) = (reply text).["view"].[name].GetValue<string>()

let viewFlag (name: string) (text: string) = (reply text).["view"].[name].GetValue<bool>()

let effects (text: string) = (reply text).["effects"].AsArray() |> Seq.toList

let describedAs (zone: string) (state: AppState.State, text: string) =
    let id = (effects text).Head.["correlationId"].GetValue<string>()
    capabilityResult id "limen.environment" $"""{{"kind":"Described","environment":{{"locale":"en-US","languages":["en-US"],"timeZone":"{zone}","direction":"ltr","preferences":[]}}}}"""
