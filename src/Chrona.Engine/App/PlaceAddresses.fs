/// A place's address (CHX-460, WI-0071): Chrona's typed codec over its
/// route table (`Places`), the guards, return targets through sign-in,
/// shared links and the `echelon.routes/v1` inventory. Pure.
module Chrona.Engine.App.PlaceAddresses

open System
open Limen.Routing
open Chrona.Engine.App.Places

let private target route parameters query =
    { Route = route
      Params = Map.ofList parameters
      Query = query |> List.choose (fun (key, value) -> value |> Option.map (fun v -> key, v)) |> Map.ofList }

let private text = Option.map Value.Text
let private date (on: DateOnly) = Value.Date on

/// A place's route and typed values.
let private placeTarget (place: Place) : Target =
    match place with
    | Today -> target Names.Today [] []
    | Day(on, project) -> target Names.Day [ "on", date on ] [ "project", text project ]
    | ThisWeek -> target Names.ThisWeek [] []
    | Week(on, project) -> target Names.Week [ "on", date on ] [ "project", text project ]
    | ThisMonth -> target Names.ThisMonth [] []
    | Month(year, month) -> target Names.Month [ "period", Value.Month(year, month) ] []
    | ThisPeriod -> target Names.ThisPeriod [] []
    | Period on -> target Names.Period [ "on", date on ] []
    | Projects -> target Names.Projects [] []
    | Project id -> target Names.Project [ "id", Value.Text id ] []
    | Track -> target Names.Track [] []
    | Entry(id, on) -> target Names.Entry [ "id", Value.Text id ] [ "on", on |> Option.map date ]
    | ReviewToday -> target Names.ReviewToday [] []
    | Review on -> target Names.Review [ "on", date on ] []
    | Reports query ->
        target
            Names.Reports
            []
            [ "from", query.From |> Option.map date
              "to", query.To |> Option.map date
              "project", text query.ProjectId
              "type", text query.ActivityTypeId
              "tag", text query.Tag
              "method", text query.Method
              "billing", text query.Billability
              "q", text query.Text
              "removed", Some(Value.Boolean query.IncludeRemoved)
              "people", Some(Value.Text query.People)
              "person", text query.Person
              "organizations", Some(Value.Text query.Organizations)
              "group", groupings |> List.tryFind (fst >> (=) query.Grouping) |> Option.map (snd >> Value.Text)
              "format", Some(Value.Text query.Format) ]
    | Candidates query -> target Names.Candidates [] [ "status", Some(Value.Text query.Status); "source", text query.Source ]
    | Candidate id -> target Names.Candidate [ "id", Value.Text id ] []
    | Settings None -> target Names.Settings [] []
    | Settings(Some People) -> target Names.People [] []
    | Settings(Some ActivityIndex) -> target Names.ActivityIndex [] []
    | Settings(Some section) -> target Names.Section [ "section", Value.Text(sectionName section) ] []
    | SignIn returnTo -> target Names.SignIn [] [ "returnTo", text returnTo ]

let private parameters (matched: Match) =
    matched.Chain |> List.collect (fun level -> Map.toList level.Params) |> Map.ofList

let private textOf (values: Map<string, Value>) key =
    match values.TryFind key with
    | Some(Value.Text value) -> Some value
    | _ -> None

let private dateOf (values: Map<string, Value>) key =
    match values.TryFind key with
    | Some(Value.Date value) -> Some value
    | _ -> None

