/// Addresses: not found, not permitted, and the copied link (CHX-460).
module Chrona.Engine.App.ProjectAddresses

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Engine.View
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Engine.App.ProjectWords
open Chrona.Engine.App.ProjectShell
open Chrona.Engine.App.ProjectToday
open Chrona.Engine.App.ProjectTrack
open Chrona.Engine.App.ProjectMore
open Chrona.Engine.App.ProjectConflicts
open Chrona.Engine.App.ProjectCandidates
open Chrona.Engine.App.ProjectObligations
open Chrona.Engine.App.ProjectDetail
open Chrona.Engine.App.ProjectReview
open Chrona.Engine.App.ProjectReports
open Chrona.Engine.App.ProjectWeeks

/// What Limen's expectation for a parameter means, for a person.
let internal expectedText (expected: string) =
    match expected with
    | "date" -> "a date such as 2026-10-08"
    | "month" -> "a month such as 2026-10"
    | "int" -> "a whole number"
    | "bool" -> "true or false"
    | "a single value" -> "given once"
    | other when other.StartsWith "one of " -> "one of " + (other.Substring 7).Replace("|", ", ")
    | other -> other

let internal problemView (model: Model) =
    let kind, title, detail =
        match model.RouteProblem with
        | None -> "", "", ""
        | Some(RecordMissing(what, id)) ->
            "not-found", "Not found", $"Nothing in your records is the {what} with the id \"{id}\". It may have been removed, or the link may be wrong."
        | Some(AddressProblem problem) ->
            match problem with
            | Limen.Routing.RouteError.NotPermitted route when route = Places.Names.Entry ->
                "not-permitted", "Not permitted", "This activity is someone else's. Only your own time is shown to you."
            | Limen.Routing.RouteError.NotPermitted _ ->
                "not-permitted", "Not permitted", "This part of Chrona is for the organization's administrators. Ask one of them if you need it."
            | Limen.Routing.RouteError.Invalid(_, parameter, value, expected) ->
                "not-found", "Page not found", $"This address is not one of Chrona's pages: its {parameter} \"{value}\" should be {expectedText expected}."
            | Limen.Routing.RouteError.Malformed _ ->
                "not-found", "Page not found", "This address could not be read. It may have been cut short or changed when it was copied."
            | Limen.Routing.RouteError.NotFound
            | Limen.Routing.RouteError.RedirectLoop _
            | Limen.Routing.RouteError.Unmapped _ -> "not-found", "Page not found", "Chrona has no page at this address."

    [ flag "screenProblem" model.RouteProblem.IsSome
      text "problemKind" kind
      text "problemTitle" title
      text "problemDetail" detail ]

/// "Copy link" for the view shown: offered whenever the address names one.
let internal linkView (model: Model) =
    [ flag "canCopyLink" model.RouteProblem.IsNone
      flag "hasLinkStatus" (model.LinkStatus <> "")
      text "linkStatus" model.LinkStatus
      // The browser would not copy it: the link, to select by hand.
      flag "linkRefused" (model.LinkStatus <> "" && model.LinkText <> "")
      text "linkText" model.LinkText ]
