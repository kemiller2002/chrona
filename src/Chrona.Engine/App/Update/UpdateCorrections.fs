/// The activity detail: amend, void, restore, split and evidence; merging
/// activities and attesting a day (WI-0020 onwards).
module Chrona.Engine.App.UpdateCorrections

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

/// The detail drafts for an activity, at the revision it has now.
let openDetail (model: Model) (activityId: string) : Detail option =
    model.Ledger.Activities.TryFind activityId
    |> Option.map (fun a ->
        { ActivityId = activityId
          Revision = a.Revision
          Amend = ofClassification a.Classification
          AmendReason = ""
          VoidReason = ""
          SplitFirst = ""
          SplitSecond = ""
          SplitEvidence = a.Evidence |> List.map (fun e -> e.Id, 1) |> Map.ofList
          EvidenceKind = "url"
          EvidenceUrl = ""
          EvidenceLabel = ""
          EvidenceSource = ""
          EvidenceNotes = "" })

let internal detail (f: Detail -> Detail) (model: Model) =
    { model with Detail = model.Detail |> Option.map f }, []

/// Runs a ledger command for the detail screen: on success the ledger moves,
/// what changed is committed and the detail reopens at the new revision.
let internal ledgerCommand (ctx: Ctx) (form: Form) (announcement: string) (command: Detail -> Ledger.Command) (model: Model) =
    match model.Detail, zoneOrProblem model with
    | None, _ -> model, []
    | _, Error problems -> withProblems form problems model, []
    | Some d, Ok zone ->
        match execute model (commandContext ctx model zone) model.Ledger (command d) with
        | Error problems -> withProblems form problems model, []
        | Ok ledger ->
            let next = { clear form model with Ledger = ledger; Announcement = announcement }
            commit ctx (changed model.Ledger ledger) [] { next with Detail = openDetail next d.ActivityId }

let internal saveAmend (ctx: Ctx) (model: Model) =
    ledgerCommand
        ctx
        AmendForm
        "Amended. The earlier version stays in the history."
        (fun d ->
            Ledger.Amend(
                d.ActivityId,
                d.Revision,
                { Classification = Some(toClassification d.Amend)
                  Billability = None
                  BillingReference = None
                  Retime = None
                  Reason = d.AmendReason.Trim() }
            ))
        model

let internal voidActivity ctx model =
    ledgerCommand ctx VoidForm "Removed from totals. The record and its history are kept." (fun d -> Ledger.Void(d.ActivityId, d.Revision, d.VoidReason.Trim())) model

let internal restoreActivity ctx model =
    ledgerCommand ctx VoidForm "Restored to totals." (fun d -> Ledger.Restore(d.ActivityId, d.Revision)) model

let internal minutesOf (text: string) =
    match Int32.TryParse(text.Trim()) with
    | true, n -> n
    | _ -> 0

let internal saveSplit (ctx: Ctx) (model: Model) =
    let part (d: Detail) (index: int) (minutes: string) : Ledger.SplitPart =
        { ActivityId = ctx.NewId "ACT"
          Minutes = minutesOf minutes
          Classification = None
          EvidenceIds = d.SplitEvidence |> Map.toList |> List.filter (fun (_, p) -> p = index) |> List.map fst }

    ledgerCommand
        ctx
        SplitForm
        "Split into two activities. The original is kept, superseded."
        (fun d -> Ledger.Split(d.ActivityId, d.Revision, [ part d 1 d.SplitFirst; part d 2 d.SplitSecond ]))
        model

/// What a person typed, or None for nothing.
let internal optionalText (text: string) =
    if String.IsNullOrWhiteSpace text then None else Some(text.Trim())

let internal attachEvidence (ctx: Ctx) (model: Model) =
    ledgerCommand
        ctx
        EvidenceForm
        "Evidence linked."
        (fun d ->
            Ledger.LinkEvidence(
                d.ActivityId,
                d.Revision,
                { Id = ctx.NewId "EVD"
                  Url = d.EvidenceUrl.Trim()
                  Kind = d.EvidenceKind.Trim()
                  Label = d.EvidenceLabel.Trim()
                  CapturedAt = ctx.Now
                  Hash = None
                  Source = optionalText d.EvidenceSource
                  Notes = optionalText d.EvidenceNotes }
            ))
        model

let internal unlinkEvidence (ctx: Ctx) (evidenceId: string) (model: Model) =
    ledgerCommand ctx EvidenceForm "Evidence unlinked. The link stays in the history." (fun d -> Ledger.UnlinkEvidence(d.ActivityId, d.Revision, evidenceId)) model

let internal saveMerge (ctx: Ctx) (model: Model) =
    match zoneOrProblem model with
    | Error problems -> withProblems MergeForm problems model, []
    | Ok zone ->
        let sources =
            model.MergeSelection
            |> List.choose (fun id -> model.Ledger.Activities.TryFind id |> Option.map (fun a -> id, a.Revision))

        let draft = toClassification model.MergeDraft

        // The merged record keeps the earliest source's classification,
        // except for what the person changed in the merge form.
        let classification =
            sources
            |> List.choose (fun (id, _) -> model.Ledger.Activities.TryFind id)
            |> List.sortBy (fun a -> a.Occurrence.LocalDate, a.Occurrence.LocalTime)
            |> List.tryHead
            |> Option.map (fun earliest ->
                let pick (typed: string) (kept: string) = if typed = "" then kept else typed
                let kept = earliest.Classification

                { kept with
                    ActivityTypeId = pick draft.ActivityTypeId kept.ActivityTypeId
                    ProjectId = pick draft.ProjectId kept.ProjectId
                    Description = pick draft.Description kept.Description
                    BusinessPurpose = pick draft.BusinessPurpose kept.BusinessPurpose })

        let newId = ctx.NewId "ACT"

        match execute model (commandContext ctx model zone) model.Ledger (Ledger.Merge(sources, newId, classification)) with
        | Error problems -> withProblems MergeForm (List.distinct problems) model, []
        | Ok ledger ->
            { clear MergeForm model with
                Ledger = ledger
                MergeSelection = []
                MergeDraft = emptyClassification
                Announcement = $"Merged {sources.Length} activities into one. The originals are kept, superseded." }
            |> commit ctx (changed model.Ledger ledger) []

let internal attestDay (ctx: Ctx) (model: Model) =
    match zoneOrProblem model with
    | Error problems -> withProblems AttestForm problems model, []
    | Ok zone ->
        let workflow = { Review.start model.Ledger with Attestations = model.Attestations }
        let date = selectedDate model

        // A day in a submitted or closed period is attested before it is
        // submitted, or after the period is reopened (WI-0036).
        let attested =
            match PeriodReview.restrictions model.PeriodConfig model.Reviews [ model.Session.ActorId, date ] with
            | [] -> Review.attest (commandContext ctx model zone) date model.AttestStatement workflow
            | problems -> Error problems

        match attested with
        | Error problems -> withProblems AttestForm problems model, []
        | Ok(next, attestation) ->
            { clear AttestForm model with
                Attestations = next.Attestations
                AttestStatement = ""
                Announcement = $"Attested {Format.longDate date}." }
            |> commitWith ctx [] [] [ attestation ]

let internal mergeDraft f = draft f _.MergeDraft (fun m d -> { m with MergeDraft = d })

let internal amendDraft (f: ClassificationDraft -> ClassificationDraft) =
    detail (fun d -> { d with Amend = f d.Amend })
