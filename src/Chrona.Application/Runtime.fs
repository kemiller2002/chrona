/// The composition root the WebAssembly shim calls into: the one place with
/// effects (the clock, fresh ids, the browser's cryptographic random source,
/// the mutable page state and the identity port's in-flight sign-in work).
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

let private bridge = Bridge.Bridge()

let private now () = System.DateTimeOffset.UtcNow

let private identity =
    Identity.create bridge now System.Security.Cryptography.RandomNumberGenerator.GetBytes

let private newId (prefix: string) = $"{prefix}-{System.Guid.NewGuid():N}"

let private appEnv: App.Env =
    { Now = now
      NewId = newId
      Session = App.localSession
      Bridge = bridge
      Identity = identity
      // Arca's GitHub adapter, through the bridge, with Fides' token
      // provider; used only when the deployment configures a location.
      Store = Store.arca bridge (Store.gitHub bridge identity.TokenProvider identity.ReportUnauthorized) now newId }

let mutable private appState = App.initial

/// One kernel message for the Chrona application page (web/index.html).
let dispatchApp (messageJson: string) =
    let next, reply = App.handle aegis.Value appEnv appState messageJson
    appState <- next
    reply
