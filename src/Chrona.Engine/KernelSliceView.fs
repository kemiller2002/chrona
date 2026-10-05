/// The kernel verification slice: projection only. Turns authoritative state
/// into Limen's flat view state. No DOM operations. Ported from
/// `verification/kernel-slice/src/projection.ts`.
module Chrona.Engine.KernelSliceView

open Chrona.Engine.View
open Chrona.Engine.KernelSlice

let statusText =
    function
    | Empty -> "Type a label, then save it."
    | Editing _ -> "Ready to save."
    | Invalid(_, reason) -> reason
    | Saving _ -> "Saving…"
    | Saved label -> $"Saved \"{labelText label}\"."
    | Loading _ -> "Loading…"
    | Loaded label -> $"Loaded \"{labelText label}\"."
    | Absent -> "Nothing stored yet."
    | StorageFailure reason -> $"Storage failed ({failureReasonText reason})."

let private draftOf =
    function
    | Editing draft
    | Invalid(draft, _) -> draft
    | Saved label
    | Loaded label -> labelText label
    | _ -> ""

let private busy =
    function
    | Saving _
    | Loading _ -> true
    | _ -> false

/// The state cue the page styles: the problem kind, or "none".
let private problemKind =
    function
    | Invalid _ -> "invalid"
    | StorageFailure _ -> "storage"
    | _ -> "none"

let project (model: Model) : View =
    let phase = model.Phase

    let problemText =
        match phase with
        | Invalid(_, reason) -> reason
        | StorageFailure reason -> $"Storage failed ({failureReasonText reason})."
        | _ -> ""

    [ "statusText", Value(Text(statusText phase))
      "phaseKind", Value(Text(phaseKind phase))
      "draft", Value(Text(draftOf phase))
      "saveDisabled",
      Value(
          Flag(
              busy phase
              || (match phase with
                  | Invalid _
                  | Empty -> true
                  | _ -> false)
          )
      )
      "loadDisabled", Value(Flag(busy phase))
      "hasProblem", Value(Flag(problemText <> ""))
      "problemText", Value(Text problemText)
      "problemKind", Value(Text(problemKind phase))
      "logCount", Value(Text(if model.Log.Length = 1 then "1 round trip" else $"{model.Log.Length} round trips"))
      "log", Items(model.Log |> List.map (fun entry -> [ "id", Text entry.Id; "text", Text entry.Text ])) ]