let private required what (value: 'a option) =
    value |> Option.map Ok |> Option.defaultValue (Error $"{what} is missing")

/// An address's route and typed values: its place's, and its organization.
let toTarget (address: Address) : Target =
    let target = placeTarget address.Place

    match address.Organization, address.Place with
    // Sign-in is in no organization; its return target holds one.
    | _, SignIn _
    | None, _ -> target
    | Some organization, _ -> { target with Query = target.Query.Add("org", Value.Text organization) }

/// A resolved route as a place. Every route of the table maps; a match this
/// cannot map is a defect, reported as Limen's `Unmapped`. The not-found
/// route never reaches here: the codec reports it as `RouteError.NotFound`.
let private placeOf (matched: Match) : Result<Place, string> =
    let path = parameters matched
    let query = matched.Query

    match matched.Route with
    | Names.Today -> Ok Today
    | Names.Day -> dateOf path "on" |> required "on" |> Result.map (fun on -> Day(on, textOf query "project"))
    | Names.ThisWeek -> Ok ThisWeek
    | Names.Week -> dateOf path "on" |> required "on" |> Result.map (fun on -> Week(on, textOf query "project"))
    | Names.ThisPeriod -> Ok ThisPeriod
    | Names.Period -> dateOf path "on" |> required "on" |> Result.map Period
    | Names.Projects -> Ok Projects
    | Names.Project -> textOf path "id" |> required "id" |> Result.map Project
    | Names.ThisMonth -> Ok ThisMonth
    | Names.Month ->
        match path.TryFind "period" with
        | Some(Value.Month(year, month)) -> Ok(Month(year, month))
        | _ -> Error "period is missing"
    | Names.Track -> Ok Track
    | Names.Entry -> textOf path "id" |> required "id" |> Result.map (fun id -> Entry(id, dateOf query "on"))
    | Names.ReviewToday -> Ok ReviewToday
    | Names.Review -> dateOf path "on" |> required "on" |> Result.map Review
    | Names.Reports ->
        Ok(
            Reports
                { From = dateOf query "from"
                  To = dateOf query "to"
                  ProjectId = textOf query "project"
                  ActivityTypeId = textOf query "type"
                  Tag = textOf query "tag"
                  Method = textOf query "method"
                  Billability = textOf query "billing"
                  Text = textOf query "q"
                  IncludeRemoved = (query.TryFind "removed" = Some(Value.Boolean true))
                  People = textOf query "people" |> Option.defaultValue allReports.People
                  Person = textOf query "person"
                  Organizations = textOf query "organizations" |> Option.defaultValue allReports.Organizations
                  Grouping =
                    textOf query "group"
                    |> Option.bind (fun name -> groupings |> List.tryFind (snd >> (=) name))
                    |> Option.map fst
                    |> Option.defaultValue allReports.Grouping
                  Format = textOf query "format" |> Option.defaultValue allReports.Format }
        )
    | Names.Candidates ->
        Ok(
            Candidates
                { Status = textOf query "status" |> Option.defaultValue awaitingCandidates.Status
                  Source = textOf query "source" }
        )
    | Names.Candidate -> textOf path "id" |> required "id" |> Result.map Candidate
    | Names.Settings -> Ok(Settings None)
    | Names.People -> Ok(Settings(Some People))
    | Names.ActivityIndex -> Ok(Settings(Some ActivityIndex))
    | Names.Section -> textOf path "section" |> Option.bind sectionOf |> required "section" |> Result.map (Some >> Settings)
    | Names.SignIn -> Ok(SignIn(textOf query "returnTo"))
    // The not-found route is Limen's NotFound outcome, never a place.
    | other -> Error $"no place for the route {other}"

/// A resolved route as an address: its place, and its organization.
let ofMatch (matched: Match) : Result<Address, string> =
    placeOf matched
    |> Result.map (fun place ->
        { Place = place
          Organization = textOf matched.Query "org" })

/// Chrona's typed codec: an address to its canonical location and back.
let codec = RouteCodec.create table toTarget ofMatch

/// An address's canonical location: "/day/2026-10-08?project=helix".
let format (address: Address) = RouteCodec.format codec address

/// A location as an address, under the engine's guard decisions.
let parse guard (location: string) = RouteCodec.parse codec guard location

/// The `href` an in-page link to an address carries: relative, in the
/// fragment ("#/day/2026-10-08").
let href (address: Address) =
    format address |> Result.map (Location.href mode)

/// The relative address a sign-in returns to for this address: its canonical
/// form, or None when it may not be returned to (the sign-in or not-found
/// page, or anything that is not one of Chrona's places).
let captureReturn (location: string) = ReturnTo.capture table location

/// Where to go once someone may work: the kept return target when it is
/// still a place they may see now, otherwise home.
let resume guard (returnTo: string option) = ReturnTo.resume table guard returnTo

/// What the guards need to know about the person, from the engine's state.
type Standing =
    { /// Sign-in is configured and no one is signed in (and none is under way).
      SignedOut: bool
      /// A return target already kept (from before a sign-in round trip): a
      /// sign-in started again returns there rather than to where the
      /// callback landed.
      KeptReturn: string option
      /// Whether the person administers the organization; None until their
      /// access is known (the records are still opening), when nothing is
      /// refused yet.
      Administrator: bool option }

/// The engine's guard decisions (LCP-099): a signed-out person is sent to
/// sign-in with the address to return to, and a part of More only an
/// administrator works in is refused to anyone else. Interface policy only:
/// GitHub refuses what the person's token may not do, whatever is shown.
let guard (standing: Standing) (name: string) (candidate: Match) : GuardDecision =
    match name with
    | Guards.SignedIn when standing.SignedOut ->
        let target =
            standing.KeptReturn
            |> Option.orElse (Router.canonical (RouteTable.routes table) candidate |> Result.toOption |> Option.bind captureReturn)

        GuardDecision.Redirect(
            Names.SignIn,
            Map.empty,
            target |> Option.map (fun t -> Map [ ReturnTo.parameter, Value.Text t ]) |> Option.defaultValue Map.empty
        )
    | Guards.Administrator when standing.Administrator = Some false -> GuardDecision.Deny
    | _ -> GuardDecision.Allow

/// The absolute link that opens a place, for "Copy link". Only the page's
/// origin and path are kept: the page's own query (where a sign-in callback
/// once carried its code and state) never goes into a link.
let share (origin: string) (path: string) (location: string) =
    Link.share mode { Origin = origin; Path = path; Query = ""; Hash = "" } location

/// The routed address of the page's fragment: "#/day/2026-10-08" is
/// "/day/2026-10-08", and no fragment is home ("/").
let ofFragment (fragment: string) =
    Location.ofBrowser mode { Origin = ""; Path = ""; Query = ""; Hash = fragment }

/// The reports screen's range when its address names none: the month that
/// contains today.
let defaultRange (today: DateOnly) =
    let first = DateOnly(today.Year, today.Month, 1)
    first, first.AddMonths(1).AddDays -1

/// The place a relative place means today, made explicit for a link that
/// must open the same view on another day: today's ledger is that day's
/// ledger, this week, month or period that one, today's review that day's
/// review, and a report over "this month" that month's dates. A week or
/// period is named by today here; the engine names it by its first day.
let explicit (today: DateOnly) (place: Place) =
    match place with
    | Today -> Day(today, None)
    | ThisWeek -> Week(today, None)
    | ThisPeriod -> Period today
    | ThisMonth -> Month(today.Year, today.Month)
    | ReviewToday -> Review today
    | Reports query ->
        let first, last = defaultRange today
        Reports { query with From = Some(defaultArg query.From first); To = Some(defaultArg query.To last) }
    | other -> other

/// The route inventory (`.echelon/routes.json`, schema `echelon.routes/v1`).
let inventory () = Inventory.render mode table
