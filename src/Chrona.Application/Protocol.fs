namespace Chrona.Application

open System.Text.Json.Nodes

module Protocol =
    type SemanticEvent =
        { Name: string
          Key: string option
          Value: string option }

    type BrowserToEngineMessage =
        | Initialize of protocolVersion: int
        | Event of SemanticEvent
        | LocationChanged

    type ViewValue =
        | VString of string
        | VNumber of int
        | VBool of bool
        | VItems of Map<string, ViewValue> list

    type EngineToBrowserMessage =
        { View: Map<string, ViewValue>
          Effects: JsonNode list
          Cancellations: string list }

    let private optionalString (obj: JsonObject) key =
        match obj.[key] with
        | null -> None
        | node -> Some(node.GetValue<string>())

    let parseMessage (json: string) =
        let root = JsonNode.Parse(json).AsObject()

        match root.["kind"].GetValue<string>() with
        | "Initialize" -> Initialize(root.["protocolVersion"].GetValue<int>())
        | "Event" ->
            let event = root.["event"].AsObject()
            Event
                { Name = event.["name"].GetValue<string>()
                  Key = optionalString event "key"
                  Value = optionalString event "value" }
        | "LocationChanged" -> LocationChanged
        | "EffectResult" ->
            failwith "The readiness slice requests no browser effect yet."
        | other -> failwithf "Unknown browser message '%s'." other

    let rec private viewNode = function
        | VString text -> JsonValue.Create(text) :> JsonNode
        | VNumber number -> JsonValue.Create(number) :> JsonNode
        | VBool flag -> JsonValue.Create(flag) :> JsonNode
        | VItems items ->
            let array = JsonArray()
            for item in items do
                let node = JsonObject()
                for KeyValue(key, value) in item do
                    node.[key] <- viewNode value
                array.Add(node)
            array :> JsonNode

    let serializeMessage message =
        let root = JsonObject()
        let view = JsonObject()

        for KeyValue(key, value) in message.View do
            view.[key] <- viewNode value

        root.["view"] <- view

        let effects = JsonArray()
        for effect in message.Effects do effects.Add(effect)
        root.["effects"] <- effects

        let cancellations = JsonArray()
        for correlationId in message.Cancellations do
            cancellations.Add(JsonValue.Create(correlationId))
        root.["cancellations"] <- cancellations

        root.ToJsonString()
