/// Where a person is in Chrona, as a value, and its one canonical address
/// (CHX-460, WI-0071, DF-CHRONA-2026-0007). Every navigable state lives in
/// the URL's fragment, so a copied link opens the same view, Back and Forward
/// retrace it, and a static host (GitHub Pages) serves every address.
///
/// The rules are Limen's URL-state semantics (`Limen.Routing`): typed
/// parameters, one canonical form (declared parameters only, in declaration
/// order, defaults omitted, sets sorted and de-duplicated, `%20` with
/// uppercase hex), credential-like parameter names refused, return targets
/// through sign-in, and the `echelon.routes/v1` inventory. This module is
/// Chrona's table and its typed codec over them. Pure.
module Chrona.Engine.App.Places

open System
open Limen.Routing

/// A part of the More screen with its own address.
type Section =
    | References
    | PeriodSettings
    | Changes
    | Account
    | People
    | ActivityIndex
    /// What reading producers' inboxes found (WI-0038).
    | Inboxes

/// The candidates screen's filters (WI-0038): which candidates, and from
/// which source. "awaiting" (the default) is what a person must still
/// decide; "decided" what was decided in the months read; "all" both.
type CandidateQuery =
    { Status: string
      Source: string option }

let awaitingCandidates = { Status = "awaiting"; Source = None }

/// The reports screen's filters, as they appear in its address. An absent
/// value is the screen's own default: the current month, anything, every
/// method, any billability.
type ReportQuery =
    { From: DateOnly option
      To: DateOnly option
      ProjectId: string option
      ActivityTypeId: string option
      Tag: string option
      /// "manual" or "timer".
      Method: string option
      /// "billable", "non-billable" or "pending".
      Billability: string option
      Text: string option
      IncludeRemoved: bool
      /// "project", "activityType", "tag" or "day".
      Grouping: string
      /// "csv" or "json".
      Format: string }

let allReports =
    { From = None
      To = None
      ProjectId = None
      ActivityTypeId = None
      Tag = None
      Method = None
      Billability = None
      Text = None
      IncludeRemoved = false
      Grouping = "project"
      Format = "csv" }

/// A navigable place in Chrona: what one address names.
type Place =
    /// Today's ledger, whatever day it is (the home address).
    | Today
    /// One day's ledger, optionally only one project's time.
    | Day of on: DateOnly * project: string option
    /// The week that contains today.
    | ThisWeek
    /// The week that contains `on`, by the organization's first day of the
    /// week, optionally only one project's time. Its canonical `on` is the
    /// week's first day.
    | Week of on: DateOnly * project: string option
    /// The month that contains today.
    | ThisMonth
    | Month of year: int * month: int
    /// The timesheet period that contains today.
    | ThisPeriod
    /// The timesheet period that contains `on`, by the organization's
    /// cadence. Its canonical `on` is the period's first day.
    | Period of on: DateOnly
    /// The organization's projects.
    | Projects
    /// One project, and the person's time on it.
    | Project of id: string
    | Track
    /// One activity. `on` is the day it was recorded on: the record is read
    /// from that month alone. Without it, the person's months are read until
    /// it is found.
    | Entry of id: string * on: DateOnly option
    /// Today's review.
    | ReviewToday
    | Review of on: DateOnly
    | Reports of ReportQuery
    /// Observations' candidates (WI-0038).
    | Candidates of CandidateQuery
    /// One candidate: what was observed, and deciding it.
    | Candidate of id: string
    /// The More screen, or one of its parts.
    | Settings of Section option
    /// Sign-in, with the relative address to return to afterwards.
    | SignIn of returnTo: string option

/// A place in one of the deployment's organizations: what an address names.
/// The organization is named only where a deployment serves several, so a
/// link opens the same organization's view; with one, it is omitted.
type Address =
    { Place: Place
      Organization: string option }

/// A place, in no organization in particular (a deployment of one).
let at (place: Place) = { Place = place; Organization = None }

// ---- the route table --------------------------------------------------------------

/// Route names, as the inventory lists them.
module Names =
    [<Literal>]
    let Today = "today"

    [<Literal>]
    let Day = "day"

    [<Literal>]
    let ThisWeek = "thisWeek"

    [<Literal>]
    let Week = "week"

    [<Literal>]
    let ThisMonth = "thisMonth"

    [<Literal>]
    let Month = "month"

    [<Literal>]
    let ThisPeriod = "thisPeriod"

    [<Literal>]
    let Period = "period"

    [<Literal>]
    let Projects = "projects"

    [<Literal>]
    let Project = "project"

    [<Literal>]
    let Track = "track"

    [<Literal>]
    let Entry = "entry"

    [<Literal>]
    let ReviewToday = "reviewToday"

    [<Literal>]
    let Review = "review"

    [<Literal>]
    let Reports = "reports"

    [<Literal>]
    let Candidates = "candidates"

    [<Literal>]
    let Candidate = "candidate"

    /// The More screen: every part of it (`settings.all`), the parts only an
    /// administrator works in (`settings.people`, `settings.index`), and the
    /// others (`settings.section`).
    [<Literal>]
    let Settings = "settings.all"

    [<Literal>]
    let People = "settings.people"

    [<Literal>]
    let ActivityIndex = "settings.index"

    [<Literal>]
    let Section = "settings.section"

    [<Literal>]
    let SignIn = "signIn"

    [<Literal>]
    let NotFound = "notFound"

/// Guards: engine decisions about what the interface shows, never security.
/// GitHub refuses what a person's token may not do whatever the page shows
/// (LCP-099).
module Guards =
    /// Someone may work here: signed in, or a deployment without sign-in.
    [<Literal>]
    let SignedIn = "signedIn"

    /// The organization's administrators only.
    [<Literal>]
    let Administrator = "administrator"

