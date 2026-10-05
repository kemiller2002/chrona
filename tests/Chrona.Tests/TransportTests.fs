/// The engine side of the Limen protocol, ported assertion for assertion
/// from the transport half of `verification/kernel-slice/test/domain.test.mjs`
/// and driven exactly as the browser kernel drives it: JSON in, JSON out.
///
/// One deliberate change of the port: where the TypeScript `step` threw (and
/// the kernel reported a bridge error), the F# engine runs under the Aegis
/// boundary every Echelon application uses, so a kernel message the engine
/// cannot honour becomes a classified, safely presented Aegis fault, and the
/// state is left as it was. It is still refused, never applied. An event name
/// the page should not send remains a defect that fails loudly.
module Chrona.Tests.TransportTests

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

let private location = """{"origin":"http://127.0.0.1:4321","path":"/web/index.html","query":"","hash":""}"""

let private offerWith (fingerprint: string) =
    $"""{{"protocol":{{"major":1,"minor":4}},"contract":{{"unit":"limen.core","version":1,"fingerprint":"{fingerprint}"}},"capabilities":[]}}"""

let private initializeWith (protocolVersion: int) (capabilities: string) (handshake: string option) =
    let handshake = handshake |> Option.map (fun h -> $",\"handshake\":{h}") |> Option.defaultValue ""
    $"""{{"kind":"Initialize","protocolVersion":{protocolVersion},"capabilities":{capabilities},"location":{location}{handshake}}}"""

let private initialize =
    initializeWith 1 """["Http","Storage","Clipboard","Navigation"]""" (Some(offerWith Limen.core.Fingerprint))

let private event (name: string) (value: string) =
    $"""{{"kind":"Event","event":{{"kind":"Event","name":"{name}","value":"{value}"}}}}"""

let private effectResult (result: string) = $"""{{"kind":"EffectResult","result":{result}}}"""

let private run aegis messages =
    messages |> List.fold (fun (state, _) message -> Wire.handle aegis state message) (Wire.initial, "")

/// The state with "note" saving: the Save effect's correlation id is slice-2.
let private saving aegis = run aegis [ event "draftChanged" "note"; event "save" "" ] |> fst

let private reply (text: string) = JsonNode.Parse text
let private viewText (name: string) (text: string) = (reply text).["view"].[name].GetValue<string>()
let private flag (name: string) (text: string) = (reply text).["view"].[name].GetValue<bool>()

[<Fact>]
let ``step rejects a mismatched protocol version`` () =
    let sink, aegis = collector ()
    let state, text = Wire.handle aegis Wire.initial (initializeWith 99 """["Storage"]""" (Some(offerWith Limen.core.Fingerprint)))
    Assert.True(flag "hasOperationalFault" text)
    Assert.Equal<string list>([ Boundary.MessageInvalid ], recordedCodes sink)
    Assert.Equal(Wire.initial.Model, state.Model)
    Assert.Null((reply text).["handshake"])

[<Fact>]
let ``step refuses a kernel that does not offer the Storage effect`` () =
    let sink, aegis = collector ()
    let state, text = Wire.handle aegis Wire.initial (initializeWith 1 """["Http"]""" (Some(offerWith Limen.core.Fingerprint)))
    Assert.True(flag "hasOperationalFault" text)
    Assert.Equal<string list>([ Boundary.CapabilityUnavailable ], recordedCodes sink)
    Assert.Equal(Wire.initial.Model, state.Model)

[<Fact>]
let ``Initialize projects the initial view without requesting effects`` () =
    let _, aegis = collector ()
    let _, text = Wire.handle aegis Wire.initial initialize
    Assert.Equal(0, (reply text).["effects"].AsArray().Count)
    Assert.Equal(0, (reply text).["cancellations"].AsArray().Count)
    Assert.Equal("Empty", viewText "phaseKind" text)

[<Fact>]
let ``Initialize answers the host's handshake: Accepted at protocol 1.4, core contract, no capability packs`` () =
    let _, aegis = collector ()
    let _, text = Wire.handle aegis Wire.initial initialize
    let handshake = (reply text).["handshake"]
    Assert.Equal("Accepted", handshake.["kind"].GetValue<string>())
    Assert.Equal(1, handshake.["protocol"].["major"].GetValue<int>())
    Assert.Equal(4, handshake.["protocol"].["minor"].GetValue<int>())
    Assert.Equal("limen.core", handshake.["contract"].["unit"].GetValue<string>())
    Assert.Equal(1, handshake.["contract"].["version"].GetValue<int>())
    Assert.Equal(Limen.core.Fingerprint, handshake.["contract"].["fingerprint"].GetValue<string>())
    Assert.Equal(0, handshake.["capabilities"].AsArray().Count)

[<Fact>]
let ``Initialize without a handshake offer (a pre-1.1 kernel) is answered HandshakeMissing`` () =
    let _, aegis = collector ()
    let _, text = Wire.handle aegis Wire.initial (initializeWith 1 """["Storage"]""" None)
    let handshake = (reply text).["handshake"]
    Assert.Equal("Rejected", handshake.["kind"].GetValue<string>())
    Assert.Equal("HandshakeMissing", handshake.["reason"].["kind"].GetValue<string>())

