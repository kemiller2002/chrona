/// Observations' candidates (WI-0038): accepting one as the person's time,
/// or rejecting it with a reason (expansion 19, 26, 27).
module Chrona.Engine.App.UpdateCandidates

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
open Chrona.Engine.App.UpdatePlaces
open Chrona.Engine.App.UpdatePeriods

/// The candidate on screen, as the person may decide it.
let internal candidateOnScreen (model: Model) =
    match model.Place with
    | Places.Candidate id -> visibleCandidates model |> List.tryFind (fun candidate -> candidate.CandidateId = id)
    | _ -> None

let internal inboxOf (model: Model) : Observations.Inbox =
    { Observations.empty with Candidates = model.Candidates |> List.map (fun candidate -> candidate.CandidateId, candidate) |> Map.ofList }

/// Sends a candidate's decision with the activities it recorded and their
/// audit entries: one change, so the decision and its time land together.
let internal sendCandidate (ctx: Ctx) (activities: Activity list) (entries: Ledger.AuditEntry list) (candidate: Observations.Candidate) (model: Model) =
    sendRequest
        { emptyRequest (ctx.NewId "COMMIT") with
            Activities = activities
            Candidates = [ candidate ]
            Audit = AuditRecord.place activities entries }
        { model with
            Candidates = model.Candidates |> List.map (fun c -> if c.CandidateId = candidate.CandidateId then candidate else c)
            CandidateDraftFor = ""
            CandidateReason = "" }

/// Accepts the candidate on screen as one activity, classified as the
/// person says (expansion 19, 26): every ordinary rule still applies, and
/// time in a submitted or closed period is refused until it is reopened.
let internal acceptCandidate (ctx: Ctx) (model: Model) =
    match candidateOnScreen model, zoneOrProblem model with
    | None, _ -> model, []
    | _, Error problems -> withProblems CandidateForm problems model, []
    | Some candidate, Ok zone ->
        let classification = toClassification model.CandidateDraft

        let problems =
            [ if classification.ActivityTypeId = "" then MissingField "activityType"
              if classification.ProjectId = "" then MissingField "project"
              if classification.BusinessPurpose = "" then MissingField "businessPurpose"
              yield! Reference.assignmentProblems model.References [] { classification with Tags = [] } ]

        let context: Observations.DecisionContext =
            { Ledger = commandContext ctx model zone
              Zone = zone }

        let accepted =
            match problems with
            | _ :: _ -> Error problems
            | [] ->
                Observations.accept context (ctx.NewId "ACT") candidate.CandidateId candidate.Revision classification (inboxOf model) model.Ledger
                |> Result.bind (fun (inbox, ledger) ->
                    let touched = PeriodReview.touchedBy model.Ledger.Activities (changed model.Ledger ledger)

                    match PeriodReview.restrictions model.PeriodConfig model.Reviews touched with
                    | [] -> Ok(inbox.Candidates[candidate.CandidateId], ledger)
                    | restricted -> Error restricted)

        match accepted with
        | Error problems -> withProblems CandidateForm (List.distinct problems) model, []
        | Ok(decided, ledger) ->
            let recorded = changed model.Ledger ledger
            let entries = ledger.Audit |> List.skip model.Ledger.Audit.Length
            let total = recorded |> List.sumBy _.Minutes

            { clear CandidateForm model with
                Ledger = ledger
                Store = { model.Store with Audited = ledger.Audit.Length }
                Announcement = $"Accepted {Format.minutes total} from {candidate.Observation.SourceSystem}." }
            |> sendCandidate ctx recorded entries decided

/// Rejects the candidate on screen, with the reason (27): it stays, decided.
let internal rejectCandidate (ctx: Ctx) (model: Model) =
    match candidateOnScreen model, zoneOrProblem model with
    | None, _ -> model, []
    | _, Error problems -> withProblems CandidateForm problems model, []
    | Some candidate, Ok zone ->
        let context: Observations.DecisionContext =
            { Ledger = commandContext ctx model zone
              Zone = zone }

        match Observations.reject context candidate.CandidateId candidate.Revision model.CandidateReason (inboxOf model) with
        | Error problems -> withProblems CandidateForm problems model, []
        | Ok inbox ->
            { clear CandidateForm model with Announcement = $"Rejected the observation from {candidate.Observation.SourceSystem}." }
            |> sendCandidate ctx [] [] inbox.Candidates[candidate.CandidateId]
