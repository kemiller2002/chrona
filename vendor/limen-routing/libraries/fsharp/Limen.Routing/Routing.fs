// Limen routing: the F# reference implementation of the language-neutral
// semantics in conformance/routing/README.md (kemiller2002/limen#20, LCP-005,
// and the URL-state cluster LCP-088..112).
//
// Routing is application meaning, so it lives in the engine. This module
// resolves a location the browser reported, builds the canonical location
// for a destination, and decides the one Navigation effect (push, replace or
// none) that keeps the browser's history consistent with the engine. It
// never touches the browser; the kernel performs the effect.
//
// Every function is total: malformed input is a value, never an exception.
// The API shape is F#'s own; only the semantics are shared across languages.
namespace Limen.Routing

open System
open System.Globalization
open System.Text

[<RequireQualifiedAccess>]
type ParamType =
    | String
    | Int
    | Bool
    /// An ISO-8601 calendar date, YYYY-MM-DD.
    | Date
    /// A year and month, YYYY-MM: a period.
    | Month
    /// One of the declared values.
    | Enum of values: string list
    /// Members sorted and unique, joined by ",". An empty list allows any
    /// non-empty strings.
    | Set of values: string list

[<RequireQualifiedAccess>]
type Segment =
    | Literal of string
    | Param of name: string * ParamType
    | Wildcard of name: string

[<RequireQualifiedAccess>]
type Value =
    | Text of string
    | Integer of int64
    | Boolean of bool
    | Date of DateOnly
    | Month of year: int * month: int
    | Members of string list

type QueryParam =
    { Name: string
      Type: ParamType
      Required: bool
      /// Reported when the key is absent, and omitted from the canonical form.
      Default: Value option }

/// A value in a route template: a source parameter to copy, or a literal.
[<RequireQualifiedAccess>]
type Template =
    | FromParam of string
    | Literal of string

type Route =
    { Name: string
      Path: Segment list
      Query: QueryParam list
      Children: Route list
      Redirect: (string * (string * Template) list) option
      Guard: string option
      Requires: string list
      /// Whether a sign-in may return here (LCP-101).
      ReturnTarget: bool }

type Level = { Route: string; Params: Map<string, Value> }

type Match =
    { Route: string
      Chain: Level list
      Query: Map<string, Value>
      Requires: string list
      RedirectedFrom: string list }

[<RequireQualifiedAccess>]
type GuardDecision =
    | Allow
    | Deny
    | Redirect of route: string * parameters: Map<string, Value> * query: Map<string, Value>

[<RequireQualifiedAccess>]
type Resolution =
    | Matched of Match
    | NotFound
    | MalformedPath
    | MalformedQuery
    /// Longer than 8,192 characters: refused before decoding.
    | TooLong
    | Invalid of route: string * parameter: string * value: string * expected: string
    | RedirectLoop of chain: string list
    | Denied of route: string

[<RequireQualifiedAccess>]
type BuildError =
    | UnknownRoute
    | MissingParameter of string
    | InvalidParameter of string

[<RequireQualifiedAccess>]
type NavigationEffect =
    | Push of string
    | Replace of string

[<RequireQualifiedAccess>]
type DefinitionError =
    | InvalidSegment of route: string * segment: string
    | DuplicateName of route: string
    | DuplicateParameter of route: string * parameter: string
    | ReservedName of route: string * parameter: string
    | InvalidValues of route: string * parameter: string
    | InvalidDefault of route: string * parameter: string
    | RequiredWithDefault of route: string * parameter: string
    | UnknownTarget of route: string * target: string
    | UnknownParameter of route: string * parameter: string
    | UnknownRole of role: string * route: string

/// The routes that play a part the module knows about.
type Roles =
    { Home: string
      SignIn: string option
      NotFound: string option }

/// An old URL pattern that now lives elsewhere (LCP-105).
type LegacyRoute =
    { Path: string
      To: string
      Params: (string * Template) list }

/// A validated table: built only by RouteTable.define.
type RouteTable =
    private
        { routes: Route list
          legacy: LegacyRoute list
          roles: Roles
          matching: Route list }

module Route =
    let private parseType (text: string) =
        match text.Split(':', 2) with
        | [| "string" |] -> Some ParamType.String
        | [| "int" |] -> Some ParamType.Int
        | [| "date" |] -> Some ParamType.Date
        | [| "month" |] -> Some ParamType.Month
        | [| "enum"; values |] when values <> "" -> Some(ParamType.Enum(List.ofArray (values.Split '|')))
        | _ -> None

    let private parseSegment (text: string) : Result<Segment, string> =
        if text.StartsWith "{*" && text.EndsWith "}" && text.Length > 3 then Ok(Segment.Wildcard(text.Substring(2, text.Length - 3)))
        elif text.StartsWith "{" && text.EndsWith "}" then
            let body = text.Substring(1, text.Length - 2)
            match body.IndexOf ':' with
            | -1 when body <> "" -> Ok(Segment.Param(body, ParamType.String))
            | -1 -> Error text
            | index ->
                match parseType (body.Substring(index + 1)) with
                | Some kind when index > 0 -> Ok(Segment.Param(body.Substring(0, index), kind))
                | _ -> Error text
        elif text.Contains '{' || text.Contains '}' then Error text
        else Ok(Segment.Literal text)

    /// "invoices/{id:int}/lines/{line:int}" → segments, or the first segment
    /// that is not one. "" is an index. Types: string (the default), int,
    /// date, month and enum:a|b.
    let tryPath (text: string) : Result<Segment list, string> =
        text.Split('/', StringSplitOptions.RemoveEmptyEntries)
        |> List.ofArray
        |> List.fold (fun acc piece -> acc |> Result.bind (fun segments -> parseSegment piece |> Result.map (fun s -> s :: segments))) (Ok [])
        |> Result.map List.rev

    let private blank name segments =
        { Name = name
          Path = segments
          Query = []
          Children = []
          Redirect = None
          Guard = None
          Requires = []
          ReturnTarget = true }

    /// A route, or InvalidSegment for a path that does not parse.
    let define name pathText : Result<Route, DefinitionError> =
        tryPath pathText |> Result.map (blank name) |> Result.mapError (fun segment -> DefinitionError.InvalidSegment(name, segment))

    /// A route from a path literal written in code. It throws on a path that
    /// does not parse, as before 0.9.0; Route.define is the total form.
    let create name pathText =
        match tryPath pathText with
        | Ok segments -> blank name segments
        | Error segment -> invalidArg "pathText" $"Unknown parameter segment {segment}"

