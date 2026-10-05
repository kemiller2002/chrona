/// The Aegis boundary of the Chrona engine.
///
/// Every kernel message crosses here: it is parsed, routed into the engine,
/// and the reply is written. *Unexpected operational* failure on that path (a
/// message that is not the shape the engine relies on, a kernel without the
/// Storage effect, a result or fact for something the engine never asked for)
/// is classified once, here, into a stable CHRONA.BOUNDARY.* fault and
/// presented through Forma's fault component. *Expected* outcomes are not
/// faults: an invalid label, a storage failure the kernel reports, or a stale
/// storage result are typed engine outcomes. Programming defects keep Aegis's fail-loud semantics
/// (they are re-raised and surface as a Limen bridge error), and are never
/// disguised as recoverable faults.
///
/// The declared boundaries are in aegis-boundaries.json.
module Chrona.Application.Boundary

open System.Text.Json
open Aegis
open Chrona.Engine.View
open Chrona.Application.Json
open Chrona.Application.Limen

[<Literal>]
let MessageInvalid = "CHRONA.BOUNDARY.MESSAGE_INVALID"

[<Literal>]
let CapabilityUnavailable = "CHRONA.BOUNDARY.CAPABILITY_UNAVAILABLE"

[<Literal>]
let Unexpected = "CHRONA.BOUNDARY.UNEXPECTED"

/// Aegis configured once for the application, and validated before it is
/// trusted. The sink is standard error, which the browser runtime routes to
/// the console; nothing is persisted (see aegis-boundaries.json).
let configure (sinks: Sinks.Sink list) =
    match Bootstrap.validate None (Aegis.configure "Chrona" None sinks) with
    | Ok valid -> valid
    | Result.Error problems -> invalidOp $"Invalid Chrona Aegis configuration: {problems}"

let private unchanged = "Nothing on this page was changed by it."

/// One central translation from an exception at the boundary to a fault.
let classify (aegis: AegisConfig) (scope: Scope) (ex: exn) =
    let code, category, message =
        match ex with
        | :? JsonException
        | MalformedInput _ -> MessageInvalid, DataFailure, $"Chrona could not read a message from the page. {unchanged}"
        | CapabilityFailed _ ->
            CapabilityUnavailable,
            IntegrationFailure,
            $"This browser could not provide something Chrona needs. {unchanged}"
        | _ -> Unexpected, IntegrationFailure, $"Chrona encountered an unexpected problem. {unchanged}"

    Aegis.faultOf aegis scope (FaultCode code) category FaultSeverity.Error OperationOnly Transient Continue message ex

/// The fault as the page shows it: safe presentation only (title, message,
/// reference), never the exception or its details.
type FaultView =
    { Title: string
      Message: string
      Reference: string }

let present (fault: Fault) =
    let presentation = Presentation.present "Chrona could not complete that operation" fault

    { Title = presentation.Title
      Message = presentation.Message
      Reference = presentation.Reference }

/// The fault's named values, bound by the Forma fault-inline component.
let faultView (fault: FaultView option) : View =
    [ "hasOperationalFault", Value(Flag fault.IsSome)
      "operationalFaultTitle", Value(Text(fault |> Option.map _.Title |> Option.defaultValue ""))
      "operationalFaultMessage", Value(Text(fault |> Option.map _.Message |> Option.defaultValue ""))
      "operationalFaultReference", Value(Text(fault |> Option.map _.Reference |> Option.defaultValue "")) ]

/// Runs one step at the boundary: its result, or the presented fault when it
/// failed operationally. Programming defects and cancellation are re-raised
/// by Aegis, not returned.
let capture (aegis: AegisConfig) (operation: string) (step: unit -> 'result) : Result<'result, FaultView> =
    let scope = Aegis.scope aegis operation Map.empty

    Aegis.capture aegis scope (classify aegis) step |> Result.mapError present
