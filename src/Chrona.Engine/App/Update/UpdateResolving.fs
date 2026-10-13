/// Changes not stored, and records edited outside Chrona (WI-0035): kept,
/// tried again or redone by the person, and outside edits accepted after
/// the usual rules (21, 23, 34, 41).
module Chrona.Engine.App.UpdateResolving

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

/// What sending a request again needs the person to hold now (3): the
/// roster may have changed since it was first sent.
let internal requestNeeds (request: StoreRequest) =
    [ for activity in request.Activities do
          if activity.Revision = 1 then Access.RecordOwnTime else Access.AmendOwnTime
      if not request.Attestations.IsEmpty then
          Access.AttestOwnDay
      for item in request.References do
          match item.Kind with
          | Reference.ActivityType -> Access.ManageActivityTypes
          | Reference.Tag -> Access.ManageTags
          | Reference.Client
          | Reference.Project
          | Reference.Engagement -> Access.ManageProjects
      if not (request.Members.IsEmpty && request.RemovedMembers.IsEmpty) then
          Access.ManageOrganizationSettings ]
    |> List.distinct

/// The page as it is once a request's records are stored.
let internal applyRequest (request: StoreRequest) (model: Model) =
    let members =
        request.Members
        |> List.fold (fun members (m: Access.Membership) -> Map.add m.Principal.PrincipalId m members) model.Roster.Members

    let audited =
        request.Audit |> List.map _.Entry |> List.filter (fun entry -> not (List.contains entry model.Ledger.Audit))

    { model with
        Ledger =
            { Activities = request.Activities |> List.fold (fun activities a -> Map.add a.ActivityId a activities) model.Ledger.Activities
              Audit = model.Ledger.Audit @ audited }
        References =
            { model.References with
                Items = request.References |> List.fold (fun items item -> Map.add (item.Kind, item.Id) item items) model.References.Items }
        Attestations = model.Attestations @ (request.Attestations |> List.filter (fun a -> not (List.contains a model.Attestations)))
        Store =
            { model.Store with
                Audited = model.Store.Audited + audited.Length }
        Roster =
            { model.Roster with
                Members = request.RemovedMembers |> List.fold (fun members id -> Map.remove id members) members } }

let internal withoutCase (caseId: string) (model: Model) =
    { model with Store = { model.Store with Conflicts = model.Store.Conflicts |> List.filter (fun case -> case.Id <> caseId) } }

let internal findCase (caseId: string) (model: Model) =
    model.Store.Conflicts |> List.tryFind (fun case -> case.Id = caseId)

/// The person keeps what is stored and sets their change aside, knowingly.
let internal keepStored (caseId: string) (model: Model) =
    match findCase caseId model with
    | Some _ ->
        { clear ConflictForm (withoutCase caseId model) with
            Announcement = "Your change was set aside. What is stored stays." },
        []
    | None -> model, []

/// Sends a change again that was refused only because the repository kept
/// moving: the store decides it again on what is stored then.
let internal retryChange (ctx: Ctx) (caseId: string) (model: Model) =
    match findCase caseId model with
    | Some case when case.Divergences = [ Reconcile.KeptChanging ] ->
        let refusals =
            requestNeeds case.Request
            |> List.choose (fun capability ->
                match Access.authorize model.Roster model.Session.OrganizationId model.Session.ActorId capability with
                | Ok() -> None
                | Error refusal -> Some refusal)

        match refusals with
        | [] ->
            let request = { case.Request with CommitId = ctx.NewId "COMMIT" }
            let next = applyRequest request (clear ConflictForm (withoutCase caseId model))

            { next with
                Store = { next.Store with Pending = next.Store.Pending @ [ request ] }
                Announcement = "Trying your change again." },
            [ Store request ]
        | refusals -> withProblems ConflictForm refusals model, []
    | _ -> model, []

/// The local wall-clock text a form shows for an instant.
let internal formTime (zone: Zone) (instant: DateTimeOffset) =
    let local = occurrence zone instant
    Format.isoDate local.LocalDate, local.LocalTime.ToString("HH:mm", Globalization.CultureInfo.InvariantCulture)