module QueryParam =
    let optional name kind = { Name = name; Type = kind; Required = false; Default = None }
    let required name kind = { Name = name; Type = kind; Required = true; Default = None }
    let withDefault value (parameter: QueryParam) = { parameter with Default = Some value }

module private Text =
    let private strict = UTF8Encoding(false, true)

    let private isUnreserved (b: byte) =
        (b >= byte 'A' && b <= byte 'Z') || (b >= byte 'a' && b <= byte 'z') || (b >= byte '0' && b <= byte '9')
        || b = byte '-' || b = byte '.' || b = byte '_' || b = byte '~'

    /// Every UTF-8 byte except A–Z a–z 0–9 - . _ ~ as %XX. None for text that
    /// is not valid UTF-16 (a lone surrogate).
    let tryEncode (value: string) =
        try
            strict.GetBytes value
            |> Array.map (fun b -> if isUnreserved b then string (char b) else $"%%{int b:X2}")
            |> String.concat ""
            |> Some
        with :? EncoderFallbackException -> None

    let private hex (c: char) =
        if c >= '0' && c <= '9' then Some(int c - int '0')
        elif c >= 'A' && c <= 'F' then Some(int c - int 'A' + 10)
        elif c >= 'a' && c <= 'f' then Some(int c - int 'a' + 10)
        else None

    /// Strict percent-decoding as UTF-8: an invalid escape, a lone surrogate
    /// or invalid UTF-8 is None.
    let decode (plusIsSpace: bool) (value: string) =
        let isPlain (c: char) = c <> '%' && not (plusIsSpace && c = '+')

        let rec bytes index (acc: byte list) =
            if index >= value.Length then Some(List.rev acc)
            else
                match value[index] with
                | '%' when index + 2 < value.Length ->
                    match hex value[index + 1], hex value[index + 2] with
                    | Some high, Some low -> bytes (index + 3) (byte (high * 16 + low) :: acc)
                    | _ -> None
                | '%' -> None
                | '+' when plusIsSpace -> bytes (index + 1) (byte ' ' :: acc)
                | _ ->
                    let run = value.Substring(index) |> Seq.takeWhile isPlain |> Seq.length
                    bytes (index + run) (List.rev (List.ofArray (strict.GetBytes(value.Substring(index, run)))) @ acc)

        try
            bytes 0 [] |> Option.map (fun decoded -> strict.GetString(Array.ofList decoded))
        with
        | :? EncoderFallbackException
        | :? DecoderFallbackException -> None

/// Typed values: conversion from and to their canonical text.
module private Values =
    let maxSafe = 9007199254740991L

    let private ascii (text: string) = text |> Seq.forall Char.IsAsciiDigit

    let parseInt (text: string) =
        let canonical =
            text = "0"
            || (text.Length > 0
                && (let digits = if text.StartsWith "-" then text.Substring 1 else text
                    digits.Length > 0 && digits.Length <= 16 && digits[0] <> '0' && ascii digits))

        if not canonical then None
        else
            match Int64.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
            | true, value when abs value <= maxSafe -> Some value
            | _ -> None

    let parseDate (text: string) =
        if text.Length = 10 && text[4] = '-' && text[7] = '-' && ascii (text.Substring(0, 4) + text.Substring(5, 2) + text.Substring(8, 2)) then
            match DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
            | true, date -> Some date
            | _ -> None
        else None

    let parseMonth (text: string) =
        if text.Length = 7 && text[4] = '-' && ascii (text.Substring(0, 4) + text.Substring(5, 2)) then
            let year = int (text.Substring(0, 4))
            let month = int (text.Substring(5, 2))
            if year >= 1 && month >= 1 && month <= 12 then Some(year, month) else None
        else None

    let typeName kind =
        match kind with
        | ParamType.String -> "string"
        | ParamType.Int -> "int"
        | ParamType.Bool -> "bool"
        | ParamType.Date -> "date"
        | ParamType.Month -> "month"
        | ParamType.Enum _ -> "enum"
        | ParamType.Set _ -> "set"

    let expected kind =
        match kind with
        | ParamType.Enum values -> "one of " + String.concat "|" values
        | ParamType.Set [] -> "a set of non-empty values"
        | ParamType.Set values -> "a set of " + String.concat "|" values
        | other -> typeName other

    let private members (values: string list) (items: string list) =
        if items |> List.exists (fun item -> item = "") then None
        elif not values.IsEmpty && items |> List.exists (fun item -> not (List.contains item values)) then None
        else Some(items |> List.distinct |> List.sortWith (fun a b -> String.CompareOrdinal(a, b)))

    /// A decoded path segment or query value → a typed value. A set is
    /// converted by convertSet, from its raw (still encoded) text.
    let convert kind (text: string) =
        match kind with
        | ParamType.String -> Some(Value.Text text)
        | ParamType.Int -> parseInt text |> Option.map Value.Integer
        | ParamType.Bool ->
            match text with
            | "true" -> Some(Value.Boolean true)
            | "false" -> Some(Value.Boolean false)
            | _ -> None
        | ParamType.Date -> parseDate text |> Option.map Value.Date
        | ParamType.Month -> parseMonth text |> Option.map Value.Month
        | ParamType.Enum values -> if List.contains text values then Some(Value.Text text) else None
        | ParamType.Set values -> members values (List.ofArray (text.Split ',')) |> Option.map Value.Members

    /// A raw (encoded) query value of a set type: split on ",", then decode
    /// each member, so an encoded comma stays inside its member.
    let convertSet values (raw: string) =
        let pieces = raw.Split ',' |> List.ofArray |> List.map (Text.decode true)
        if pieces |> List.forall Option.isSome then members values (pieces |> List.choose id) |> Option.map Value.Members else None

    let private dateText (date: DateOnly) = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

    /// A value → its canonical, percent-encoded text, or None when it is not
    /// a value of that type. An empty set is Some "".
    let render kind (value: Value) =
        let encoded = Text.tryEncode
        match kind, value with
        | ParamType.String, Value.Text text -> encoded text
        | ParamType.Int, Value.Integer number when abs number <= maxSafe -> Some(number.ToString(CultureInfo.InvariantCulture))
        | ParamType.Int, Value.Text text when (parseInt text).IsSome -> Some text
        | ParamType.Bool, Value.Boolean flag -> Some(if flag then "true" else "false")
        | ParamType.Date, Value.Date date -> Some(dateText date)
        | ParamType.Month, Value.Month(year, month) when year >= 1 && year <= 9999 && month >= 1 && month <= 12 -> Some $"%04d{year}-%02d{month}"
        | ParamType.Enum values, Value.Text text when List.contains text values -> encoded text
        | ParamType.Set values, Value.Members items ->
            members values items
            |> Option.bind (fun sorted ->
                let pieces = sorted |> List.map encoded
                if pieces |> List.forall Option.isSome then Some(pieces |> List.choose id |> String.concat ",") else None)
        | _ -> None

    /// Whether two values of one type have the same canonical text.
    let same kind a b =
        match render kind a, render kind b with
        | Some x, Some y -> x = y
        | _ -> false

