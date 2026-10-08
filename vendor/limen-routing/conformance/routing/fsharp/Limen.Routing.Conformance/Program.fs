// The F# reference routing library against the language-neutral vectors
// (conformance/routing/routing.vectors.json and url-state.vectors.json), then
// the round-trip, canonical-form and totality properties (LCP-094). Tables
// arrive as neutral JSON and are translated into F# routes here; results are
// rendered back into the neutral JSON shape and compared structurally.
open System
open System.IO
open System.Text.Json.Nodes
open Limen.Routing

let load name = JsonNode.Parse(File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", name))).AsObject()
let files = [ load "routing.vectors.json"; load "url-state.vectors.json" ]

let text (node: JsonNode) = node.GetValue<string>()
let items (node: JsonNode) = match node with | null -> [] | node -> node.AsArray() |> Seq.map id |> List.ofSeq
let kindOf (node: JsonNode) = node.GetValueKind()
let isString (node: JsonNode) = node <> null && kindOf node = System.Text.Json.JsonValueKind.String

let paramType (q: JsonNode) =
    let values () = items q["values"] |> List.map text
    match text q["type"] with
    | "int" -> ParamType.Int
    | "bool" -> ParamType.Bool
    | "date" -> ParamType.Date
    | "month" -> ParamType.Month
    | "enum" -> ParamType.Enum(values ())
    | "set" -> ParamType.Set(values ())
    | _ -> ParamType.String

/// A JSON value with no type context: the vectors' input encoding.
let rec toValue (node: JsonNode) =
    match kindOf node with
    | System.Text.Json.JsonValueKind.Number -> Value.Integer(node.GetValue<int64>())
    | System.Text.Json.JsonValueKind.True -> Value.Boolean true
    | System.Text.Json.JsonValueKind.False -> Value.Boolean false
    | System.Text.Json.JsonValueKind.Array -> Value.Members(items node |> List.map text)
    | System.Text.Json.JsonValueKind.Object when node["date"] <> null ->
        match DateOnly.TryParseExact(text node["date"], "yyyy-MM-dd") with
        | true, date -> Value.Date date
        | _ -> Value.Text(text node["date"])
    | System.Text.Json.JsonValueKind.Object -> let m = text node["month"] in Value.Month(int (m.Substring(0, 4)), int (m.Substring(5, 2)))
    | _ -> Value.Text(text node)

/// A default in a table: typed by its parameter.
let defaultValue (kind: ParamType) (node: JsonNode) =
    match kind, kindOf node with
    | ParamType.Date, System.Text.Json.JsonValueKind.String ->
        match DateOnly.TryParseExact(text node, "yyyy-MM-dd") with
        | true, date -> Value.Date date
        | _ -> Value.Text(text node)
    | ParamType.Month, System.Text.Json.JsonValueKind.String when (text node).Length = 7 -> Value.Month(int ((text node).Substring(0, 4)), int ((text node).Substring(5, 2)))
    | _ -> toValue node

let template (value: string) = if value.StartsWith "{" && value.EndsWith "}" then Template.FromParam(value.Substring(1, value.Length - 2)) else Template.Literal value

let rec toRoute (node: JsonNode) : Result<Route, DefinitionError> =
    let o = node.AsObject()
    let children = items o["children"] |> List.map toRoute

    match Route.define (text o["name"]) (text o["path"]), children |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
    | Error e, _
    | _, Some e -> Error e
    | Ok route, None ->
        let redirect =
            match o["redirect"] with
            | null -> None
            | r -> Some(text r["to"], r["params"].AsObject() |> Seq.map (fun pair -> pair.Key, template (text pair.Value)) |> List.ofSeq)

        Ok
            { route with
                Query =
                    items o["query"]
                    |> List.map (fun q ->
                        let kind = paramType q
                        { Name = text q["name"]; Type = kind; Required = q["required"].GetValue<bool>(); Default = (match q["default"] with null -> None | d -> Some(defaultValue kind d)) })
                Children = children |> List.choose (function Ok r -> Some r | Error _ -> None)
                Redirect = redirect
                Guard = (match o["guard"] with | null -> None | g -> Some(text g))
                Requires = items o["requires"] |> List.map text
                ReturnTarget = (match o["returnTarget"] with | null -> true | r -> r.GetValue<bool>()) }

let routesOf (node: JsonNode) = items node |> List.map toRoute

let legacyOf (node: JsonNode) =
    items node |> List.map (fun l -> { Path = text l["path"]; To = text l["to"]; Params = (match l["params"] with null -> [] | p -> p.AsObject() |> Seq.map (fun pair -> pair.Key, template (text pair.Value)) |> List.ofSeq) })

let rolesOf (node: JsonNode) =
    { Home = text node["home"]
      SignIn = (match node["signIn"] with null -> None | s -> Some(text s))
      NotFound = (match node["notFound"] with null -> None | s -> Some(text s)) }

let ok results = results |> List.map (function Ok r -> r | Error e -> failwith $"table does not parse: {e}")

let rawTables = files |> List.collect (fun f -> f["tables"].AsObject() |> Seq.map (fun pair -> pair.Key, pair.Value) |> List.ofSeq) |> Map.ofList

let definedTables =
    files
    |> List.collect (fun f -> match f["definitions"] with null -> [] | d -> d.AsObject() |> Seq.map (fun pair -> pair.Key, pair.Value) |> List.ofSeq)
    |> List.map (fun (name, definition) ->
        match RouteTable.define (ok (routesOf rawTables[name])) (legacyOf definition["legacy"]) (rolesOf definition["roles"]) with
        | Ok table -> name, table
        | Error errors -> failwith $"table {name} does not define: {errors}")
    |> Map.ofList

let routeList name =
    match Map.tryFind name definedTables with
    | Some table -> RouteTable.routes table
    | None -> ok (routesOf rawTables[name])

let toValues (node: JsonNode) =
    match node with
    | null -> Map.empty
    | node -> node.AsObject() |> Seq.map (fun pair -> pair.Key, toValue pair.Value) |> Map.ofSeq

let valueJson (value: Value) : JsonNode =
    match value with
    | Value.Text t -> JsonValue.Create t
    | Value.Integer i -> JsonValue.Create i
    | Value.Boolean b -> JsonValue.Create b
    | Value.Date d -> JsonObject(dict [ "date", JsonValue.Create(d.ToString("yyyy-MM-dd")) :> JsonNode ])
    | Value.Month(y, m) -> JsonObject(dict [ "month", JsonValue.Create($"%04d{y}-%02d{m}") :> JsonNode ])
    | Value.Members ms -> JsonArray(ms |> List.map (fun v -> JsonValue.Create v :> JsonNode) |> Array.ofList)

let mapJson (values: Map<string, Value>) =
    let o = JsonObject()
    values |> Map.iter (fun key value -> o[key] <- valueJson value)
    o

let strings (values: string list) = JsonArray(values |> List.map (fun v -> JsonValue.Create v :> JsonNode) |> Array.ofList)

let resolutionJson (resolution: Resolution) : JsonNode =
    let o = JsonObject()
    match resolution with
    | Resolution.Matched m ->
        o["kind"] <- "Matched"
        o["route"] <- m.Route
        o["chain"] <- JsonArray(m.Chain |> List.map (fun level -> JsonObject(dict [ "route", JsonValue.Create level.Route :> JsonNode; "params", mapJson level.Params :> JsonNode ]) :> JsonNode) |> Array.ofList)
        o["query"] <- mapJson m.Query
        o["requires"] <- strings m.Requires
        o["redirectedFrom"] <- strings m.RedirectedFrom
    | Resolution.NotFound -> o["kind"] <- "NotFound"
    | Resolution.MalformedPath -> o["kind"] <- "MalformedPath"
    | Resolution.MalformedQuery -> o["kind"] <- "MalformedQuery"
    | Resolution.TooLong -> o["kind"] <- "TooLong"
    | Resolution.Invalid(route, parameter, value, expected) ->
        o["kind"] <- "Invalid"; o["route"] <- route; o["parameter"] <- parameter; o["value"] <- value; o["expected"] <- expected
    | Resolution.RedirectLoop chain -> o["kind"] <- "RedirectLoop"; o["chain"] <- strings chain
    | Resolution.Denied route -> o["kind"] <- "Denied"; o["route"] <- route
    o

let guardsFrom (node: JsonNode) : string -> Match -> GuardDecision =
    let decisions = match node with | null -> Map.empty | node -> node.AsObject() |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq
    fun name _ ->
        match Map.tryFind name decisions with
        | None -> GuardDecision.Allow
        | Some decision when isString decision -> if text decision = "deny" then GuardDecision.Deny else GuardDecision.Allow
        | Some decision ->
            let r = decision["redirect"]
            GuardDecision.Redirect(text r["to"], toValues r["params"], toValues r["query"])

let failures = ResizeArray<string>()
let mutable passed = 0

let show (node: JsonNode) = match node with null -> "null" | n -> n.ToJsonString()
let nameOf (node: JsonNode) = text node["name"]

let check name (expected: JsonNode) (actual: JsonNode) =
    if JsonNode.DeepEquals(expected, actual) then passed <- passed + 1
    else failures.Add $"{name}\n    expected {show expected}\n    actual   {show actual}"

let str (value: string option) : JsonNode = match value with Some v -> JsonValue.Create v | None -> null

// ---------------------------------------------------------------- vectors

for file in files do
    for case in items file["resolve"] do
        let path =
            match case["pathRepeat"] with
            | null -> text case["path"]
            | r -> text r["prefix"] + String.replicate (r["times"].GetValue<int>()) (text r["text"])
        let actual = Router.resolve (routeList (text case["table"])) (guardsFrom case["guards"]) path (text case["query"])
        check $"resolve: {nameOf case}" case["expect"] (resolutionJson actual)

    for case in items file["build"] do
        let actual : JsonNode =
            match Router.build (routeList (text case["table"])) (text case["route"]) (toValues case["params"]) (toValues case["query"]) with
            | Ok location -> JsonObject(dict [ "ok", JsonValue.Create location :> JsonNode ])
            | Error error ->
                let kind, parameter =
                    match error with
                    | BuildError.UnknownRoute -> "UnknownRoute", ""
                    | BuildError.MissingParameter p -> "MissingParameter", p
                    | BuildError.InvalidParameter p -> "InvalidParameter", p
                JsonObject(dict [ "error", JsonValue.Create kind :> JsonNode; "parameter", JsonValue.Create parameter :> JsonNode ])
        check $"build: {nameOf case}" case["expect"] actual

let effectJson (effect: NavigationEffect option) : JsonNode =
    match effect with
    | None -> null
    | Some(NavigationEffect.Push url) -> JsonObject(dict [ "push", JsonValue.Create url :> JsonNode ])
    | Some(NavigationEffect.Replace url) -> JsonObject(dict [ "replace", JsonValue.Create url :> JsonNode ])

/// The browser's session history, as far as the vectors need it.
type History = { Entries: string list; Index: int }

let applyEffect (history: History) (effect: NavigationEffect option) =
    match effect with
    | None -> history
    | Some(NavigationEffect.Push url) -> { Entries = (List.take (history.Index + 1) history.Entries) @ [ url ]; Index = history.Index + 1 }
    | Some(NavigationEffect.Replace url) -> { history with Entries = history.Entries |> List.mapi (fun i e -> if i = history.Index then url else e) }

for file in files do
    for session in items file["sessions"] do
        let tableName = text session["table"]
        let table = routeList tableName
        let name = text session["name"]

        items session["steps"]
        |> List.fold (fun (state, history: History, index) (step: JsonNode) ->
            let guard = guardsFrom step["guards"]
            let actual = JsonObject()

            let adoptAt (history: History) location =
                let next, resolution, effect = Navigation.adopt table guard state location
                actual["route"] <- (match resolution with Resolution.Matched m -> JsonValue.Create m.Route :> JsonNode | _ -> null)
                actual["effect"] <- effectJson effect
                next, applyEffect history effect

            let moved (history: History) delta =
                let target = history.Index + delta
                if target < 0 || target >= history.Entries.Length then
                    actual["left"] <- true
                    state, history
                else
                    let history = { history with Index = target }
                    let location = history.Entries[target]
                    actual["location"] <- location
                    adoptAt history location

            let next, history =
                if step["adopt"] <> null then
                    let location = text step["adopt"]
                    adoptAt (if history.Entries.IsEmpty then { Entries = [ location ]; Index = 0 } else applyEffect history (Some(NavigationEffect.Push location))) location
                elif step["navigate"] <> null || step["refine"] <> null then
                    let n, operation = if step["navigate"] <> null then step["navigate"], Navigation.navigate else step["refine"], Navigation.refine
                    match operation table state (text n["route"]) (toValues n["params"]) (toValues n["query"]) with
                    | Ok(next, effect) ->
                        actual["route"] <- text n["route"]
                        actual["effect"] <- effectJson effect
                        next, applyEffect history effect
                    | Error error -> failwith $"{name}: {error}"
                elif step["back"] <> null then moved history -1
                elif step["forward"] <> null then moved history 1
                else
                    let target = ReturnTo.resume definedTables[tableName] guard (Some(text step["resume"]))
                    let next, effect = Navigation.replace state target
                    actual["route"] <- (match Router.resolveLocation table guard target with Resolution.Matched m -> JsonValue.Create m.Route :> JsonNode | _ -> null)
                    actual["effect"] <- effectJson effect
                    next, applyEffect history effect

            check $"session {name} step {index}" step["expect"] actual
            next, history, index + 1) (Navigation.initial, { Entries = []; Index = -1 }, 1)
        |> ignore

let definitionErrorJson (error: DefinitionError) : JsonNode =
    let pairs =
        match error with
        | DefinitionError.InvalidSegment(route, segment) -> [ "kind", "InvalidSegment"; "route", route; "segment", segment ]
        | DefinitionError.DuplicateName route -> [ "kind", "DuplicateName"; "route", route ]
        | DefinitionError.DuplicateParameter(route, p) -> [ "kind", "DuplicateParameter"; "route", route; "parameter", p ]
        | DefinitionError.ReservedName(route, p) -> [ "kind", "ReservedName"; "route", route; "parameter", p ]
        | DefinitionError.InvalidValues(route, p) -> [ "kind", "InvalidValues"; "route", route; "parameter", p ]
        | DefinitionError.InvalidDefault(route, p) -> [ "kind", "InvalidDefault"; "route", route; "parameter", p ]
        | DefinitionError.RequiredWithDefault(route, p) -> [ "kind", "RequiredWithDefault"; "route", route; "parameter", p ]
        | DefinitionError.UnknownTarget(route, target) -> [ "kind", "UnknownTarget"; "route", route; "target", target ]
        | DefinitionError.UnknownParameter(route, p) -> [ "kind", "UnknownParameter"; "route", route; "parameter", p ]
        | DefinitionError.UnknownRole(role, route) -> [ "kind", "UnknownRole"; "role", role; "route", route ]
    JsonObject(pairs |> List.map (fun (k, v) -> Collections.Generic.KeyValuePair(k, JsonValue.Create v :> JsonNode)))

for file in files do
    for case in items file["definitionCases"] do
        let routes = if isString case["routes"] then routesOf rawTables[text case["routes"]] else routesOf case["routes"]
        let actual : JsonNode =
            let parseErrors = routes |> List.choose (function Error e -> Some e | Ok _ -> None)
            let result =
                if parseErrors.IsEmpty then RouteTable.define (routes |> List.choose (function Ok r -> Some r | Error _ -> None)) (legacyOf case["legacy"]) (rolesOf case["roles"]) |> Result.map ignore
                else Error parseErrors
            match result with
            | Ok() -> JsonObject(dict [ "ok", JsonValue.Create true :> JsonNode ])
            | Error errors -> JsonObject(dict [ "errors", JsonArray(errors |> List.map definitionErrorJson |> Array.ofList) :> JsonNode ])
        check $"definition: {nameOf case}" case["expect"] actual

    for case in items file["returnTo"] do
        let table = definedTables[text case["table"]]
        let optional (node: JsonNode) = match node with null -> None | n -> Some(text n)
        let actual =
            if case.AsObject().ContainsKey "capture" then str (ReturnTo.capture table (text case["capture"]))
            elif case.AsObject().ContainsKey "resume" then JsonValue.Create(ReturnTo.resume table (guardsFrom case["guards"]) (optional case["resume"])) :> JsonNode
            else
                match ReturnTo.signIn table (optional case["signIn"]) with
                | Ok location -> JsonValue.Create location
                | Error e -> JsonValue.Create $"error {e}"
        check $"returnTo: {nameOf case}" case["expect"] actual

    for case in items file["locations"] do
        let mode = if text case["mode"] = "hash" then LocationMode.Hash else LocationMode.Path
        let page () =
            let l = case["location"]
            { Origin = text l["origin"]; Path = text l["path"]; Query = text l["query"]; Hash = text l["hash"] }
        let actual =
            if case["href"] <> null then Location.href mode (text case["href"])
            elif case["share"] <> null then Link.share mode (page ()) (text case["share"])
            else Location.ofBrowser mode (page ())
        check $"location: {nameOf case}" case["expect"] (JsonValue.Create actual)

    for case in items file["outcomes"] do
        let location =
            match case["locationRepeat"] with
            | null -> text case["location"]
            | r -> text r["prefix"] + String.replicate (r["times"].GetValue<int>()) (text r["text"])
        let table = definedTables[text case["table"]]
        let actual : JsonNode =
            let pairs : (string * string) list =
                match Router.resolveLocation (RouteTable.routes table) (guardsFrom case["guards"]) location |> RouteError.ofResolution table with
                | Ok m -> [ "ok", m.Route ]
                | Error RouteError.NotFound -> [ "error", "NotFound" ]
                | Error(RouteError.NotPermitted route) -> [ "error", "NotPermitted"; "route", route ]
                | Error(RouteError.Invalid(route, p, v, e)) -> [ "error", "Invalid"; "route", route; "parameter", p; "value", v; "expected", e ]
                | Error(RouteError.Malformed part) -> [ "error", "Malformed"; "part", part ]
                | Error(RouteError.RedirectLoop _) -> [ "error", "RedirectLoop" ]
                | Error(RouteError.Unmapped(route, problem)) -> [ "error", "Unmapped"; "route", route; "problem", problem ]
            JsonObject(pairs |> List.map (fun (k, v) -> Collections.Generic.KeyValuePair(k, JsonValue.Create v :> JsonNode)))
        check $"outcome: {nameOf case}" case["expect"] actual

    for case in items file["inventory"] do
        let mode = if text case["mode"] = "hash" then LocationMode.Hash else LocationMode.Path
        let actual = Inventory.render mode definedTables[text case["table"]]
        if actual = text case["expect"] then passed <- passed + 1
        else failures.Add $"inventory: {nameOf case}\n{actual}"

let vectorCount = passed

// ------------------------------------------------------------- properties

/// The views table as an application's own route type (LCP-093).
type Filter = { Status: string list; Sort: string; Page: int64; Archived: bool; Q: string option }

type View =
    | Home
    | Invoices of Filter
    | Invoice of id: int64 * filter: Filter * tab: string
    | Report of period: (int * int) * on: DateOnly option * tags: string list
    | Day of DateOnly
    | Board of string
    | SignIn of returnTo: string option
    | Admin

let viewsTable = definedTables["views"]

let filterQuery (f: Filter) =
    Map [ "status", Value.Members f.Status; "sort", Value.Text f.Sort; "page", Value.Integer f.Page; "archived", Value.Boolean f.Archived
          match f.Q with Some q -> "q", Value.Text q | None -> () ]

let toTarget (view: View) : Target =
    match view with
    | Home -> { Route = "home"; Params = Map.empty; Query = Map.empty }
    | Invoices f -> { Route = "invoices.list"; Params = Map.empty; Query = filterQuery f }
    | Invoice(id, f, tab) -> { Route = "invoices.invoice"; Params = Map [ "id", Value.Integer id ]; Query = filterQuery f |> Map.add "tab" (Value.Text tab) }
    | Report((y, m), on, tags) ->
        { Route = "reports"; Params = Map [ "period", Value.Month(y, m) ]
          Query = Map [ match on with Some d -> "on", Value.Date d | None -> ()
                        "tags", Value.Members tags ] }
    | Day d -> { Route = "day"; Params = Map [ "on", Value.Date d ]; Query = Map.empty }
    | Board v -> { Route = "board"; Params = Map [ "view", Value.Text v ]; Query = Map.empty }
    | SignIn target -> { Route = "signIn"; Params = Map.empty; Query = (match target with Some t -> Map [ "returnTo", Value.Text t ] | None -> Map.empty) }
    | Admin -> { Route = "admin"; Params = Map.empty; Query = Map.empty }

let ofMatch (m: Match) : Result<View, string> =
    let p = m.Chain |> List.collect (fun l -> Map.toList l.Params) |> Map.ofList
    let filter () =
        match m.Query.TryFind "sort", m.Query.TryFind "page", m.Query.TryFind "archived" with
        | Some(Value.Text sort), Some(Value.Integer page), Some(Value.Boolean archived) ->
            Ok { Status = (match m.Query.TryFind "status" with Some(Value.Members s) -> s | _ -> [])
                 Sort = sort; Page = page; Archived = archived
                 Q = (match m.Query.TryFind "q" with Some(Value.Text q) -> Some q | _ -> None) }
        | _ -> Error "filter"
    match m.Route, p.TryFind "id", p.TryFind "period", p.TryFind "on", p.TryFind "view" with
    | "home", _, _, _, _ -> Ok Home
    | "invoices.list", _, _, _, _ -> filter () |> Result.map Invoices
    | "invoices.invoice", Some(Value.Integer id), _, _, _ ->
        match filter (), m.Query.TryFind "tab" with
        | Ok f, Some(Value.Text tab) -> Ok(Invoice(id, f, tab))
        | _ -> Error "invoice"
    | "reports", _, Some(Value.Month(y, mo)), _, _ ->
        Ok(Report((y, mo), (match m.Query.TryFind "on" with Some(Value.Date d) -> Some d | _ -> None), (match m.Query.TryFind "tags" with Some(Value.Members t) -> t | _ -> [])))
    | "day", _, _, Some(Value.Date d), _ -> Ok(Day d)
    | "board", _, _, _, Some(Value.Text v) -> Ok(Board v)
    | "signIn", _, _, _, _ -> Ok(SignIn(match m.Query.TryFind "returnTo" with Some(Value.Text t) -> Some t | _ -> None))
    | "admin", _, _, _, _ -> Ok Admin
    | route, _, _, _, _ -> Error $"unmapped {route}"

let codec = RouteCodec.create viewsTable toTarget ofMatch
let random = Random 20261008
let pick (xs: 'a list) = xs[random.Next xs.Length]
let alphabet = [ "a"; "b"; "Z"; "0"; " "; ","; "&"; "="; "?"; "#"; "/"; "%"; "+"; "~"; "é"; "日本"; "😀"; "-"; "_"; "."; "'"; "\"" ]
let word () = String.concat "" [ for _ in 1 .. random.Next(1, 7) -> pick alphabet ]
let maybe f = if random.Next 2 = 0 then None else Some(f ())
let subset (values: string list) = values |> List.filter (fun _ -> random.Next 2 = 0)
let sorted (xs: string list) = xs |> List.distinct |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
let safeInt () = pick [ 0L; 1L; -1L; 9007199254740991L; -9007199254740991L; int64 (random.Next()); -(int64 (random.Next())) ]
let date () = DateOnly(random.Next(1, 10000), random.Next(1, 13), 1).AddDays(random.Next 28)

let filter () =
    { Status = sorted (subset [ "draft"; "open"; "overdue"; "paid" ]); Sort = pick [ "due"; "amount"; "number" ]
      Page = safeInt (); Archived = random.Next 2 = 0; Q = maybe word }

let view () =
    match random.Next 8 with
    | 0 -> Home
    | 1 -> Invoices(filter ())
    | 2 -> Invoice(safeInt (), filter (), pick [ "summary"; "history"; "lines" ])
    | 3 -> Report((random.Next(1, 10000), random.Next(1, 13)), maybe date, sorted [ for _ in 1 .. random.Next 4 -> word () ])
    | 4 -> Day(date ())
    | 5 -> Board(pick [ "week"; "month" ])
    | 6 -> SignIn(maybe (fun () -> "/" + word ()))
    | _ -> Admin

let propertyFailures = ResizeArray<string>()
let cases = 3000

// 1. parse (format v) = Ok v
for _ in 1 .. cases do
    let v = view ()
    match RouteCodec.format codec v with
    | Error e -> propertyFailures.Add $"format {v}: {e}"
    | Ok location ->
        match RouteCodec.parse codec Router.allowAll location with
        | Ok parsed when parsed = v -> ()
        | other -> propertyFailures.Add $"round trip {v} via {location}: {other}"

// 2. canonical forms are stable, and mutated locations still resolve to values
let mutate (location: string) =
    match random.Next 7 with
    | 0 -> location + (if location.Contains '?' then "&" else "?") + "utm=" + string (random.Next 100)
    | 1 -> location + "/"
    | 2 -> location.Replace(",", ",,")
    | 3 -> location.Insert(random.Next(location.Length + 1), pick [ "%"; "%zz"; "%C3"; "+"; "&&"; "=" ])
    | 4 -> location.ToUpperInvariant()
    | 5 -> location + (if location.Contains '?' then "&" else "?") + "page=1&page=1"
    | _ -> location.Replace("sort=", "sort=due&sort=")

let routes = RouteTable.routes viewsTable
for _ in 1 .. cases do
    match RouteCodec.format codec (view ()) with
    | Error _ -> ()
    | Ok location ->
        let candidate = mutate location
        match Router.resolveLocation routes Router.allowAll candidate with
        | Resolution.Matched m ->
            match Router.canonical routes m with
            | Ok canonical ->
                match Router.resolveLocation routes Router.allowAll canonical with
                | Resolution.Matched again when Router.canonical routes again = Ok canonical -> ()
                | other -> propertyFailures.Add $"canonical {canonical} of {candidate} is not stable: {other}"
            | Error e -> propertyFailures.Add $"no canonical form for a match of {candidate}: {e}"
        | _ -> ()

// 3. totality: every string resolves, parses and resumes without an exception
let noise () =
    String(Array.init (random.Next 40) (fun _ -> char (pick [ random.Next(0, 128); random.Next(0xD800, 0xE000); random.Next(0x80, 0x3000); int '%'; int '/' ])))

for _ in 1 .. cases do
    let candidate = if random.Next 2 = 0 then "/" + noise () else noise ()
    try
        Router.resolveLocation routes (guardsFrom null) candidate |> ignore
        RouteCodec.parse codec Router.allowAll candidate |> ignore
        let resumed = ReturnTo.resume viewsTable Router.allowAll (Some candidate)
        if not (ReturnTo.isRelative resumed) then propertyFailures.Add $"resume gave {resumed}"
        ReturnTo.capture viewsTable candidate |> ignore
    with error -> propertyFailures.Add $"threw on {candidate}: {error.Message}"

if failures.Count = 0 && propertyFailures.Count = 0 then
    printfn "Routing conformance: %d/%d vectors agree (F# reference library)." vectorCount vectorCount
    printfn "Routing properties: %d round trips, %d canonical-form checks and %d totality checks hold." cases cases cases
else
    failures |> Seq.iter (eprintfn "FAIL %s")
    propertyFailures |> Seq.truncate 20 |> Seq.iter (eprintfn "PROPERTY %s")
    eprintfn "%d routing vector(s) disagree; %d agree. %d property failure(s)." failures.Count passed propertyFailures.Count
    Environment.ExitCode <- 1
