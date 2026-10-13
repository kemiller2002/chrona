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
/// Chrona's table over them; its typed codec is `PlaceAddresses`. Pure.
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
      /// "mine", or "everyone": the organization's time, for those who may
      /// see it (WI-0039).
      People: string
      /// One person's time, among everyone's.
      Person: string option
      /// "this" organization, or "all" the deployment's organizations the
      /// person belongs to (WI-0075).
      Organizations: string
      /// "project", "activityType", "tag", "day" or "person".
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
      People = "mine"
      Person = None
      Organizations = "this"
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

let internal sectionNames =
    [ References, "references"
      PeriodSettings, "periods"
      Changes, "changes"
      Account, "account"
      People, "people"
      ActivityIndex, "index"
      Inboxes, "inbox" ]

let sectionName (section: Section) = sectionNames |> List.find (fst >> (=) section) |> snd

let internal sectionOf (name: string) = sectionNames |> List.tryFind (snd >> (=) name) |> Option.map fst

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
let internal openSections = sectionNames |> List.map fst |> List.filter (administrative >> not)

let internal groupings =
    [ "project", "project"; "activityType", "type"; "tag", "tag"; "day", "day"; "person", "person"; "organization", "organization" ]

/// Whose time a report covers: the person's own, or everyone's (WI-0039).
let reportPeople = [ "mine"; "everyone" ]

/// Which organizations a report covers: this one, or all of the
/// deployment's organizations the person belongs to (WI-0075).
let reportOrganizations = [ "this"; "all" ]

let internal optional name kind = QueryParam.optional name kind

/// The candidate statuses an address may name.
let candidateStatuses = [ "awaiting"; "decided"; "all" ]

/// A place people work in: behind sign-in, and in an organization, named
/// last among its parameters.
let internal guarded (route: Route) =
    { route with
        Guard = Some Guards.SignedIn
        Query = route.Query @ [ optional "org" ParamType.String ] }

let internal routes: Route list =
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
                    optional "people" (ParamType.Enum reportPeople) |> QueryParam.withDefault (Value.Text "mine")
                    optional "person" ParamType.String
                    optional "organizations" (ParamType.Enum reportOrganizations) |> QueryParam.withDefault (Value.Text "this")
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
let internal legacy: LegacyRoute list =
    [ { Path = "today"; To = Names.Today; Params = [] }
      { Path = "today/{on:date}"; To = Names.Day; Params = [ "on", Template.FromParam "on" ] }
      { Path = "activity/{id}"; To = Names.Entry; Params = [ "id", Template.FromParam "id" ] } ]

let internal roles =
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
