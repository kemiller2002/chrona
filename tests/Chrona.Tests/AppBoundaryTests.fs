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

/// Every commit is acknowledged, as a store that keeps nothing does.
let private committed (_: StoreRequest) = Committed

/// An environment whose store records what it was asked and answers as told.
let private envWith (answer: StoreRequest -> StoreOutcome) =
    clock.Value <- now
    let requests = ResizeArray<StoreRequest>()
    let ids = ref 0

    let bridge = Bridge.Bridge()

    let env: App.Env =
        { Now = fun () -> clock.Value
          NewId = fun prefix -> $"{prefix}-{Threading.Interlocked.Increment ids}"
          Session = App.localSession
          Bridge = bridge
          Identity = Identity.create bridge (fun () -> clock.Value) (fun count -> Array.create count 7uy)
          Store =
            { Kind = InMemory
              Open = fun _ _ _ -> ()
              Commit =
                fun request ->
                    requests.Add request
                    bridge.Start(async { return [ Chrona.Engine.App.Update.StoreAnswered(request.CommitId, answer request) ] }) } }

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

/// A deployment that configures neither storage nor sign-in: a local session.
let private localConfiguration = """{"environment":"local","environmentName":"local"}"""

let private httpResult (id: string) (status: int) (body: string) =
    let body = Text.Json.JsonSerializer.Serialize body
    $"""{{"kind":"EffectResult","result":{{"kind":"HttpResult","correlationId":"{id}","outcome":{{"kind":"Success","status":{status},"body":{body}}}}}}}"""

/// `App.handle`, and when the reply asks for the deployment's configuration,
/// the local configuration's answer too. The reply returned is the first one.
let private handle aegis env state message =
    let state, text = App.handle aegis env state message

    let read =
        (JsonNode.Parse text).["effects"].AsArray()
        |> Seq.tryFind (fun e -> e.["kind"].GetValue<string>() = "Http" && e.["url"].GetValue<string>().EndsWith "chrona.deployment.json")

    match read with
    | Some request -> fst (App.handle aegis env state (httpResult (request.["correlationId"].GetValue<string>()) 200 localConfiguration)), text
    | None -> state, text

let private run aegis env messages =
    messages |> List.fold (fun (state, _) message -> handle aegis env state message) (App.initial, "")

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
    let env, _ = envWith committed
    let _, text = handle aegis env App.initial initialize
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
    let _, text = handle aegis env App.initial (initializeWith events (offer stale) "")
    Assert.Equal(0, (reply text).["handshake"].["capabilities"].AsArray().Count)
    // Without the environment pack the engine falls back to UTC at once; it
    // asks only for the deployment's configuration.
    Assert.Equal("UTC", viewText "zoneId" text)
    Assert.Equal<string list>([ "Http" ], effects text |> List.map (fun e -> e.["kind"].GetValue<string>()))

[<Fact>]
let ``a kernel without Navigation is refused as an unavailable capability`` () =
    let sink, aegis = collector ()
    let env, _ = envWith committed
    let state, text = handle aegis env App.initial (initializeWith """["Storage"]""" (offer "") "")
    Assert.True(viewFlag "hasOperationalFault" text)
    Assert.Equal<string list>([ Boundary.CapabilityUnavailable ], recordedCodes sink)
    Assert.True(state.Model.IsNone)

[<Fact>]
let ``a kernel without Http or Storage is refused: configuration and sign-in need them`` () =
    let sink, aegis = collector ()
    let env, _ = envWith committed
    let state, text = App.handle aegis env App.initial (initializeWith """["Navigation","Clipboard"]""" (offer "") "")
    Assert.True(viewFlag "hasOperationalFault" text)
    Assert.Equal<string list>([ Boundary.CapabilityUnavailable ], recordedCodes sink)
    Assert.True(state.Model.IsNone)

[<Fact>]
let ``a contract mismatch is answered with the reason and leaves the page unbound`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let wrong = """{"protocol":{"major":1,"minor":4},"contract":{"unit":"limen.core","version":1,"fingerprint":"sha256:00"},"capabilities":[]}"""
    let state, text = handle aegis env App.initial (initializeWith events wrong "")
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
let ``navigation is a same-origin push of the page path and fragment; the result moves the route`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let state, _ = run aegis env [ initialize ]
    let state, text = handle aegis env state (event "navigate" (Some "more") "")

    let push = (effects text).Head
    Assert.Equal("Navigation", push.["kind"].GetValue<string>())
    Assert.Equal("push", push.["operation"].GetValue<string>())
    Assert.Equal("/web/index.html#/more", push.["url"].GetValue<string>())
    Assert.True(viewFlag "screenTrack" text)

    let id = push.["correlationId"].GetValue<string>()
    let moved = $"""{{"kind":"EffectResult","result":{{"kind":"NavigationResult","correlationId":"{id}","outcome":{{"kind":"Success","location":{{"origin":"http://127.0.0.1:4321","path":"/web/index.html","query":"","hash":"#/more"}}}}}}}}"""
    let _, text = handle aegis env state moved
    Assert.True(viewFlag "screenMore" text)

    // Back and Forward arrive as LocationChanged.
    let back = """{"kind":"LocationChanged","location":{"origin":"http://127.0.0.1:4321","path":"/web/index.html","query":"","hash":"#/today"}}"""
    let _, text = handle aegis env state back
    Assert.True(viewFlag "screenToday" text)

