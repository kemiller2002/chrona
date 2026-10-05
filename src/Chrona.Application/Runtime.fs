/// The composition root the WebAssembly shim calls into.
///
/// The shim cannot thread state between calls, so the page's state lives
/// here, behind one string-in/string-out function. Aegis is configured once,
/// at first use.
module Chrona.Application.Runtime

open Aegis

let private aegis = lazy (Boundary.configure [ Sinks.standardError ])

let mutable private state = Wire.initial

/// One kernel message for the slice page (web/).
let dispatch (messageJson: string) =
    let next, reply = Wire.handle aegis.Value state messageJson
    state <- next
    reply