/// The person redoes their version of one activity on what is stored now,
/// through the usual forms and rules: a changed classification goes into
/// the correction form of the current version; new time that overlapped
/// goes back into the manual entry form. The change is then theirs to save.
let internal redoChange (caseId: string) (activityId: string) (model: Model) =
    let divergence =
        findCase caseId model
        |> Option.bind (fun case ->
            case.Divergences
            |> List.tryFind (function
                | Reconcile.ActivityChanged(mine, _)
                | Reconcile.OverlapsStored(mine, _) -> activityId = "" || mine.ActivityId = activityId
                | _ -> false))

    match divergence, model.Zone with
    | Some(Reconcile.ActivityChanged(mine, Some _)), _ ->
        let activityId = mine.ActivityId

        match openDetail model activityId with
        | Some current when model.Ledger.Activities[activityId].Record = Recorded ->
            let next = clear ConflictForm (withoutCase caseId model)

            navigate
                (Places.Entry(activityId, Some mine.Occurrence.LocalDate))
                { next with
                    Detail = Some { current with Amend = ofClassification mine.Classification }
                    Announcement = "Your version is in the correction form, on the current record. Save it to apply it." }
        | _ -> model, []
    | Some(Reconcile.OverlapsStored(mine, _)), Some zone when mine.Revision = 1 ->
        let next = clear ConflictForm (withoutCase caseId model)

        let startDate, startTime, endDate, endTime =
            match mine.Timing with
            | Interval(start, finish) ->
                let sd, st = formTime zone start
                let ed, et = formTime zone finish
                sd, st, ed, et
            | DurationOnDate _ ->
                let date = Format.isoDate mine.Occurrence.LocalDate
                date, "", date, ""

        navigate
            Places.Track
            { next with
                Manual =
                    { Classification = ofClassification mine.Classification
                      StartDate = startDate
                      StartTime = startTime
                      EndDate = endDate
                      EndTime = endTime
                      Reason = defaultArg mine.Reason "" }
                Announcement = "Your entry is back in the form. Change its time so it no longer overlaps, then save it." }
    | _ -> model, []

/// The person accepts a record of theirs edited outside Chrona, after it
/// passes the rules of a recorded activity (41). It is stored as Chrona's
/// next revision of it.
let internal acceptOutsideEdit (ctx: Ctx) (activityId: string) (model: Model) =
    match model.Store.Held |> List.tryFind (fun a -> a.ActivityId = activityId), zoneOrProblem model with
    | None, _ -> model, []
    | _, Error problems -> withProblems OutsideEditForm problems model, []
    | Some held, Ok zone ->
        let trusted = model.Ledger.Activities |> Map.toList |> List.map snd

        match Persistence.acceptance (commandContext ctx model zone) model.Roster trusted held with
        | Error problems -> withProblems OutsideEditForm problems model, []
        | Ok accepted ->
            // Accepting is audited like any change to a record (25).
            let entry: Ledger.AuditEntry =
                { Performer = model.Session.ActorId
                  At = ctx.Now
                  Source = "chrona-web"
                  Command = "accept-outside-edit"
                  ActivityIds = [ activityId ]
                  PriorRevisions = [ activityId, held.Revision ]
                  ResultingRevisions = [ activityId, accepted.Revision ]
                  Reason = None
                  CorrelationId = None }

            let request =
                { emptyRequest (ctx.NewId "COMMIT") with
                    Activities = [ accepted ]
                    Accepted = [ held ]
                    Audit = AuditRecord.place [ accepted ] [ entry ] }

            let heldPath =
                ActivityRecord.path held |> Result.toOption |> Option.map Arca.RelativePath.render

            { clear OutsideEditForm model with
                Ledger =
                    { Activities = Map.add activityId accepted model.Ledger.Activities
                      Audit = model.Ledger.Audit @ [ entry ] }
                Store =
                    { model.Store with
                        Held = model.Store.Held |> List.filter (fun a -> a.ActivityId <> activityId)
                        Integrity =
                            model.Store.Integrity
                            |> List.filter (function
                                | ExternalEdit path -> Some path <> heldPath
                                | _ -> true)
                        Pending = model.Store.Pending @ [ request ]
                        Audited = model.Ledger.Audit.Length + 1 }
                Announcement = $"Accepted the outside change to \"{accepted.Classification.Description}\"." },
            [ Store request ]
