namespace Chrona.Application

open Chrona.Engine
open Chrona.Application.Protocol

module Dispatch =
    let private protocolVersion = 1
    let mutable private state = Engine.initial

    let private response () =
        { View = Application.project state
          Effects = []
          Cancellations = [] }

    let handle (messageJson: string) =
        match Protocol.parseMessage messageJson with
        | Initialize version ->
            if version <> protocolVersion then
                failwithf "Protocol version %d is unsupported; expected %d." version protocolVersion
        | Event event ->
            state <- Application.apply state event
        | LocationChanged ->
            ()

        response () |> Protocol.serializeMessage

    let resetForTests () =
        state <- Engine.initial
