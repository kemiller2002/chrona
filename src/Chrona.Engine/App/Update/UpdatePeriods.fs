/// Periods under review (WI-0036): settings, submitting, reopening, and
/// approving or returning another person's submission (14, 15).
module Chrona.Engine.App.UpdatePeriods

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

/// The period on screen: the period page's, or the one holding today.
let selectedPeriod (model: Model) = Periods.containing model.PeriodConfig (selectedDate model)

/// Saves the organization's period settings as edited (15): the stored
/// configuration's next revision.
let internal savePeriodSettings (ctx: Ctx) (model: Model) =
    let saved: PeriodConfigRecord.StoredPeriods =
        { Config = model.PeriodConfig
          Revision = model.PeriodsStored |> Option.map (fun stored -> stored.Revision + 1) |> Option.defaultValue 1
          ChangedBy = model.Session.ActorId
          ChangedAt = ctx.Now }

    { clear PeriodForm model with
        PeriodsStored = Some saved
        Announcement = "Period settings saved." }
    |> sendRequest { emptyRequest (ctx.NewId "COMMIT") with Periods = Some saved }

/// Sends a review step with the activities it changed and its audit entry.
let internal sendReview (ctx: Ctx) (changedActivities: Activity list) (entries: Ledger.AuditEntry list) (review: PeriodReview.PeriodReview) (model: Model) =
    sendRequest
        { emptyRequest (ctx.NewId "COMMIT") with
            Activities = changedActivities
            Reviews = [ review ]
            Audit = AuditRecord.place changedActivities entries }
        { model with Reviews = model.Reviews @ [ review ] }

/// Submits the person's period on screen (14).
let internal submitPeriod (ctx: Ctx) (model: Model) =
    match zoneOrProblem model with
    | Error problems -> withProblems ReviewForm problems model, []
    | Ok zone ->
        let period = selectedPeriod model

        match PeriodReview.submit (commandContext ctx model zone) model.PeriodConfig model.Reviews (ctx.NewId "SUB") period model.Ledger with
        | Error problems -> withProblems ReviewForm (List.distinct problems) model, []
        | Ok(ledger, review) ->
            let entries = ledger.Audit |> List.skip model.Ledger.Audit.Length

            { clear ReviewForm model with
                Ledger = ledger
                Store = { model.Store with Audited = ledger.Audit.Length }
                Announcement =
                    if model.PeriodConfig.ApprovalRequired then
                        $"Submitted {Format.longDate period.Start} to {Format.longDate period.Finish} for approval."
                    else
                        $"Submitted {Format.longDate period.Start} to {Format.longDate period.Finish}." }
            |> sendReview ctx (changed model.Ledger ledger) entries review

/// Reopens the person's own period on screen (14), with the reason:
/// withdrawing a submission waiting for approval needs only that they may
/// submit; reopening a closed period needs that they may reopen time.
let internal reopenPeriod (ctx: Ctx) (model: Model) =
    let period = selectedPeriod model
    let actorId = model.Session.ActorId

    let needed =
        match PeriodReview.stateOf model.PeriodConfig.ApprovalRequired model.Reviews actorId period with
        | PeriodReview.AwaitingApproval _ -> Access.SubmitOwnTime
        | _ -> Access.ReopenTime

    match Access.authorize model.Roster model.Session.OrganizationId actorId needed, zoneOrProblem model with
    | Error refusal, _ -> withProblems ReviewForm [ refusal ] model, []
    | _, Error problems -> withProblems ReviewForm problems model, []
    | Ok(), Ok zone ->
        match PeriodReview.reopen (commandContext ctx model zone) model.PeriodConfig model.Reviews actorId period model.ReopenReason model.Ledger with
        | Error problems -> withProblems ReviewForm (List.distinct problems) model, []
        | Ok(ledger, review) ->
            let entries = ledger.Audit |> List.skip model.Ledger.Audit.Length

            { clear ReviewForm model with
                Ledger = ledger
                ReopenReason = ""
                Store = { model.Store with Audited = ledger.Audit.Length }
                Announcement = $"Reopened {Format.longDate period.Start} to {Format.longDate period.Finish}. Its time can be changed again." }
            |> sendReview ctx (changed model.Ledger ledger) entries review

/// Approves or returns another person's submission (14), over their time
/// as read for review.
let internal decideSubmission (ctx: Ctx) (approve: bool) (submissionId: string) (model: Model) =
    match zoneOrProblem model with
    | Error problems -> withProblems ReviewForm problems model, []
    | Ok zone ->
        let theirs: Ledger.Ledger =
            { Activities = model.Reviewing |> List.map (fun a -> a.ActivityId, a) |> Map.ofList
              Audit = [] }

        let note = model.ReviewNotes.TryFind submissionId |> Option.map _.Trim() |> Option.filter ((<>) "")
        let context = commandContext ctx model zone

        let decided =
            if approve then
                PeriodReview.approve context model.PeriodConfig model.Reviews submissionId note theirs
            else
                PeriodReview.reject context model.PeriodConfig model.Reviews submissionId (defaultArg note "") theirs

        match decided with
        | Error problems -> withProblems ReviewForm (List.distinct problems) model, []
        | Ok(ledger, review) ->
            let decidedActivities = changed theirs ledger
            let names = model.Roster.Members.TryFind review.ActorId |> Option.map _.Principal.DisplayName |> Option.defaultValue review.ActorId

            { clear ReviewForm model with
                Reviewing =
                    model.Reviewing
                    |> List.map (fun a -> decidedActivities |> List.tryFind (fun d -> d.ActivityId = a.ActivityId) |> Option.defaultValue a)
                ReviewNotes = model.ReviewNotes.Remove submissionId
                Announcement =
                    if approve then
                        $"Approved {names}'s time, {Format.longDate review.Period.Start} to {Format.longDate review.Period.Finish}."
                    else
                        $"Returned {names}'s time, {Format.longDate review.Period.Start} to {Format.longDate review.Period.Finish}, for correction." }
            |> sendReview ctx decidedActivities ledger.Audit review