[<Fact>]
let ``a running timer asks the schedule pack for one-second wake-ups, and a fired wake-up re-arms`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed

    let started = handle aegis env App.initial initialize
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
let ``every commit goes through the store port, and its answer is shown`` () =
    let _, aegis = collector ()
    let env, requests = envWith (fun _ -> Conflict "changed on another device")
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
    let before = state.Model.Value.Route

    let state, text = handle aegis env state (capabilityResult "app-999" "limen.schedule" """{"kind":"Fired","elapsedMs":1}""")
    Assert.True(viewFlag "hasOperationalFault" text)
    Assert.Equal(before, state.Model.Value.Route)

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

let private locationChanged (hash: string) =
    $"""{{"kind":"LocationChanged","location":{{"origin":"http://127.0.0.1:4321","path":"/web/index.html","query":"","hash":"{hash}"}}}}"""

let private toggled (name: string) (key: string) (on: bool) =
    $"""{{"kind":"Event","event":{{"kind":"Event","name":"{name}","key":"{key}","value":"","checked":{(if on then "true" else "false")}}}}}"""

/// Views of every screen in states where every list the page repeats is
/// non-empty somewhere: records, a merge selection, reference data, problems
/// on every form, a held timer, an attested and then changed day, an
/// activity with evidence and history, and a month.
let private richViews () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let started = handle aegis env App.initial initialize
    let send (state: App.State) message = fst (handle aegis env state message)
    let view (state: App.State) = Chrona.Engine.App.Project.project state.Model.Value
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

    let manual (start: string) (finish: string) (description: string) =
        [ event "manualActivityType" None activityType
          event "manualProject" None project
          event "manualStartDate" None "2026-10-08"
          event "manualStartTime" None start
          event "manualEndTime" None finish
          event "manualDescription" None description
          event "manualPurpose" None "Delivery"
          event "saveManual" None "" ]

    let state = manual "09:00" "10:00" "First" @ manual "10:00" "10:30" "Second" @ manual "11:00" "11:30" "Third" |> List.fold send state
    let idOf (description: string) (state: App.State) =
        state.Model.Value.Ledger.Activities |> Map.toList |> List.map snd |> List.find (fun a -> a.Classification.Description = description) |> _.ActivityId

    let first, third = idOf "First" state, idOf "Third" state

    // Problems on every Track and More form, a merge refused as not
    // contiguous, an attested day, and a timer held for completion.
    let state =
        [ event "saveManual" None ""
          event "pauseTimer" None ""
          toggled "mergeSelect" first true
          toggled "mergeSelect" third true
          event "saveMerge" None ""
          locationChanged "#/review/2026-10-08"
          event "attestDay" None ""
          event "attestStatement" None "Complete and accurate."
          event "attestDay" None ""
          event "attestDay" None ""
          event "timerActivityType" None activityType
          event "timerProject" None project
          event "startTimer" None "" ]
        |> List.fold send state

    clock.Value <- now.AddMinutes 30.0
    let state = [ event "stopTimer" None ""; event "saveCompletion" None ""; event "pauseTimer" None "" ] |> List.fold send state

    // An activity with evidence, history and a refusal on each of its forms;
    // the correction also makes the attested day stale.
    let state =
        [ locationChanged $"#/activity/{first}"
          event "evidenceLabel" None "Commit"
          event "evidenceUrl" None "https://example.test/commit/1"
          event "attachEvidence" None ""
          event "evidenceLabel" None "Bad"
          event "evidenceUrl" None "not a link"
          event "attachEvidence" None ""
          event "amendDescription" None "First, corrected"
          event "saveAmend" None ""
          event "amendPurpose" None ""
          event "saveAmend" None ""
          event "restoreActivity" None ""
          event "splitFirst" None "10"
          event "splitSecond" None "10"
          event "saveSplit" None "" ]
        |> List.fold send state

    let activity = view state
    let review = view (send state (locationChanged "#/review/2026-10-08"))
    let month = view (send state (locationChanged "#/month/2026-10"))
    let today = view (send state (locationChanged "#/today/2026-10-08"))

    // A member who may keep their own time but not change settings or
    // export: the refusals are shown where the commands were made.
    let restricted =
        let model = state.Model.Value
        let roster = model.Roster
        let actor = model.Session.ActorId

        let limited =
            { roster with
                Members =
                    roster.Members.Add(actor, { roster.Members[actor] with Capabilities = set [ Chrona.Domain.Access.ViewOwnTime ] }) }

        let limitedState = { state with Model = Some { model with Roster = limited } }
        [ event "periodCadence" None "monthly"; event "copyExport" None "" ] |> List.fold send limitedState

    // Records read from storage that need attention.
    let troubled =
        let model = state.Model.Value
        { state with Model = Some { model with Store = { model.Store with Integrity = [ Chrona.Domain.Diagnostics.ExternalEdit "records/chrona.activity/a/2026/10/A-1.json" ] } } }

    // Signed in to a deployment of two organizations, as their administrator,
    // with a refused attempt to add a member.
    let administering =
        let model = state.Model.Value

        let config =
            """{"environment":"test","environmentName":"test","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":""},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23li","redirectUri":"https://chrona.test/"},"organizations":[{"id":"org_a","displayName":"A","slug":"a","timeZone":"UTC"},{"id":"org_b","displayName":"B","slug":"b","timeZone":"UTC"}]}"""
            |> Chrona.Domain.Deployment.parse
            |> function
                | Ok config -> config
                | Error error -> failwith $"{error}"

        let signedIn =
            { model with
                Deployment = Some config
                Identity = { model.Identity with Mode = SignedInMode } }

        send { state with Model = Some signedIn } (event "admitMember" None "")

    [ activity; review; month; today; view restricted; view troubled; view administering ]

[<Fact>]
let ``the application page binds only what its engine projects and sends only what it handles`` () =
    let html = readRepoFile "web/index.html"
    let views = richViews ()

    let lists =
        views
        |> List.collect (List.choose (fun (name, value) -> match value with Chrona.Engine.View.Items items -> Some(name, items) | _ -> None))

    for name in lists |> List.map fst |> List.distinct do
        let everyEmpty = lists |> List.filter (fst >> (=) name) |> List.forall (snd >> List.isEmpty)
        Assert.False(everyEmpty, $"{name} is empty in every view, so its item fields are not checked")

    let names =
        views
        |> List.concat
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

[<Fact>]
let ``an export is copied through Limen's clipboard effect, and its answer is shown`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let state, _ = run aegis env [ initialize; event "goReports" None "" ]
    let state, text = handle aegis env state (event "copyExport" None "")
    let copy = (effects text).Head
    Assert.Equal("Clipboard", copy.["kind"].GetValue<string>())
    Assert.Equal("writeText", copy.["operation"].GetValue<string>())
    Assert.StartsWith("# schema: chrona.time-report/1", copy.["text"].GetValue<string>())
    let id = copy.["correlationId"].GetValue<string>()
    let result = $"""{{"kind":"EffectResult","result":{{"kind":"ClipboardResult","correlationId":"{id}","outcome":{{"kind":"Success"}}}}}}"""
    let _, text = handle aegis env state result
    Assert.Equal("Copied to the clipboard.", viewText "copyStatus" text)

[<Fact>]
let ``without the clipboard effect the page says so at once; without the print pack nothing is requested`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let noClipboard = initializeWith """["Http","Storage","Navigation"]""" (offer $"{schedule},{environment}") "#/reports"
    let state, _ = run aegis env [ noClipboard ]
    let state, text = handle aegis env state (event "copyExport" None "")
    Assert.Empty(effects text)
    Assert.StartsWith("This browser did not allow copying.", viewText "copyStatus" text)
    let _, text = handle aegis env state (event "printReport" None "")
    Assert.Empty(effects text)

[<Fact>]
let ``the print pack is selected when offered and opens the print dialog`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let printOffer = $"""{{"id":"chrona.print","version":1,"fingerprint":"{AppProtocol.print.Fingerprint}"}}"""
    let state, text = run aegis env [ initializeWith events (offer $"{schedule},{environment},{printOffer}") "#/reports" ]
    Assert.Equal(3, (reply text).["handshake"].["capabilities"].AsArray().Count)
    let _, text = handle aegis env state (event "printReport" None "")
    let request = (effects text).Head
    Assert.Equal("chrona.print", request.["capability"].GetValue<string>())
    Assert.Equal("print", request.["request"].["action"].GetValue<string>())
