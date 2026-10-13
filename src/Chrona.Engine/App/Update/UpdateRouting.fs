/// Where the person is (CHX-460): the address settled after every message,
/// under what the engine knows now.
module Chrona.Engine.App.UpdateRouting

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update
open Chrona.Engine.App.UpdateCommands
open Chrona.Engine.App.UpdateRecording
open Chrona.Engine.App.UpdateCorrections
open Chrona.Engine.App.UpdateReports
open Chrona.Engine.App.UpdatePlaces
open Chrona.Engine.App.UpdatePeriods
open Chrona.Engine.App.UpdateCandidates
open Chrona.Engine.App.UpdateMembers
open Chrona.Engine.App.UpdateResolving
open Chrona.Engine.App.UpdateEvents
open Chrona.Engine.App.UpdateDeviceTimer
open Chrona.Engine.App.UpdateSession
open Chrona.Engine.App.UpdateSignOut

/// What the guards need to know about the person now.
let internal standing (model: Model) : Places.Standing =
    { SignedOut = (model.Identity.Mode = SignInRequired false)
      KeptReturn = model.Identity.ReturnTo
      Administrator =
        if canWork model then
            Some(permits model Access.ManageOrganizationSettings)
        else
            None }

let internal guardOf (model: Model) = Places.guard (standing model)

/// Someone may work now, and no kept return target is still being read.
let internal mayResume (model: Model) =
    not model.Identity.ReadingReturn
    && (match model.Identity.Mode with
        | LocalOnly
        | SignedInMode -> true
        | _ -> false)

/// Whether a record an address names may still arrive: the records are
/// still opening, or a month is still being read.
let internal stillLooking (model: Model) =
    not (canWork model)
    || model.Store.Opening
    || not model.Store.Reading.IsEmpty
    || not model.Store.OthersReading.IsEmpty
    || not model.ElsewhereReading.IsEmpty

/// A record the place names that does not exist, or is not the person's.
/// Decided again whenever records arrive.
let internal recordProblem (model: Model) =
    let exists kind id =
        Reference.all kind model.References |> List.exists (fun item -> item.Id = id)

    let missingReference (kind: Reference.Kind, name: string) (id: string option) =
        match id with
        | Some id when canWork model && not (exists kind id) -> Some(RecordMissing(name, id))
        | _ -> None

    match model.Place with
    | Places.Entry(id, _) ->
        match model.Ledger.Activities.TryFind id with
        | Some activity when activity.ActorId = model.Session.ActorId -> None
        | Some _ -> Some(AddressProblem(Limen.Routing.RouteError.NotPermitted Places.Names.Entry))
        | None when stillLooking model -> None
        | None -> Some(RecordMissing("activity", id))
    | Places.Day(_, project)
    | Places.Week(_, project) -> missingReference (Reference.Project, "project") project
    | Places.Project id -> missingReference (Reference.Project, "project") (Some id)
    | Places.Candidate id ->
        match model.Candidates |> List.tryFind (fun candidate -> candidate.CandidateId = id) with
        | Some candidate when candidate.Observation.ActorId |> Option.forall ((=) model.Session.ActorId) -> None
        | Some _ -> Some(AddressProblem(Limen.Routing.RouteError.NotPermitted Places.Names.Candidate))
        | None when stillLooking model -> None
        | None -> Some(RecordMissing("candidate", id))
    // Everyone's time is for those who may see the organization's; one
    // person's must be someone the organization has (WI-0039).
    | Places.Reports query when query.People = "everyone" && canWork model && not (seesOrganization model) ->
        Some(AddressProblem(Limen.Routing.RouteError.NotPermitted Places.Names.Reports))
    | Places.Reports query when
        canWork model
        && query.Person |> Option.exists (fun person -> not (List.contains person (reportPeople model)))
        && not (stillLooking model)
        ->
        Some(RecordMissing("person", defaultArg query.Person ""))
    | Places.Reports query ->
        [ missingReference (Reference.Project, "project") query.ProjectId
          missingReference (Reference.ActivityType, "activity type") query.ActivityTypeId
          missingReference (Reference.Tag, "tag") query.Tag ]
        |> List.tryPick id
    | _ -> None

/// Shows a place: entering a different one opens what it needs and reads
/// the months it shows that were not read yet.
let internal arrive (place: Places.Place) (model: Model) =
    let model, effects =
        if place = model.Place then
            model, []
        else
            readNeeded (followRoute { model with Place = place })

    // An activity's drafts open once its record has arrived.
    match model.Place, model.Detail with
    | Places.Entry(id, _), None -> { model with Detail = openDetail model id }, effects
    | Places.Candidate id, _ -> openCandidate id model, effects
    | _ -> model, effects

/// Settles where the person is from the current address, after every
/// message: the address is adopted again under what the engine knows now
/// (who is signed in, what they may do, which records arrived). Adopting
/// never adds a history entry; at most it replaces the address with its
/// canonical form, or with sign-in when someone must sign in first. Once
/// someone may work, a sign-in page or a kept return target resumes to the
/// target (re-checked), replacing the entry so Back never returns to it.
let rec internal settle (resumed: bool) (model: Model) : Model * Effect list =
    match model.Router.Current with
    | None -> model, []
    | Some location ->
        let router, result, correction = Limen.Routing.RouteCodec.adopt Places.codec (guardOf model) model.Router location
        let model = { model with Router = router }
        let corrected = correction |> Option.map Navigate |> Option.toList

        match result with
        | Ok { Place = Places.SignIn returnTo } when mayResume model && not resumed ->
            resumeAt (Places.resume (guardOf model) (returnTo |> Option.orElse model.Identity.ReturnTo)) model
        | Ok _ when model.Identity.ReturnTo.IsSome && mayResume model && not resumed ->
            resumeAt (Places.resume (guardOf model) model.Identity.ReturnTo) model
        | Ok address ->
            match organizationStep model address with
            | MissingOrganization organization -> { model with RouteProblem = Some(RecordMissing("organization", organization)) }, corrected
            // A link into another of the deployment's organizations: nothing of
            // this one stays in the page, and the other's records are opened.
            | SwitchTo organization ->
                let switched, opening = openStore (fresh { model.Session with OrganizationId = organization } model.Identity model)
                let settled, effects = settle resumed switched
                settled, corrected @ opening @ effects
            | StayHere ->
                // A week or period named by another of its days is named by its
                // first; the organization is named exactly when it must be.
                let wanted = { addressOf model (normalize model address.Place) with Organization = wantedOrganization model address }

                if wanted <> address && not resumed then
                    match Places.format wanted with
                    | Ok canonical -> resumeAt canonical model
                    | Error error -> invalidOp $"No address for {wanted}: %A{error}"
                else
                    let arrived, effects = arrive address.Place model
                    { arrived with RouteProblem = recordProblem arrived }, corrected @ effects
        | Error problem -> { model with RouteProblem = Some(AddressProblem problem) }, corrected

and internal resumeAt (target: string) (model: Model) =
    let router, moved = Limen.Routing.Navigation.replace model.Router target
    let next, effects = settle true { model with Router = router; Identity = { model.Identity with ReturnTo = None } }
    next, (moved |> Option.map Navigate |> Option.toList) @ effects
