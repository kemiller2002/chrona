/// Keeping the page in step with the place (CHX-460): the drafts a place
/// opens, its canonical form, and reading the months it shows that were not
/// read yet (38).
module Chrona.Engine.App.UpdatePlaces

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update
open Chrona.Engine.App.UpdateCommands
open Chrona.Engine.App.UpdateRecording
open Chrona.Engine.App.UpdateCorrections
open Chrona.Engine.App.UpdateReports

/// The draft a candidate's page opens with: what the observation proposes.
/// The activity type and business purpose are the person's to give.
let internal openCandidate (id: string) (model: Model) =
    match model.Candidates |> List.tryFind (fun candidate -> candidate.CandidateId = id) with
    | Some candidate when model.CandidateDraftFor <> id ->
        { model with
            CandidateDraft =
                { emptyClassification with
                    ProjectId = candidate.Observation.ProjectId
                    Description = defaultArg candidate.Observation.Description "" }
            CandidateDraftFor = id
            CandidateReason = "" }
    | _ -> model

/// Keeps the drafts in step with the place: the activity screen's drafts
/// opened when it is entered and dropped when it is left, and the report
/// form set from the address's filters (its export generated afresh).
let internal followRoute (model: Model) =
    // A link copied for the place left behind is not this place's.
    let model = { model with LinkStatus = ""; LinkText = "" }

    let model =
        match model.Place with
        | Places.Reports query -> { model with Report = { draftOf query model.Report with GeneratedAt = Some model.Now } }
        | _ -> model

    let model =
        match model.Place with
        | Places.Candidate id -> openCandidate id model
        | _ -> { model with CandidateDraftFor = "" }

    match model.Place, model.Detail with
    | Places.Entry(id, _), Some detail when detail.ActivityId = id -> model
    | Places.Entry(id, _), _ -> { model with Detail = openDetail model id }
    | _ -> { model with Detail = None }

/// A place in its canonical form: a week or period named by its first day
/// (the organization's week start and cadence decide which day that is).
let normalize (model: Model) (place: Places.Place) =
    match place with
    | Places.Week(on, project) -> Places.Week((weekOf model on).Start, project)
    | Places.Period on -> Places.Period (Periods.containing model.PeriodConfig on).Start
    | other -> other

/// The months a place shows stored time from. An activity named without its
/// day is looked for in every month that holds the person's time.
let internal monthsNeeded (model: Model) =
    let monthOf (date: DateOnly) = date.Year, date.Month

    let between (first: DateOnly) (last: DateOnly) = [ monthOf first; monthOf last ]

    // Every month that holds the person's time, from the activity index.
    let history () =
        model.Store.History
        |> List.filter (fun total -> total.ActorId = model.Session.ActorId)
        |> List.map (fun total -> total.Year, total.Month)

    match model.Place with
    | Places.Day(on, _)
    | Places.Review on -> [ monthOf on ]
    | Places.Week(on, _) -> let week = weekOf model on in between week.Start week.Finish
    | Places.Period on -> let period = Periods.containing model.PeriodConfig on in between period.Start period.Finish
    | Places.Month(year, month) -> [ year, month ]
    // A report reads every month of its range from the stored records
    // (WI-0039), not just the months already open.
    | Places.Reports _ -> reportMonths model model.Session.ActorId
    | Places.Entry(id, _) when model.Ledger.Activities.ContainsKey id -> []
    | Places.Entry(_, Some on) -> [ monthOf on ]
    | Places.Entry(_, None)
    // A project's page shows all of the person's time on it.
    | Places.Project _ -> history ()
    // A candidate's day is read, so accepting it is checked against the
    // time already there; one not found yet may be decided in any month.
    | Places.Candidate id ->
        match model.Candidates |> List.tryFind (fun candidate -> candidate.CandidateId = id) with
        | Some candidate -> [ monthOf (observedDate model candidate.Observation) ]
        | None -> history ()
    | _ -> []

/// Other people's months a report across everyone needs: for someone who
/// may see the organization's time, each person's months in the range
/// (WI-0039).
let internal othersNeeded (model: Model) =
    match model.Place with
    | Places.Reports query when query.People = "everyone" && seesOrganization model ->
        let people =
            match query.Person with
            | Some person -> [ person ]
            | None -> reportPeople model

        people
        |> List.filter ((<>) model.Session.ActorId)
        |> List.collect (fun person -> reportMonths model person |> List.map (fun month -> person, month))
    | _ -> []

/// The other organizations a report across organizations still needs read:
/// one not read yet, or read for less than the report asks (only the
/// person's time where everyone's is wanted, or fewer months). Which months
/// hold time there is that organization's own index to say, so every month
/// of the range is asked for.
let internal elsewhereNeeded (model: Model) =
    match model.Place with
    | Places.Reports query when query.Organizations = "all" ->
        let months = reportRange model ||> monthsBetween
        let everyone = query.People = "everyone"

        otherOrganizations model
        |> List.filter (fun id ->
            match model.Elsewhere.TryFind id with
            | Some read when read.Failure.IsNone ->
                (everyone && not read.Everyone && read.SeesOrganization)
                || months |> List.exists (fun month -> not (List.contains month read.Months))
            | _ -> true)
        |> List.map (fun id -> id, everyone, months)
    | _ -> []

/// A month the person goes to that was not read yet is read now: one month
/// folder, on demand, never the whole history (38). A report across
/// everyone reads each person's months it covers the same way (WI-0039).
let internal readNeeded (model: Model) =
    match model.Store.Kind with
    | Durable _ when canWork model ->
        let unread =
            monthsNeeded model
            |> List.distinct
            |> List.filter (fun month -> not (List.contains month model.Store.Months) && not (List.contains month model.Store.Reading))

        let theirs =
            othersNeeded model
            |> List.distinct
            |> List.filter (fun wanted -> not (List.contains wanted model.Store.Others) && not (List.contains wanted model.Store.OthersReading))

        let firstOf (year, month) = DateOnly(year, month, 1)

        let elsewhere =
            elsewhereNeeded model |> List.filter (fun (id, _, _) -> not (List.contains id model.ElsewhereReading))

        { model with
            Store =
                { model.Store with
                    Reading = model.Store.Reading @ unread
                    OthersReading = model.Store.OthersReading @ theirs }
            ElsewhereReading = model.ElsewhereReading @ (elsewhere |> List.map (fun (id, _, _) -> id)) },
        [ if not unread.IsEmpty then
              ReadMonths(unread |> List.map firstOf)
          if not theirs.IsEmpty then
              ReadOthers(theirs |> List.map (fun (person, month) -> person, firstOf month))
          if not elsewhere.IsEmpty then
              ReadElsewhere(elsewhere |> List.map (fun (id, everyone, months) -> id, everyone, months |> List.map firstOf)) ]
    | _ -> model, []
