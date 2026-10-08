/// Routes are the URL fragment, so every screen has an address that survives
/// refresh, Back and Forward on a static host: `#/today`,
/// `#/today/2026-10-08`, `#/track`, `#/month/2026-10`, `#/more`,
/// `#/activity/<id>`, `#/review/2026-10-08`. Pure.
module Chrona.Engine.App.Routes

open System
open Chrona.Engine.App.Model

let private screenName =
    function
    | Today -> "today"
    | Track -> "track"
    | Month -> "month"
    | More -> "more"
    | ActivityDetail _ -> "activity"
    | DayReview -> "review"

/// The screens a navigation item names (not those that need an argument).
let ofScreenName =
    function
    | "today" -> Some Today
    | "track" -> Some Track
    | "month" -> Some Month
    | "more" -> Some More
    | "review" -> Some DayReview
    | _ -> None

let private parseMonth (text: string) =
    Format.parseIsoDate $"{text}-01"

/// The route a fragment names. Anything unrecognised is Today: an old or
/// mistyped link lands somewhere useful rather than nowhere.
let parse (hash: string) : Route =
    let parts = hash.TrimStart('#').Trim('/').Split('/') |> Array.toList

    match parts with
    | [ "today"; date ] -> { Screen = Today; Date = Format.parseIsoDate date }
    | [ "review"; date ] -> { Screen = DayReview; Date = Format.parseIsoDate date }
    | [ "month"; month ] -> { Screen = Month; Date = parseMonth month }
    | [ "activity"; id ] when not (String.IsNullOrWhiteSpace id) -> { Screen = ActivityDetail(Uri.UnescapeDataString id); Date = None }
    | [ name ] ->
        { Screen = ofScreenName name |> Option.defaultValue Today
          Date = None }
    | _ -> { Screen = Today; Date = None }

let hash (route: Route) =
    match route with
    | { Screen = Today; Date = Some date } -> $"#/today/{Format.isoDate date}"
    | { Screen = DayReview; Date = Some date } -> $"#/review/{Format.isoDate date}"
    | { Screen = Month; Date = Some date } -> $"#/month/{date.Year:D4}-{date.Month:D2}"
    | { Screen = ActivityDetail id } -> $"#/activity/{Uri.EscapeDataString id}"
    | { Screen = screen } -> $"#/{screenName screen}"