module Router =
    let private full (prefix: string) (name: string) = if prefix = "" then name else $"{prefix}.{name}"

    /// Locations longer than this are refused before decoding (LCP-095).
    let maxLength = 8192

    // ------------------------------------------------------------------
    // Structural matching: literals and segment counts only; types later.
    // ------------------------------------------------------------------

    type private Binding = { Level: string; Name: string; Type: ParamType; Raw: string }

    let rec private consume (level: string) (pattern: Segment list) (segments: string list) (bound: Binding list) =
        match pattern, segments with
        | [], rest -> Some(List.rev bound, rest)
        | [ Segment.Wildcard name ], rest -> Some(List.rev ({ Level = level; Name = name; Type = ParamType.String; Raw = String.concat "/" rest } :: bound), [])
        | Segment.Literal literal :: pattern, segment :: rest when segment = literal -> consume level pattern rest bound
        | Segment.Param(name, kind) :: pattern, segment :: rest -> consume level pattern rest ({ Level = level; Name = name; Type = kind; Raw = segment } :: bound)
        | _ -> None

    let rec private matchRoute (prefix: string) (route: Route) (segments: string list) : ((string * Route) list * Binding list) option =
        let name = full prefix route.Name

        consume name route.Path segments []
        |> Option.bind (fun (bound, rest) ->
            if List.isEmpty route.Children then
                if List.isEmpty rest then Some([ name, route ], bound) else None
            else
                route.Children
                |> List.tryPick (fun child -> matchRoute name child rest)
                |> Option.map (fun (chain, childBound) -> (name, route) :: chain, bound @ childBound))

    let private matchTable (table: Route list) segments =
        table |> List.tryPick (fun route -> matchRoute "" route segments)

    /// The chain of a destination's full name: (full name, route) per level.
    let chainOf (table: Route list) (fullName: string) =
        let rec walk (routes: Route list) (names: string list) (prefix: string) acc =
            match names with
            | [] -> None
            | name :: rest ->
                routes
                |> List.tryFind (fun route -> route.Name = name)
                |> Option.bind (fun route ->
                    let chain = acc @ [ full prefix name, route ]
                    if List.isEmpty rest then (if List.isEmpty route.Children then Some chain else None)
                    else walk route.Children rest (full prefix name) chain)

        walk table (List.ofArray (fullName.Split '.')) "" []

    // ------------------------------------------------------------------
    // Building
    // ------------------------------------------------------------------

    let private sequence (results: Result<'a, 'e> list) =
        List.foldBack (fun item acc -> Result.bind (fun items -> Result.map (fun value -> value :: items) item) acc) results (Ok [])

    let build (table: Route list) (fullName: string) (parameters: Map<string, Value>) (query: Map<string, Value>) : Result<string, BuildError> =
        match chainOf table fullName with
        | None -> Error BuildError.UnknownRoute
        | Some chain ->
            let routes = chain |> List.map snd

            let segment piece =
                match piece with
                | Segment.Literal literal -> Text.tryEncode literal |> Option.map Ok |> Option.defaultValue (Error(BuildError.InvalidParameter literal))
                | Segment.Param(name, kind) ->
                    match Map.tryFind name parameters with
                    | None -> Error(BuildError.MissingParameter name)
                    | Some value -> Values.render kind value |> Option.map Ok |> Option.defaultValue (Error(BuildError.InvalidParameter name))
                | Segment.Wildcard name ->
                    match Map.tryFind name parameters with
                    | Some(Value.Text text) ->
                        let pieces = text.Split('/', StringSplitOptions.RemoveEmptyEntries) |> Array.map Text.tryEncode
                        if pieces |> Array.forall Option.isSome then Ok(pieces |> Array.choose id |> String.concat "/") else Error(BuildError.InvalidParameter name)
                    | Some _ -> Error(BuildError.InvalidParameter name)
                    | None -> Ok ""

            let pair (declared: QueryParam) =
                let absent () = if declared.Required then Some(Error(BuildError.MissingParameter declared.Name)) else None

                match Map.tryFind declared.Name query with
                | None -> absent ()
                | Some value ->
                    match Values.render declared.Type value, Text.tryEncode declared.Name with
                    | None, _
                    | _, None -> Some(Error(BuildError.InvalidParameter declared.Name))
                    | Some "", _ when (match declared.Type with ParamType.Set _ -> true | _ -> false) -> absent ()
                    | Some text, Some key ->
                        match declared.Default with
                        | Some fallback when not declared.Required && Values.same declared.Type fallback value -> None
                        | _ -> Some(Ok $"{key}={text}")

            let path = routes |> List.collect (fun route -> route.Path) |> List.map segment |> sequence
            let pairs = routes |> List.collect (fun route -> route.Query) |> List.choose pair |> sequence

            match path, pairs with
            | Error error, _
            | _, Error error -> Error error
            | Ok segments, Ok pairs ->
                let joined = "/" + (segments |> List.filter (fun piece -> piece <> "") |> String.concat "/")
                Ok(if List.isEmpty pairs then joined else joined + "?" + String.concat "&" pairs)

    // ------------------------------------------------------------------
    // Resolving
    // ------------------------------------------------------------------

    let private decodePath (path: string) =
        let pieces = path.Split('/', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray |> List.map (Text.decode false)
        if pieces |> List.forall Option.isSome then Some(pieces |> List.choose id) else None

    type private Pair = { Key: string; Raw: string; Decoded: string }

    let private decodeQuery (query: string) =
        let raw = if query.StartsWith "?" then query.Substring 1 else query

        let pieces =
            raw.Split('&', StringSplitOptions.RemoveEmptyEntries)
            |> List.ofArray
            |> List.map (fun part ->
                let index = part.IndexOf '='
                let key, value = if index < 0 then part, "" else part.Substring(0, index), part.Substring(index + 1)
                match Text.decode true key, Text.decode true value with
                | Some key, Some decoded -> Some { Key = key; Raw = value; Decoded = decoded }
                | _ -> None)

        if pieces |> List.forall Option.isSome then Some(pieces |> List.choose id) else None

    let splitLocation (location: string) =
        let index = location.IndexOf '?'
        if index < 0 then location, "" else location.Substring(0, index), location.Substring index

    /// Step 4: typed path parameters, per chain level.
    let private typedLevels (chain: (string * Route) list) (bindings: Binding list) =
        let converted =
            bindings
            |> List.map (fun binding ->
                match Values.convert binding.Type binding.Raw with
                | Some value -> Ok(binding.Level, binding.Name, value)
                | None -> Error(Resolution.Invalid(binding.Level, binding.Name, binding.Raw, Values.expected binding.Type)))

        match converted |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
        | Some invalid -> Error invalid
        | None ->
            let values = converted |> List.choose (function Ok v -> Some v | Error _ -> None)
            Ok(chain |> List.map (fun (name, route) -> { Route = route.Name; Params = values |> List.filter (fun (level, _, _) -> level = name) |> List.map (fun (_, key, value) -> key, value) |> Map.ofList }), values)

    /// Step 6: declared query parameters along the chain, parent first.
    let private typedQuery (chain: (string * Route) list) (pairs: Pair list) =
        let declared = chain |> List.collect (fun (name, route) -> route.Query |> List.map (fun parameter -> name, parameter))

        let check (level, parameter: QueryParam) =
            match pairs |> List.filter (fun pair -> pair.Key = parameter.Name) with
            | [] when parameter.Required -> Error(Resolution.Invalid(level, parameter.Name, "", $"{Values.expected parameter.Type} (required)"))
            | [] -> Ok(parameter.Default |> Option.map (fun value -> parameter.Name, value))
            | [ pair ] ->
                let converted =
                    match parameter.Type with
                    | ParamType.Set values -> Values.convertSet values pair.Raw
                    | kind -> Values.convert kind pair.Decoded

                match converted with
                | Some value -> Ok(Some(parameter.Name, value))
                | None -> Error(Resolution.Invalid(level, parameter.Name, pair.Decoded, Values.expected parameter.Type))
            | many -> Error(Resolution.Invalid(level, parameter.Name, many |> List.map (fun pair -> pair.Decoded) |> String.concat ",", "a single value"))

        declared |> List.map check |> sequence |> Result.map (List.choose id >> Map.ofList)

    let rec private resolveFrom (table: Route list) (guard: string -> Match -> GuardDecision) (visited: string list) (path: string) (query: string) =
        match decodePath path, decodeQuery query with
        | None, _ -> Resolution.MalformedPath
        | _, None -> Resolution.MalformedQuery
        | Some segments, Some pairs ->
            match matchTable table segments with
            | None -> Resolution.NotFound
            | Some(chain, bindings) ->
                match typedLevels chain bindings with
                | Error invalid -> invalid
                | Ok(levels, values) ->
                    let destination = chain |> List.last |> fst
                    let visited = visited @ [ destination ]

                    if List.contains destination (List.take (visited.Length - 1) visited) then
                        Resolution.RedirectLoop visited
                    else
                        let follow target (parameters: Map<string, Value>) (targetQuery: string) =
                            match build table target parameters Map.empty with
                            | Error _ -> Resolution.Invalid(destination, target, "", "a buildable redirect target")
                            | Ok location -> resolveFrom table guard visited (fst (splitLocation location)) targetQuery

                        match chain |> List.last |> snd |> fun route -> route.Redirect with
                        | Some(target, templates) ->
                            let lookup = values |> List.map (fun (_, key, value) -> key, value) |> Map.ofList

                            let parameters =
                                templates
                                |> List.choose (fun (key, template) ->
                                    match template with
                                    | Template.FromParam source -> Map.tryFind source lookup |> Option.map (fun value -> key, value)
                                    | Template.Literal literal -> Some(key, Value.Text literal))
                                |> Map.ofList

                            follow target parameters query
                        | None ->
                            match typedQuery chain pairs with
                            | Error invalid -> invalid
                            | Ok typed ->
                                let candidate =
                                    { Route = destination
                                      Chain = levels
                                      Query = typed
                                      Requires = chain |> List.collect (fun (_, route) -> route.Requires) |> List.distinct
                                      RedirectedFrom = List.take (visited.Length - 1) visited }

                                let decision =
                                    chain
                                    |> List.tryPick (fun (name, route) ->
                                        route.Guard
                                        |> Option.bind (fun guardName ->
                                            match guard guardName candidate with
                                            | GuardDecision.Allow -> None
                                            | other -> Some(name, other)))

                                match decision with
                                | None -> Resolution.Matched candidate
                                | Some(name, GuardDecision.Deny) -> Resolution.Denied name
                                | Some(_, GuardDecision.Redirect(target, parameters, guardQuery)) ->
                                    match build table target parameters guardQuery with
                                    | Error _ -> Resolution.Invalid(destination, target, "", "a buildable guard redirect target")
                                    | Ok location ->
                                        let path, query = splitLocation location
                                        resolveFrom table guard visited path query
                                | Some(_, GuardDecision.Allow) -> Resolution.Matched candidate

    /// Resolves a reported location. `guard` is the engine's decision for a
    /// named guard; it is interface policy, never an authorization boundary.
    let resolve (table: Route list) (guard: string -> Match -> GuardDecision) (path: string) (query: string) =
        if path.Length + query.Length > maxLength then Resolution.TooLong
        else resolveFrom table guard [] path query

    /// Resolves "path?query" as one string.
    let resolveLocation (table: Route list) guard (location: string) =
        let path, query = splitLocation location
        resolve table guard path query

    /// The canonical location of a match.
    let canonical (table: Route list) (matched: Match) =
        let parameters = matched.Chain |> List.collect (fun level -> Map.toList level.Params) |> Map.ofList
        build table matched.Route parameters matched.Query

    /// Allows every guard: for checks that must not depend on who is signed in.
    let allowAll : string -> Match -> GuardDecision = fun _ _ -> GuardDecision.Allow

module RouteTable =
    let private reserved =
        set [ "token"; "accesstoken"; "idtoken"; "refreshtoken"; "password"; "passwd"; "secret"; "clientsecret"; "apikey"; "key"; "session"
              "sessionid"; "auth"; "authorization"; "code"; "credential"; "credentials" ]

    /// Whether a parameter name is reserved for credentials (LCP-109).
    let isReserved (name: string) =
        reserved.Contains(name.ToLowerInvariant().Replace("-", "").Replace("_", ""))

    let private full (prefix: string) (name: string) = if prefix = "" then name else $"{prefix}.{name}"

    let private pathParams (route: Route) =
        route.Path
        |> List.choose (function
            | Segment.Param(name, kind) -> Some(name, Some kind)
            | Segment.Wildcard name -> Some(name, None)
            | Segment.Literal _ -> None)

    let private valuesProblem kind =
        match kind with
        | ParamType.Enum values -> values.IsEmpty || values |> List.exists ((=) "") || List.distinct values <> values
        | ParamType.Set values -> values |> List.exists ((=) "") || List.distinct values <> values
        | _ -> false

    let rec private destinationsOf (prefix: string) (routes: Route list) =
        routes
        |> List.collect (fun route ->
            let name = full prefix route.Name
            if route.Children.IsEmpty then [ name, route ] else destinationsOf name route.Children)

    let rec private check (table: Route list) (prefix: string) (inherited: string list) (routes: Route list) : DefinitionError list =
        let duplicates =
            routes
            |> List.countBy (fun route -> route.Name)
            |> List.filter (fun (_, count) -> count > 1)
            |> List.map (fun (name, _) -> DefinitionError.DuplicateName(full prefix name))

        let perRoute (route: Route) =
            let name = full prefix route.Name
            let last = route.Path.Length - 1

            let segmentErrors =
                route.Path
                |> List.indexed
                |> List.choose (fun (index, segment) ->
                    match segment with
                    | Segment.Wildcard wildcard when index <> last || not route.Children.IsEmpty -> Some(DefinitionError.InvalidSegment(name, $"{{*{wildcard}}}"))
                    | _ -> None)
            let local = (pathParams route |> List.map fst) @ (route.Query |> List.map (fun parameter -> parameter.Name))

            let duplicateParams =
                local
                |> List.indexed
                |> List.choose (fun (index, parameter) ->
                    if List.contains parameter inherited || List.contains parameter (List.take index local) then Some(DefinitionError.DuplicateParameter(name, parameter)) else None)

            let reservedNames = local |> List.filter isReserved |> List.map (fun parameter -> DefinitionError.ReservedName(name, parameter))

            let pathTypes =
                pathParams route
                |> List.choose (fun (parameter, kind) ->
                    match kind with
                    | Some(ParamType.Bool | ParamType.Set _) -> Some(DefinitionError.InvalidValues(name, parameter))
                    | Some kind when valuesProblem kind -> Some(DefinitionError.InvalidValues(name, parameter))
                    | _ -> None)

            let queryErrors =
                route.Query
                |> List.collect (fun parameter ->
                    [ if valuesProblem parameter.Type then DefinitionError.InvalidValues(name, parameter.Name)
                      match parameter.Default with
                      | Some _ when parameter.Required -> DefinitionError.RequiredWithDefault(name, parameter.Name)
                      | Some value when (Values.render parameter.Type value).IsNone -> DefinitionError.InvalidDefault(name, parameter.Name)
                      | _ -> () ])

            let redirectErrors =
                match route.Redirect with
                | None -> []
                | Some(target, templates) ->
                    let sources = pathParams route |> List.map fst
                    [ if (Router.chainOf table target).IsNone then DefinitionError.UnknownTarget(name, target)
                      for (_, template) in templates do
                          match template with
                          | Template.FromParam source when not (List.contains source sources) -> DefinitionError.UnknownParameter(name, source)
                          | _ -> () ]

            segmentErrors @ duplicateParams @ reservedNames @ pathTypes @ queryErrors @ redirectErrors @ check table name (inherited @ local) route.Children

        duplicates @ (routes |> List.collect perRoute)

    let private legacyRoute (index: int) (legacy: LegacyRoute) =
        let name = $"legacy-{index + 1}"
        Route.define name legacy.Path
        |> Result.map (fun route -> { route with Redirect = Some(legacy.To, legacy.Params); ReturnTarget = false })

    /// Legacy entries are matched after every current route and before the
    /// first top-level wildcard, so a current route always wins.
    let private withLegacy (routes: Route list) (legacy: Route list) =
        let isWildcard (route: Route) = match route.Path with Segment.Wildcard _ :: _ -> true | _ -> false
        let before = routes |> List.takeWhile (isWildcard >> not)
        let after = routes |> List.skipWhile (isWildcard >> not)
        before @ legacy @ after

    /// A validated table, or every problem found (LCP-090).
    let define (routes: Route list) (legacy: LegacyRoute list) (roles: Roles) : Result<RouteTable, DefinitionError list> =
        let legacyRoutes = legacy |> List.mapi legacyRoute
        let legacyErrors = legacyRoutes |> List.choose (function Error e -> Some e | Ok _ -> None)
        let parsed = legacyRoutes |> List.choose (function Ok r -> Some r | Error _ -> None)
        let matching = withLegacy routes parsed
        let isDestination name = (Router.chainOf routes name |> Option.map (fun chain -> (chain |> List.last |> snd).Redirect.IsNone)) = Some true

        let roleErrors =
            [ if not (isDestination roles.Home) then DefinitionError.UnknownRole("home", roles.Home)
              match roles.SignIn with
              | Some signIn when not (isDestination signIn) -> DefinitionError.UnknownRole("signIn", signIn)
              | _ -> ()
              match roles.NotFound with
              | Some notFound when not (isDestination notFound) -> DefinitionError.UnknownRole("notFound", notFound)
              | _ -> () ]

        let errors = check matching "" [] matching @ legacyErrors @ roleErrors
        if errors.IsEmpty then Ok { routes = routes; legacy = legacy; roles = roles; matching = matching } else Error errors

    /// The routes as matched: the table's own, with the legacy entries.
    let routes (table: RouteTable) = table.matching
    let declared (table: RouteTable) = table.routes
    let legacy (table: RouteTable) = table.legacy
    let roles (table: RouteTable) = table.roles

    /// Every destination's full name with its chain, in table order.
    let destinations (table: RouteTable) = destinationsOf "" table.matching

/// What an application route maps to: a destination and its typed values.
type Target =
    { Route: string
      Params: Map<string, Value>
      Query: Map<string, Value> }

/// Why a location is not one of the application's routes (LCP-098). Every
/// case has its own view; none is a blank page or another route's view.
[<RequireQualifiedAccess>]
type RouteError =
    | NotFound
    | NotPermitted of route: string
    | Invalid of route: string * parameter: string * value: string * expected: string
    /// "path", "query" or "length".
    | Malformed of part: string
    | RedirectLoop of chain: string list
    /// The application's mapping refused a match.
    | Unmapped of route: string * problem: string

module RouteError =
    /// A resolution as a match or a route error. A match of the table's
    /// not-found route is NotFound.
    let ofResolution (table: RouteTable) (resolution: Resolution) : Result<Match, RouteError> =
        match resolution with
        | Resolution.Matched matched when Some matched.Route = (RouteTable.roles table).NotFound -> Error RouteError.NotFound
        | Resolution.Matched matched -> Ok matched
        | Resolution.NotFound -> Error RouteError.NotFound
        | Resolution.MalformedPath -> Error(RouteError.Malformed "path")
        | Resolution.MalformedQuery -> Error(RouteError.Malformed "query")
        | Resolution.TooLong -> Error(RouteError.Malformed "length")
        | Resolution.Invalid(route, parameter, value, expected) -> Error(RouteError.Invalid(route, parameter, value, expected))
        | Resolution.RedirectLoop chain -> Error(RouteError.RedirectLoop chain)
        | Resolution.Denied route -> Error(RouteError.NotPermitted route)

/// The engine's navigation state: the location it last adopted or pushed.
type RouterState = { Current: string option }

module Navigation =
    let initial = { Current = None }

    /// A location the browser reported (a deep link in Initialize, or Back and
    /// Forward in LocationChanged). Never answered with a push: at most a
    /// replace that corrects the entry to its canonical form.
    let adopt (table: Route list) guard (state: RouterState) (location: string) =
        let resolution = Router.resolveLocation table guard location

        match resolution with
        | Resolution.Matched matched ->
            match Router.canonical table matched with
            | Ok canonical when canonical <> location -> { Current = Some canonical }, resolution, Some(NavigationEffect.Replace canonical)
            | Ok canonical -> { Current = Some canonical }, resolution, None
            | Error _ -> { state with Current = Some location }, resolution, None
        | _ -> { Current = Some location }, resolution, None

    /// An in-app navigation to another place: push the built location unless
    /// it is already current (LCP-096).
    let navigate (table: Route list) (state: RouterState) route parameters query =
        Router.build table route parameters query
        |> Result.map (fun location ->
            if state.Current = Some location then state, None
            else { Current = Some location }, Some(NavigationEffect.Push location))

    /// Replace the current entry with a location the engine decided on, such
    /// as ReturnTo.resume after sign-in; nothing when it is already current.
    let replace (state: RouterState) (location: string) =
        if state.Current = Some location then state, None
        else { Current = Some location }, Some(NavigationEffect.Replace location)

    /// An in-place refinement of the current view (a filter, sort, page, tab
    /// or date): replace, so Back steps to the previous place (LCP-096).
    let refine (table: Route list) (state: RouterState) route parameters query =
        Router.build table route parameters query
        |> Result.map (fun location ->
            if state.Current = Some location then state, None
            else { Current = Some location }, Some(NavigationEffect.Replace location))

/// A typed codec: the table mapped onto the application's own route type.
[<NoEquality; NoComparison>]
type RouteCodec<'Route> =
    { Table: RouteTable
      ToTarget: 'Route -> Target
      OfMatch: Match -> Result<'Route, string> }

module RouteCodec =
    let create table (toTarget: 'Route -> Target) (ofMatch: Match -> Result<'Route, string>) =
        { Table = table; ToTarget = toTarget; OfMatch = ofMatch }

    let private typed (codec: RouteCodec<'Route>) resolution =
        RouteError.ofResolution codec.Table resolution
        |> Result.bind (fun matched -> codec.OfMatch matched |> Result.mapError (fun problem -> RouteError.Unmapped(matched.Route, problem)))

    /// A routed location ("/path?query") → the application's route.
    let parse (codec: RouteCodec<'Route>) guard (location: string) : Result<'Route, RouteError> =
        Router.resolveLocation (RouteTable.routes codec.Table) guard location |> typed codec

    /// The application's route → its canonical location.
    let format (codec: RouteCodec<'Route>) (route: 'Route) : Result<string, BuildError> =
        let target = codec.ToTarget route
        Router.build (RouteTable.routes codec.Table) target.Route target.Params target.Query

    /// Navigation.adopt, typed.
    let adopt (codec: RouteCodec<'Route>) guard (state: RouterState) (location: string) =
        let next, resolution, effect = Navigation.adopt (RouteTable.routes codec.Table) guard state location
        next, typed codec resolution, effect

    let private move operation (codec: RouteCodec<'Route>) (state: RouterState) (route: 'Route) =
        let target = codec.ToTarget route
        operation (RouteTable.routes codec.Table) state target.Route target.Params target.Query

    /// Navigation.navigate, typed: a push.
    let navigate codec state route = move Navigation.navigate codec state route

    /// Navigation.refine, typed: a replace.
    let refine codec state route = move Navigation.refine codec state route

/// Where the routed location lives in the browser's URL (LCP-102).
[<RequireQualifiedAccess>]
type LocationMode =
    /// The fragment: "#/invoices/42?tab=history". Static hosts need nothing.
    | Hash
    /// The path and query: for hosts that serve the application at every path.
    | Path

/// The location the kernel reports (Initialize.location, LocationChanged).
type PageLocation =
    { Origin: string
      Path: string
      Query: string
      Hash: string }

module Location =
    /// The routed location ("/path?query") of the page's URL.
    let ofBrowser (mode: LocationMode) (page: PageLocation) =
        match mode with
        | LocationMode.Hash ->
            let body = if page.Hash.StartsWith "#" then page.Hash.Substring 1 else page.Hash
            if body = "" then "/" elif body.StartsWith "/" then body else "/" + body
        | LocationMode.Path -> (if page.Path = "" then "/" else page.Path) + page.Query

    /// The relative URL to request with Navigation, or to render as a link's
    /// href: "#/x" in hash mode, "/x" in path mode.
    let href (mode: LocationMode) (location: string) =
        match mode with
        | LocationMode.Hash -> "#" + location
        | LocationMode.Path -> location

module Link =
    /// The absolute URL of a canonical location, for "copy link" (LCP-106).
    /// The engine writes it with the Core Clipboard effect.
    let share (mode: LocationMode) (page: PageLocation) (location: string) =
        match mode with
        | LocationMode.Hash -> page.Origin + page.Path + page.Query + "#" + location
        | LocationMode.Path -> page.Origin + location

module ReturnTo =
    /// The query parameter the sign-in route declares for the target.
    let parameter = "returnTo"

    /// A single-slash relative location with no backslash or control
    /// character: the only shape a return target may have (LCP-101).
    let isRelative (location: string) =
        location.Length > 0
        && location.Length <= Router.maxLength
        && location[0] = '/'
        && not (location.StartsWith "//")
        && not (location.Contains '\\')
        && location |> Seq.forall (fun c -> c >= ' ' && c <> '\u007f')

    let private eligible (table: RouteTable) (matched: Match) =
        let roles = RouteTable.roles table
        Some matched.Route <> roles.SignIn
        && Some matched.Route <> roles.NotFound
        && (Router.chainOf (RouteTable.routes table) matched.Route |> Option.map (fun chain -> (chain |> List.last |> snd).ReturnTarget)) = Some true

    /// The target to keep for a location that needs sign-in: its canonical
    /// form, or None when it may not be returned to. Guards are not consulted
    /// here; resume consults them after sign-in.
    let capture (table: RouteTable) (location: string) : string option =
        if not (isRelative location) then None
        else
            match Router.resolveLocation (RouteTable.routes table) Router.allowAll location with
            | Resolution.Matched matched when eligible table matched -> Router.canonical (RouteTable.routes table) matched |> Result.toOption
            | _ -> None

    /// The sign-in location carrying the target, or None without a sign-in route.
    let signIn (table: RouteTable) (target: string option) : Result<string, BuildError> =
        match (RouteTable.roles table).SignIn with
        | None -> Error BuildError.UnknownRoute
        | Some route ->
            let query = target |> Option.map (fun t -> Map [ parameter, Value.Text t ]) |> Option.defaultValue Map.empty
            Router.build (RouteTable.routes table) route Map.empty query

    let private home (table: RouteTable) =
        Router.build (RouteTable.routes table) (RouteTable.roles table).Home Map.empty Map.empty |> Result.defaultValue "/"

    /// Where to go after sign-in: the target's canonical location when it is
    /// still eligible and its guards allow it now; otherwise home. Replace,
    /// so Back does not return to the sign-in page.
    let resume (table: RouteTable) guard (target: string option) : string =
        match target |> Option.filter isRelative with
        | None -> home table
        | Some location ->
            match Router.resolveLocation (RouteTable.routes table) guard location with
            | Resolution.Matched matched when eligible table matched -> Router.canonical (RouteTable.routes table) matched |> Result.defaultValue (home table)
            | _ -> home table

/// The route inventory (LCP-107): echelon.routes/v1, for .echelon/routes.json.
module Inventory =
    type private Json =
        | JNull
        | JBool of bool
        | JNumber of int64
        | JString of string
        | JArray of Json list
        | JObject of (string * Json) list

    let private escape (text: string) =
        let builder = StringBuilder("\"")

        for c in text do
            match c with
            | '"' -> builder.Append "\\\"" |> ignore
            | '\\' -> builder.Append "\\\\" |> ignore
            | '\b' -> builder.Append "\\b" |> ignore
            | '\f' -> builder.Append "\\f" |> ignore
            | '\n' -> builder.Append "\\n" |> ignore
            | '\r' -> builder.Append "\\r" |> ignore
            | '\t' -> builder.Append "\\t" |> ignore
            | c when c < ' ' -> builder.Append($"\\u{int c:x4}") |> ignore
            | c -> builder.Append c |> ignore

        builder.Append('"').ToString()

    /// JSON.stringify(value, null, 2) with keys sorted by code unit.
    let rec private write (indent: string) (json: Json) =
        let inner = indent + "  "
        match json with
        | JNull -> "null"
        | JBool flag -> if flag then "true" else "false"
        | JNumber number -> number.ToString(CultureInfo.InvariantCulture)
        | JString text -> escape text
        | JArray [] -> "[]"
        | JArray items -> "[\n" + (items |> List.map (fun item -> inner + write inner item) |> String.concat ",\n") + "\n" + indent + "]"
        | JObject [] -> "{}"
        | JObject fields ->
            let sorted = fields |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
            "{\n" + (sorted |> List.map (fun (key, value) -> inner + escape key + ": " + write inner value) |> String.concat ",\n") + "\n" + indent + "}"

    let private optionalString = Option.map JString >> Option.defaultValue JNull

    let private valueJson (value: Value) =
        match value with
        | Value.Text text -> JString text
        | Value.Integer number -> JNumber number
        | Value.Boolean flag -> JBool flag
        | Value.Date date -> JString(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        | Value.Month(year, month) -> JString $"%04d{year}-%02d{month}"
        | Value.Members items -> JArray(items |> List.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> List.map JString)

    let private values kind =
        match kind with
        | ParamType.Enum items -> JArray(List.map JString items)
        | ParamType.Set [] -> JNull
        | ParamType.Set items -> JArray(List.map JString items)
        | _ -> JNull

    let private segmentText (segment: Segment) =
        match segment with
        | Segment.Literal literal -> literal
        | Segment.Param(name, kind) -> $"{{{name}:{Values.typeName kind}}}"
        | Segment.Wildcard name -> $"{{*{name}}}"

    let private pattern (segments: Segment list) = "/" + (segments |> List.map segmentText |> String.concat "/")

    let private parameter name place (kind: ParamType) required (fallback: Value option) =
        JObject
            [ "name", JString name
              "in", JString place
              "type", JString(Values.typeName kind)
              "required", JBool required
              "default", fallback |> Option.map valueJson |> Option.defaultValue JNull
              "values", values kind ]

    let private chainParams (chain: (string * Route) list) =
        let path =
            chain
            |> List.collect (fun (_, route) -> route.Path)
            |> List.choose (fun segment ->
                match segment with
                | Segment.Param(name, kind) -> Some(parameter name "path" kind true None)
                | Segment.Wildcard name -> Some(parameter name "path" ParamType.String false None)
                | Segment.Literal _ -> None)

        let query =
            chain
            |> List.collect (fun (_, route) -> route.Query)
            |> List.map (fun declared -> parameter declared.Name "query" declared.Type declared.Required declared.Default)

        path @ query

    let private templateJson (template: Template) =
        match template with
        | Template.FromParam source -> JString $"{{{source}}}"
        | Template.Literal literal -> JString literal

    /// The inventory document: deterministic, byte-identical in every
    /// conforming library (sorted keys, two-space indentation, final newline).
    let render (mode: LocationMode) (table: RouteTable) : string =
        let roles = RouteTable.roles table
        let all = RouteTable.destinations table
        let isRedirect (_, route: Route) = route.Redirect.IsSome

        let routeJson (name: string, _) =
            let chain = Router.chainOf (RouteTable.routes table) name |> Option.defaultValue []
            let destination = chain |> List.last |> snd

            JObject
                [ "name", JString name
                  "pattern", JString(pattern (chain |> List.collect (fun (_, route) -> route.Path)))
                  "params", JArray(chainParams chain)
                  "guards", JArray(chain |> List.choose (fun (_, route) -> route.Guard) |> List.map JString)
                  "requires", JArray(chain |> List.collect (fun (_, route) -> route.Requires) |> List.distinct |> List.map JString)
                  "returnTarget", JBool(destination.ReturnTarget && Some name <> roles.SignIn && Some name <> roles.NotFound) ]

        let legacyJson (name: string, route: Route) =
            let chain = Router.chainOf (RouteTable.routes table) name |> Option.defaultValue []
            let target, templates = route.Redirect |> Option.defaultValue ("", [])

            JObject
                [ "name", JString name
                  "pattern", JString(pattern (chain |> List.collect (fun (_, r) -> r.Path)))
                  "to", JString target
                  "params", JObject(templates |> List.map (fun (key, template) -> key, templateJson template)) ]

        let document =
            JObject
                [ "schema", JString "echelon.routes/v1"
                  "mode", JString(match mode with LocationMode.Hash -> "hash" | LocationMode.Path -> "path")
                  "home", JString roles.Home
                  "signIn", optionalString roles.SignIn
                  "notFound", optionalString roles.NotFound
                  "routes", JArray(all |> List.filter (isRedirect >> not) |> List.map routeJson)
                  "legacy", JArray(all |> List.filter isRedirect |> List.map legacyJson) ]

        write "" document + "\n"
