open Chrona.Engine
open Chrona.Application

let fail message =
    eprintfn "FAIL: %s" message
    1

let run () =
    let edited =
        Engine.initial
        |> fun state -> Engine.transition state (EditDraft(BusinessDate, "2026-09-23")) |> Result.get
        |> fun state -> Engine.transition state (EditDraft(Minutes, "90")) |> Result.get
        |> fun state -> Engine.transition state (EditDraft(Description, "Architecture review")) |> Result.get

    match Engine.transition edited RecordManualActivity with
    | Error error -> fail $"Could not record activity: {error}"
    | Ok saved ->
        match saved.Activities with
        | [ activity ] when activity.ExactMinutes = 90 && saved.Sequence = 1 ->
            let projection = Application.project saved
            match projection.TryFind "hasActivities" with
            | Some (Protocol.VBool true) ->
                printfn "PASS: Chrona first vertical slice"
                0
            | _ -> fail "Projection did not expose recorded activity."
        | _ -> fail "Recorded activity did not preserve exact minutes."

System.Environment.ExitCode <- run ()
