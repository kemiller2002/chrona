/// The kernel verification slice's domain and projection, ported assertion
/// for assertion from `verification/kernel-slice/test/domain.test.mjs` (the
/// TypeScript tests the F# engine replaces). The transport half of that file
/// is in TransportTests.fs.
module Chrona.Tests.KernelSliceTests

open Xunit
open Chrona.Engine.View
open Chrona.Engine.KernelSlice
open Chrona.Engine.KernelSliceView

let private draft (model: Model) (value: string) = transition model (DraftChanged value)

let private modelOf =
    function
    | Accepted(model, _)
    | Rejected(model, _) -> model

let private errorOf =
    function
    | Rejected(_, error) -> Some error
    | Accepted _ -> None

let private isAccepted =
    function
    | Accepted _ -> true
    | Rejected _ -> false

let private savingModel () =
    transition (modelOf (draft initialModel "note")) (Save "c1") |> modelOf

let private textOf name (view: View) =
    match view |> List.find (fst >> (=) name) |> snd with
    | Value(Text text) -> text
    | other -> failwith $"{name} is not text: {other}"

[<Fact>]
let ``a blank label is rejected by the guard`` () =
    match decodeLabel "   " with
    | Error reason -> Assert.Matches("required", reason)
    | Ok _ -> Assert.Fail "a blank label decoded"

[<Fact>]
let ``an over-long label is rejected by the guard`` () =
    match decodeLabel (String.replicate 25 "x") with
    | Error reason -> Assert.Matches("at most 24", reason)
    | Ok _ -> Assert.Fail "an over-long label decoded"

[<Fact>]
let ``a valid label is trimmed`` () =
    match decodeLabel "  hello  " with
    | Ok label -> Assert.Equal("hello", labelText label)
    | Error reason -> Assert.Fail reason

[<Fact>]
let ``draft change moves Empty -> Editing, and invalid input -> Invalid`` () =
    let ok = draft initialModel "note"
    Assert.True(isAccepted ok)
    Assert.Equal("Editing", phaseKind (modelOf ok).Phase)

    let bad = draft initialModel ""
    Assert.Equal("Invalid", phaseKind (modelOf bad).Phase)

[<Fact>]
let ``Save is illegal from Empty and rejected from Invalid with the reason`` () =
    let fromEmpty = transition initialModel (Save "c1")
    Assert.False(isAccepted fromEmpty)
    Assert.Equal(Some IllegalFromCurrentPhase, errorOf fromEmpty)

    let invalid = modelOf (draft initialModel "")
    let fromInvalid = transition invalid (Save "c1")
    Assert.False(isAccepted fromInvalid)

    match errorOf fromInvalid with
    | Some(InvalidLabel _) -> ()
    | other -> Assert.Fail $"expected InvalidLabel, got {other}"

[<Fact>]
let ``Save requests exactly one Storage set effect and does not perform it`` () =
    let editing = modelOf (draft initialModel "note")
    let saving = transition editing (Save "c1")
    Assert.True(isAccepted saving)

    match saving with
    | Accepted(model, effects) ->
        Assert.Equal<Effect list>([ StorageSet("c1", "chrona.kernel-slice.label", "note") ], effects)
        Assert.Equal("Saving", phaseKind model.Phase)
    | Rejected _ -> Assert.Fail "save was rejected"

[<Fact>]
let ``a stale effect result is rejected rather than applied`` () =
    let saving = savingModel ()
    let stale = transition saving (RecordStorage("c-other", StorageSucceeded None))
    Assert.False(isAccepted stale)
    Assert.Equal(Some StaleEffectResult, errorOf stale)
    Assert.Equal("Saving", phaseKind (modelOf stale).Phase) // phase must be unchanged

[<Fact>]
let ``a storage failure lands in StorageFailed, not silently ignored`` () =
    let failed = transition (savingModel ()) (RecordStorage("c1", StorageFailed QuotaExceeded))
    Assert.Equal("StorageFailed", phaseKind (modelOf failed).Phase)
    Assert.Equal(StorageFailure QuotaExceeded, (modelOf failed).Phase)
    Assert.Equal("quota-exceeded", failureReasonText QuotaExceeded)

[<Fact>]
let ``load of an absent key yields Absent, not Loaded(null)`` () =
    let loading = transition initialModel (Load "c9") |> modelOf
    let fin = transition loading (RecordStorage("c9", StorageSucceeded None))
    Assert.Equal("Absent", phaseKind (modelOf fin).Phase)

[<Fact>]
let ``projection is total over every phase`` () =
    let label =
        match decodeLabel "a" with
        | Ok label -> label
        | Error reason -> failwith reason

    let phases =
        [ Empty
          Editing "a"
          Invalid("", "r")
          Saving(label, "c")
          Saved label
          Loading "c"
          Loaded label
          Absent
          StorageFailure Unavailable ]

    for phase in phases do
        let view = project { Phase = phase; Log = []; Sequence = 0 }
        Assert.NotEqual<string>("", textOf "statusText" view) // ${phase.kind} must project a status

        match view |> List.find (fst >> (=) "log") |> snd with
        | Items _ -> ()
        | other -> Assert.Fail $"log is not a list: {other}"

[<Fact>]
let ``ViewState log items are flat records of primitives, as ViewItem requires`` () =
    // View items are (string * Scalar) lists by construction; what remains
    // to check is that the log actually carries items with fields.
    let saved = savingModel ()

    match project saved |> List.find (fst >> (=) "log") |> snd with
    | Items items ->
        Assert.NotEmpty items
        Assert.All(items, fun item -> Assert.Equal<string list>([ "id"; "text" ], item |> List.map fst))
    | other -> Assert.Fail $"log is not a list: {other}"

[<Fact>]
let ``unknown event names are rejected rather than silently ignored`` () =
    Assert.Equal(None, eventToCommand "nope" "" "c1")
    Assert.Equal(Some(DraftChanged "a"), eventToCommand "draftChanged" "a" "c1")
    Assert.Equal(Some(Save "c1"), eventToCommand "save" "" "c1")
    Assert.Equal(Some(Load "c1"), eventToCommand "load" "" "c1")

// Additional decisions of the port, beyond the TypeScript tests.

[<Fact>]
let ``Load is illegal while a storage request is in flight`` () =
    Assert.Equal(Some IllegalFromCurrentPhase, errorOf (transition (savingModel ()) (Load "c2")))

[<Fact>]
let ``a successful save lands in Saved and logs the round trip`` () =
    let saved = transition (savingModel ()) (RecordStorage("c1", StorageSucceeded None)) |> modelOf
    Assert.Equal("Saved \"note\".", statusText saved.Phase)

    Assert.Equal<string list>(
        [ "save requested"; "Saving: Success (value=null)" ],
        saved.Log |> List.map _.Text
    )

    Assert.Equal<string list>([ "entry-1"; "entry-2" ], saved.Log |> List.map _.Id)

[<Fact>]
let ``a loaded value is logged as JSON and projected back as the draft`` () =
    let loading = transition initialModel (Load "c9") |> modelOf
    let loaded = transition loading (RecordStorage("c9", StorageSucceeded(Some "alpha"))) |> modelOf
    Assert.Equal("Loaded \"alpha\".", statusText loaded.Phase)
    Assert.Equal("Loading: Success (value=\"alpha\")", (List.last loaded.Log).Text)
    Assert.Equal("alpha", textOf "draft" (project loaded))