[<Fact>]
let ``Initialize from a host on a different core contract is answered ContractMismatch`` () =
    let _, aegis = collector ()
    let _, text = Wire.handle aegis Wire.initial (initializeWith 1 """["Storage"]""" (Some(offerWith "sha256:00")))
    let handshake = (reply text).["handshake"]
    Assert.Equal("Rejected", handshake.["kind"].GetValue<string>())
    Assert.Equal("ContractMismatch", handshake.["reason"].["kind"].GetValue<string>())

[<Fact>]
let ``only Initialize carries a handshake`` () =
    let _, aegis = collector ()
    let _, text = Wire.handle aegis Wire.initial (event "draftChanged" "a")
    Assert.False((reply text :?> JsonObject).ContainsKey "handshake")

[<Fact>]
let ``LocationChanged re-projects the model unchanged and requests nothing`` () =
    let _, aegis = collector ()
    let state = saving aegis
    let after, text = Wire.handle aegis state $"""{{"kind":"LocationChanged","location":{location}}}"""
    Assert.Equal(state.Model, after.Model) // the model must be the same value
    Assert.Equal(0, (reply text).["effects"].AsArray().Count)
    let expected = (reply (Wire.render state [] None)).["view"].ToJsonString()
    Assert.Equal(expected, (reply text).["view"].ToJsonString())

[<Fact>]
let ``a CapabilityFact is a contract violation: no capability was negotiated`` () =
    let sink, aegis = collector ()
    let state = saving aegis
    let after, text = Wire.handle aegis state """{"kind":"CapabilityFact","capability":"limen.focus","version":1,"fact":{}}"""
    Assert.True(flag "hasOperationalFault" text)
    Assert.Equal<string list>([ Boundary.CapabilityUnavailable ], recordedCodes sink)
    Assert.Equal(state.Model, after.Model)

[<Fact>]
let ``a StorageResult is routed to the domain as RecordStorage`` () =
    let _, aegis = collector ()
    let state = saving aegis

    let after, text =
        Wire.handle aegis state (effectResult """{"kind":"StorageResult","correlationId":"slice-2","outcome":{"kind":"Success","value":null}}""")

    Assert.Equal("Saved", Chrona.Engine.KernelSlice.phaseKind after.Model.Phase)
    Assert.Equal("Saved \"note\".", viewText "statusText" text)

[<Theory>]
[<InlineData("""{"kind":"HttpResult","correlationId":"c1","outcome":{"kind":"Cancelled"}}""")>]
[<InlineData("""{"kind":"ClipboardResult","correlationId":"c1","outcome":{"kind":"Success"}}""")>]
[<InlineData("""{"kind":"NavigationResult","correlationId":"c1","outcome":{"kind":"Dispatched"}}""")>]
[<InlineData("""{"kind":"CapabilityResult","correlationId":"c1","capability":"limen.focus","version":1,"outcome":{"kind":"Unsupported","reason":"not-negotiated"}}""")>]
let ``a non-Storage result has no request behind it and is refused, not applied`` (result: string) =
    let sink, aegis = collector ()
    let state = saving aegis
    let after, text = Wire.handle aegis state (effectResult result)
    Assert.True(flag "hasOperationalFault" text)
    Assert.Equal<string list>([ Boundary.CapabilityUnavailable ], recordedCodes sink)
    Assert.Equal(state.Model, after.Model)
    Assert.Equal("Saving", Chrona.Engine.KernelSlice.phaseKind after.Model.Phase)

[<Fact>]
let ``an unknown message kind fails loudly rather than being ignored`` () =
    let sink, aegis = collector ()
    let state, text = Wire.handle aegis Wire.initial """{"kind":"Bogus"}"""
    Assert.True(flag "hasOperationalFault" text)
    Assert.Equal<string list>([ Boundary.MessageInvalid ], recordedCodes sink)
    Assert.Equal(Wire.initial.Model, state.Model)

// Additional decisions of the port, beyond the TypeScript tests.

[<Fact>]
let ``the engine mints the correlation ids, and Save asks the kernel to set the label`` () =
    let _, aegis = collector ()
    let _, text = run aegis [ event "draftChanged" "note"; event "save" "" ]
    let effect = (reply text).["effects"].[0]
    Assert.Equal("Storage", effect.["kind"].GetValue<string>())
    Assert.Equal("slice-2", effect.["correlationId"].GetValue<string>())
    Assert.Equal("set", effect.["operation"].GetValue<string>())
    Assert.Equal("chrona.kernel-slice.label", effect.["key"].GetValue<string>())
    Assert.Equal("note", effect.["value"].GetValue<string>())

    let _, load = run aegis [ event "load" "" ]
    let get = (reply load).["effects"].[0]
    Assert.Equal("get", get.["operation"].GetValue<string>())
    Assert.Null(get.["value"])

[<Fact>]
let ``a storage failure the kernel reports is a typed outcome, never an Aegis fault`` () =
    let sink, aegis = collector ()
    let state = saving aegis

    let _, text =
        Wire.handle aegis state (effectResult """{"kind":"StorageResult","correlationId":"slice-2","outcome":{"kind":"Failure","reason":"quota-exceeded"}}""")

    Assert.True(flag "hasProblem" text)
    Assert.Equal("Storage failed (quota-exceeded).", viewText "problemText" text)
    Assert.Equal("storage", viewText "problemKind" text)
    Assert.False(flag "hasOperationalFault" text)
    Assert.Empty(sink.Events)
