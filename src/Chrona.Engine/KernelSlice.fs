/// The kernel verification slice: semantic model and legal transitions.
///
/// **Not a Chrona product feature.** It claims no product territory (the
/// charter leaves the first user and outcome to the owner); its job is to
/// prove the Limen bridge end to end in a real browser, now through the F#
/// engine on .NET WebAssembly. Ported from the TypeScript slice
/// (`verification/kernel-slice/src/domain.ts`) decision for decision.
///
/// Pure. Knows nothing about the DOM, fetch, localStorage or the kernel's
/// mechanism. Effects are returned as data; the kernel executes them.
module Chrona.Engine.KernelSlice

[<Literal>]
let StorageKey = "chrona.kernel-slice.label"

[<Literal>]
let MaxLabel = 24

/// A trimmed, non-empty label of at most MaxLabel characters. `decodeLabel`
/// is the only way to make one.
type Label = private Label of string

let labelText (Label text) = text

/// Limen's StorageOutcome failure reasons.
type StorageFailureReason =
    | Unavailable
    | QuotaExceeded

type StorageOutcome =
    /// `value` is None when the key was absent or the operation was a set.
    | StorageSucceeded of value: string option
    | StorageFailed of StorageFailureReason

/// Phase is the closed set of things that can be true.
type Phase =
    | Empty
    | Editing of draft: string
    | Invalid of draft: string * reason: string
    | Saving of label: Label * correlationId: string
    | Saved of label: Label
    | Loading of correlationId: string
    | Loaded of label: Label
    | Absent
    | StorageFailure of reason: StorageFailureReason

type LogEntry = { Id: string; Text: string }

type Model =
    { Phase: Phase
      Log: LogEntry list
      Sequence: int }

type Command =
    | DraftChanged of value: string
    | Save of correlationId: string
    | Load of correlationId: string
    | RecordStorage of correlationId: string * outcome: StorageOutcome

/// An effect the kernel is asked to perform.
type Effect =
    | StorageGet of correlationId: string * key: string
    | StorageSet of correlationId: string * key: string * value: string

type TransitionError =
    | IllegalFromCurrentPhase
    | StaleEffectResult
    | InvalidLabel of reason: string

type TransitionResult =
    | Accepted of model: Model * effects: Effect list
    | Rejected of model: Model * error: TransitionError

let initialModel =
    { Phase = Empty
      Log = []
      Sequence = 0 }

/// Guard: the only place a raw string becomes a Label.
let decodeLabel (value: string) : Result<Label, string> =
    let trimmed = value.Trim()

    if trimmed.Length = 0 then Error "A label is required."
    elif trimmed.Length > MaxLabel then Error $"A label may be at most {MaxLabel} characters."
    else Ok(Label trimmed)

let private note (model: Model) (text: string) =
    { model with
        Sequence = model.Sequence + 1
        Log = model.Log @ [ { Id = $"entry-{model.Sequence + 1}"; Text = text } ] }

/// The correlation id currently awaited, or None when nothing is in flight.
let private pending =
    function
    | Saving(_, correlationId)
    | Loading correlationId -> Some correlationId
    | _ -> None

let phaseKind =
    function
    | Empty -> "Empty"
    | Editing _ -> "Editing"
    | Invalid _ -> "Invalid"
    | Saving _ -> "Saving"
    | Saved _ -> "Saved"
    | Loading _ -> "Loading"
    | Loaded _ -> "Loaded"
    | Absent -> "Absent"
    | StorageFailure _ -> "StorageFailed"

let failureReasonText =
    function
    | Unavailable -> "unavailable"
    | QuotaExceeded -> "quota-exceeded"

let private outcomeText =
    function
    | StorageSucceeded None -> "Success (value=null)"
    // JSON.stringify of a string: quoted, with quotes and backslashes escaped.
    | StorageSucceeded(Some value) -> $"""Success (value="{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}")"""
    | StorageFailed reason -> $"Failure ({failureReasonText reason})"

let transition (model: Model) (command: Command) : TransitionResult =
    match command with
    | DraftChanged value ->
        let phase =
            match decodeLabel value with
            | Ok _ -> Editing value
            | Error reason -> Invalid(value, reason)

        Accepted({ model with Phase = phase }, [])

    | Save correlationId ->
        match model.Phase with
        | Invalid(_, reason) -> Rejected(model, InvalidLabel reason)
        | Editing draft ->
            match decodeLabel draft with
            | Error reason -> Rejected(model, InvalidLabel reason)
            | Ok label ->
                Accepted(
                    note { model with Phase = Saving(label, correlationId) } "save requested",
                    [ StorageSet(correlationId, StorageKey, labelText label) ]
                )
        | _ -> Rejected(model, IllegalFromCurrentPhase)

    | Load correlationId ->
        match pending model.Phase with
        | Some _ -> Rejected(model, IllegalFromCurrentPhase)
        | None ->
            Accepted(
                note { model with Phase = Loading correlationId } "load requested",
                [ StorageGet(correlationId, StorageKey) ]
            )

    | RecordStorage(correlationId, outcome) ->
        match pending model.Phase with
        | Some awaited when awaited = correlationId ->
            let logged = note model $"{phaseKind model.Phase}: {outcomeText outcome}"

            let phase =
                match outcome, model.Phase with
                | StorageFailed reason, _ -> StorageFailure reason
                | StorageSucceeded _, Saving(label, _) -> Saved label
                | StorageSucceeded None, _ -> Absent
                | StorageSucceeded(Some value), _ ->
                    // What the kernel stored is what the slice saved; a value
                    // that no longer decodes was not written by this engine.
                    match decodeLabel value with
                    | Ok label -> Loaded label
                    | Error reason -> Invalid(value, reason)

            Accepted({ logged with Phase = phase }, [])
        | _ -> Rejected(model, StaleEffectResult)

/// The `data-event` names the slice's page sends.
let eventNames = [ "draftChanged"; "save"; "load" ]

/// The command a page event becomes, or None for a name the slice does not
/// define (which the caller must treat as a defect, never ignore).
let eventToCommand (name: string) (value: string) (correlationId: string) =
    match name with
    | "draftChanged" -> Some(DraftChanged value)
    | "save" -> Some(Save correlationId)
    | "load" -> Some(Load correlationId)
    | _ -> None