/// What a guarded route needs from the engine's state.
module Requires =
    /// An activity the person may see, by its id.
    [<Literal>]
    let Activity = "activity"

    /// A project of the organization, by its id.
    [<Literal>]
    let Project = "project"

    /// An observation's candidate the person may see, by its id.
    [<Literal>]
    let Candidate = "candidate"

let private sectionNames =
    [ References, "references"
      PeriodSettings, "periods"
      Changes, "changes"
      Account, "account"
      People, "people"
      ActivityIndex, "index"
      Inboxes, "inbox" ]

let sectionName (section: Section) = sectionNames |> List.find (fst >> (=) section) |> snd

let private sectionOf (name: string) = sectionNames |> List.tryFind (snd >> (=) name) |> Option.map fst

/// The sections only an administrator works in.
let administrative (section: Section) =
    match section with
    | People
    | ActivityIndex -> true
    | References
    | PeriodSettings
    | Changes
    | Account
    | Inboxes -> false

/// The parts anyone who works here may open.
let private openSections = sectionNames |> List.map fst |> List.filter (administrative >> not)

let private groupings = [ "project", "project"; "activityType", "type"; "tag", "tag"; "day", "day" ]

let private optional name kind = QueryParam.optional name kind

/// The candidate statuses an address may name.
let candidateStatuses = [ "awaiting"; "decided"; "all" ]

/// A place people work in: behind sign-in, and in an organization, named
/// last among its parameters.
let private guarded (route: Route) =
    { route with
        Guard = Some Guards.SignedIn
        Query = route.Query @ [ optional "org" ParamType.String ] }

let private routes: Route list =
    [ guarded (Route.create Names.Today "")
      guarded { Route.create Names.Day "day/{on:date}" with Query = [ optional "project" ParamType.String ] }
      guarded (Route.create Names.ThisWeek "week")
      guarded { Route.create Names.Week "week/{on:date}" with Query = [ optional "project" ParamType.String ] }
      guarded (Route.create Names.ThisMonth "month")
      guarded (Route.create Names.Month "month/{period:month}")
      guarded (Route.create Names.ThisPeriod "periods")
      guarded (Route.create Names.Period "periods/{on:date}")
      guarded (Route.create Names.Projects "projects")
      guarded { Route.create Names.Project "projects/{id}" with Requires = [ Requires.Project ] }
      guarded (Route.create Names.Track "track")
      guarded
          { Route.create Names.Entry "entries/{id}" with
              Query = [ optional "on" ParamType.Date ]
              Requires = [ Requires.Activity ] }
      guarded (Route.create Names.ReviewToday "review")
      guarded (Route.create Names.Review "review/{on:date}")
      guarded
          { Route.create Names.Reports "reports" with
              Query =
                  [ optional "from" ParamType.Date
                    optional "to" ParamType.Date
                    optional "project" ParamType.String
                    optional "type" ParamType.String
                    optional "tag" ParamType.String
                    optional "method" (ParamType.Enum [ "manual"; "timer" ])
                    optional "billing" (ParamType.Enum [ "billable"; "non-billable"; "pending" ])
                    optional "q" ParamType.String
                    optional "removed" ParamType.Bool |> QueryParam.withDefault (Value.Boolean false)
                    optional "group" (ParamType.Enum(groupings |> List.map snd)) |> QueryParam.withDefault (Value.Text "project")
                    optional "format" (ParamType.Enum [ "csv"; "json" ]) |> QueryParam.withDefault (Value.Text "csv") ] }
      guarded
          { Route.create Names.Candidates "candidates" with
              Query =
                  [ optional "status" (ParamType.Enum candidateStatuses) |> QueryParam.withDefault (Value.Text awaitingCandidates.Status)
                    optional "source" ParamType.String ] }
      guarded { Route.create Names.Candidate "candidates/{id}" with Requires = [ Requires.Candidate ] }
      guarded
          { Route.create "settings" "more" with
              Children =
                  [ Route.create "all" ""
                    { Route.create "people" "people" with Guard = Some Guards.Administrator }
                    { Route.create "index" "index" with Guard = Some Guards.Administrator }
                    // The other parts' names are the enum's values: `{section:enum:a|b}`.
                    Route.create "section" $"""{{section:enum:{openSections |> List.map sectionName |> String.concat "|"}}}""" ] }
      { Route.create Names.SignIn "sign-in" with Query = [ optional "returnTo" ParamType.String ] }
      Route.create Names.NotFound "{*rest}" ]

/// Addresses Chrona used before (WI-0046's fragment routes), and where they
/// live now. A link someone kept still opens the same place.
let private legacy: LegacyRoute list =
    [ { Path = "today"; To = Names.Today; Params = [] }
      { Path = "today/{on:date}"; To = Names.Day; Params = [ "on", Template.FromParam "on" ] }
      { Path = "activity/{id}"; To = Names.Entry; Params = [ "id", Template.FromParam "id" ] } ]

let private roles =
    { Home = Names.Today
      SignIn = Some Names.SignIn
      NotFound = Some Names.NotFound }

/// Chrona's route table. A table that does not define is a defect in this
/// module, found by its tests before anything runs.
let table =
    match RouteTable.define routes legacy roles with
    | Ok table -> table
    | Error errors -> invalidOp $"Chrona's route table does not define: %A{errors}"

/// Chrona keeps its places in the fragment: a static host serves the one page
/// at every address.
let mode = LocationMode.Hash

// ---- the typed codec ---------------------------------------------------------------

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
