/// Routes are the URL fragment, so every screen has an address that survives
/// refresh, Back and Forward on a static host: `#/today`,
/// `#/today/2026-10-08`, `#/track`, `#/more`. Pure.
module Chrona.Engine.App.Routes

open Chrona.Engine.App.Model

let private screenName =
    function
    | Today -> "today"
    | Track -> "track"
    | More -> "more"

let ofScreenName =
    function
    | "today" -> Some Today
    | "track" -> Some Track
    | "more" -> Some More
    | _ -> None

/// The route a fragment names. Anything unrecognised is Today: an old or
/// mistyped link lands somewhere useful rather than nowhere.
let parse (hash: string) : Route =
    let parts = hash.TrimStart('#').Trim('/').Split('/') |> Array.toList

    match parts with
    | [ "today"; date ] -> { Screen = Today; Date = Format.parseIsoDate date }
    | [ name ] ->
        { Screen = ofScreenName name |> Option.defaultValue Today
          Date = None }
    | _ -> { Screen = Today; Date = None }

let hash (route: Route) =
    match route with
    | { Screen = Today; Date = Some date } -> $"#/today/{Format.isoDate date}"
    | { Screen = screen } -> $"#/{screenName screen}"
