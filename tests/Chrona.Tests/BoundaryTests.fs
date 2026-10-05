/// The Aegis boundary: what a fault looks like on the page, that it clears,
/// and that a defect is not turned into one. Faults go to a deterministic
/// Aegis collector sink.
module Chrona.Tests.BoundaryTests

open System
open System.Text.Json.Nodes
open Xunit
open Aegis
open Chrona.Application

let private collector () =
    let sink = Sinks.Collector()
    let aegis = { Boundary.configure [ sink.Sink() ] with Persistence = Blocking }
    sink, aegis

let private recordedCodes (sink: Sinks.Collector) =
    sink.Events |> List.map (fun event -> (JsonNode.Parse event).["code"] |> string)

let private viewOf (text: string) = (JsonNode.Parse text).["view"]
let private flag (name: string) (text: string) = (viewOf text).[name].GetValue<bool>()
let private textOf (name: string) (text: string) = (viewOf text).[name].GetValue<string>()

let private event (name: string) (value: string) =
    $"""{{"kind":"Event","event":{{"kind":"Event","name":"{name}","value":"{value}"}}}}"""

[<Fact>]
let ``a fault is presented safely, keeps the state and clears on the next message`` () =
    let sink, aegis = collector ()
    let state, _ = Wire.handle aegis Wire.initial (event "draftChanged" "alpha")

    let state, faulted =
        Wire.handle aegis state """{"kind":"EffectResult","result":{"kind":"StorageResult","correlationId":"slice-1","outcome":{"kind":"Sideways"}}}"""

    Assert.Equal<string list>([ Boundary.MessageInvalid ], recordedCodes sink)
    Assert.True(flag "hasOperationalFault" faulted)
    Assert.Contains("could not read", textOf "operationalFaultMessage" faulted)
    Assert.NotEqual<string>("", textOf "operationalFaultReference" faulted)
    // Safe presentation only: no exception names or paths reach the page.
    Assert.DoesNotContain("MalformedInput", faulted)
    Assert.DoesNotContain("$.result", faulted)
    // The page keeps the state it had.
    Assert.Equal("alpha", textOf "draft" faulted)

    let _, next = Wire.handle aegis state (event "draftChanged" "beta")
    Assert.False(flag "hasOperationalFault" next)

[<Fact>]
let ``a message that is not JSON is classified as an invalid message`` () =
    let sink, aegis = collector ()
    let _, reply = Wire.handle aegis Wire.initial "{not json"
    Assert.True(flag "hasOperationalFault" reply)
    Assert.Equal<string list>([ Boundary.MessageInvalid ], recordedCodes sink)

[<Fact>]
let ``an invalid label is a typed outcome, never an Aegis fault`` () =
    let sink, aegis = collector ()
    let _, reply = Wire.handle aegis Wire.initial (event "draftChanged" "")
    Assert.True(flag "hasProblem" reply)
    Assert.Equal("invalid", textOf "problemKind" reply)
    Assert.False(flag "hasOperationalFault" reply)
    Assert.Empty(sink.Events)

[<Fact>]
let ``an event the page does not know is a defect: it fails loudly and is not turned into a fault`` () =
    let sink, aegis = collector ()
    let ex = Assert.Throws<InvalidOperationException>(fun () -> Wire.handle aegis Wire.initial (event "nope" "") |> ignore)
    Assert.Matches("Unknown event name", ex.Message)
    Assert.Empty(sink.Events)
