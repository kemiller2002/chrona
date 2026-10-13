/// A page event (`Ui`): the capability it needs (3), and what it does.
module Chrona.Engine.App.UpdateEvents

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
open Chrona.Engine.App.UpdateCandidates
open Chrona.Engine.App.UpdateMembers
open Chrona.Engine.App.UpdateResolving

let internal month (offset: int) (model: Model) =
    let first = (selectedMonth model).AddMonths offset
    refine (Places.Month(first.Year, first.Month)) model

/// The capability each command needs (3), and where a refusal is shown.
/// Typing into a draft or moving between screens needs none.
let requirement (name: string) (key: string option) : (Access.Capability * Form) option =
    match name with
    | "startTimer"
    | "pauseTimer"
    | "resumeTimer"
    | "stopTimer" -> Some(Access.RecordOwnTime, TimerForm)
    | "saveCompletion" -> Some(Access.RecordOwnTime, CompletionForm)
    | "saveManual" -> Some(Access.RecordOwnTime, ManualForm)
    | "addProject" -> Some(Access.ManageProjects, ReferenceForm)
    | "addActivityType" -> Some(Access.ManageActivityTypes, ReferenceForm)
    | "addTag" -> Some(Access.ManageTags, ReferenceForm)
    | "referenceActive" ->
        match (defaultArg key "").Split(':', 2) |> Array.tryHead |> Option.bind kindOf with
        | Some Reference.ActivityType -> Some(Access.ManageActivityTypes, ReferenceForm)
        | Some Reference.Tag -> Some(Access.ManageTags, ReferenceForm)
        | _ -> Some(Access.ManageProjects, ReferenceForm)
    | "saveAmend" -> Some(Access.AmendOwnTime, AmendForm)
    | "voidActivity"
    | "restoreActivity" -> Some(Access.AmendOwnTime, VoidForm)
    | "saveSplit" -> Some(Access.AmendOwnTime, SplitForm)
    | "attachEvidence"
    | "unlinkEvidence" -> Some(Access.AmendOwnTime, EvidenceForm)
    | "saveMerge" -> Some(Access.AmendOwnTime, MergeForm)
    | "attestDay" -> Some(Access.AttestOwnDay, AttestForm)
    | "admitMember"
    | "changeMemberAccess"
    | "removeMember" -> Some(Access.ManageOrganizationSettings, MemberForm)
    | "periodCadence"
    | "periodWeekStart"
    | "periodSubmissionExpected"
    | "periodApprovalRequired"
    | "savePeriodSettings" -> Some(Access.ManageOrganizationSettings, PeriodForm)
    | "submitPeriod" -> Some(Access.SubmitOwnTime, ReviewForm)
    | "approveSubmission" -> Some(Access.ApproveTime, ReviewForm)
    | "rejectSubmission" -> Some(Access.RejectTime, ReviewForm)
    | "acceptCandidate"
    | "rejectCandidate" -> Some(Access.RecordOwnTime, CandidateForm)
    | "copyExport"
    | "printReport" -> Some(Access.ExportTime, ExportForm)
    | "acceptOutsideEdit" -> Some(Access.AmendOwnTime, OutsideEditForm)
    | "rebuildIndex" -> Some(Access.ManageOrganizationSettings, IndexForm)
    | _ -> None

/// Runs a command only when the person holds what it needs; otherwise the
/// stable refusal is shown where the command was made, and nothing changes.
let internal authorized (name: string) (key: string option) (model: Model) (run: unit -> Model * Effect list) =
    match requirement name key with
    | None -> run ()
    | Some(capability, form) ->
        match Access.authorize model.Roster model.Session.OrganizationId model.Session.ActorId capability with
        | Ok() -> run ()
        | Error refusal -> withProblems form [ refusal ] model, []

let internal onEvent (ctx: Ctx) (name: string) (key: string option) (value: string) (isChecked: bool option) (model: Model) =
    let checkedOn = isChecked |> Option.defaultValue false

    let dayProject =
        match model.Place with
        | Places.Day(_, project) -> project
        | _ -> None

    let weekProject =
        match model.Place with
        | Places.Week(_, project) -> project
        | _ -> None

    // The report form changed: the address follows, replacing the entry.
    let report (change: ReportDraft -> ReportDraft) =
        let next = { model with Report = { change model.Report with GeneratedAt = Some ctx.Now } }
        refine (Places.Reports(queryOf next.Report)) next

    match name with
    | "goToday" -> navigate Places.Today model
    | "goTrack" -> navigate Places.Track model
    | "goMore" -> navigate (Places.Settings None) model
    | "copyLink" ->
        let place = Places.explicit (Model.today model) model.Place |> normalize model

        match model.RouteProblem, Places.format (addressOf model place) with
        | None, Ok location ->
            let link = Places.share model.Page.Origin model.Page.Path location
            { model with LinkStatus = ""; LinkText = link }, [ CopyText(LinkCopy, link) ]
        | _ -> model, []
    | "dayProject" ->
        let project = if value = "" then None else Some value

        match model.Place with
        | Places.Today when project.IsNone -> model, []
        | _ -> refine (Places.Day(selectedDate model, project)) model
    | "keepStored" -> keepStored (defaultArg key value) model
    | "retryChange" -> retryChange ctx (defaultArg key value) model
    | "redoChange" -> redoChange (defaultArg key "") value model
    | "acceptOutsideEdit" -> acceptOutsideEdit ctx (defaultArg key value) model
    | "rebuildIndex" ->
        match model.Store.Kind with
        | Durable _ ->
            { clear IndexForm model with
                Store = { model.Store with Index = "Rebuilding the activity index from the records…" } },
            [ RebuildIndex ]
        | InMemory -> model, []
    | "showDate" ->
        match Format.parseIsoDate value with
        | Some date -> refine (Places.Day(date, dayProject)) model
        | None -> model, []
    | "previousWeek" -> refine (Places.Week((weekOf model (selectedDate model)).Start.AddDays -7, weekProject)) model
    | "nextWeek" -> refine (Places.Week((weekOf model (selectedDate model)).Start.AddDays 7, weekProject)) model
    | "weekProject" -> refine (Places.Week((weekOf model (selectedDate model)).Start, (if value = "" then None else Some value))) model
    | "previousPeriod" -> refine (Places.Period (Periods.previous model.PeriodConfig (Periods.containing model.PeriodConfig (selectedDate model))).Start) model
    | "nextPeriod" -> refine (Places.Period (Periods.next model.PeriodConfig (Periods.containing model.PeriodConfig (selectedDate model))).Start) model
    | "previousDay" -> refine (Places.Day((selectedDate model).AddDays -1, dayProject)) model
    | "nextDay" -> refine (Places.Day((selectedDate model).AddDays 1, dayProject)) model

    | "timerActivityType" -> timerDraft (fun d -> { d with ActivityTypeId = value }) model
    | "timerProject" -> timerDraft (fun d -> { d with ProjectId = value }) model
    | "timerDescription" -> timerDraft (fun d -> { d with Description = value }) model
    | "timerUseRecent" -> timerUseRecent key model
    | "manualUseRecent" -> manualUseRecent key model
    | "manualDuration" -> manualDuration key model
    | "copyActivity" -> copyActivity (defaultArg key (model.Detail |> Option.map _.ActivityId |> Option.defaultValue value)) model
    | "startTimer" -> startTimer ctx model
    | "pauseTimer" -> pauseTimer ctx model
    | "resumeTimer" -> resumeTimer ctx model
    | "stopTimer" -> stopTimer ctx model

    | "completeActivityType" -> completionDraft (fun d -> { d with ActivityTypeId = value }) model
    | "completeProject" -> completionDraft (fun d -> { d with ProjectId = value }) model
    | "completeDescription" -> completionDraft (fun d -> { d with Description = value }) model
    | "completePurpose" -> completionDraft (fun d -> { d with BusinessPurpose = value }) model
    | "completeTag" -> completionDraft (fun d -> { d with Tags = toggle (defaultArg key "") checkedOn d.Tags }) model
    | "confirmLongTimer" -> { model with LongTimerConfirmed = checkedOn }, []
    | "saveCompletion" -> saveCompletion ctx model

    | "manualActivityType" -> manualDraft (fun d -> { d with ActivityTypeId = value }) model
    | "manualProject" -> manualDraft (fun d -> { d with ProjectId = value }) model
    | "manualDescription" -> manualDraft (fun d -> { d with Description = value }) model
    | "manualPurpose" -> manualDraft (fun d -> { d with BusinessPurpose = value }) model
    | "manualTag" -> manualDraft (fun d -> { d with Tags = toggle (defaultArg key "") checkedOn d.Tags }) model
    | "manualStartDate" -> manual (fun d -> { d with StartDate = value; EndDate = if d.EndDate = "" then value else d.EndDate }) model
    | "manualStartTime" -> manual (fun d -> { d with StartTime = value }) model
    | "manualEndDate" -> manual (fun d -> { d with EndDate = value }) model
    | "manualEndTime" -> manual (fun d -> { d with EndTime = value }) model
    | "manualReason" -> manual (fun d -> { d with Reason = value }) model
    | "saveManual" -> saveManual ctx model

    | "newProjectName" -> { model with NewNames = model.NewNames.Add(Reference.Project, value) }, []
    | "newActivityTypeName" -> { model with NewNames = model.NewNames.Add(Reference.ActivityType, value) }, []
    | "newTagName" -> { model with NewNames = model.NewNames.Add(Reference.Tag, value) }, []
    | "addProject" -> addReference ctx Reference.Project model
    | "addActivityType" -> addReference ctx Reference.ActivityType model
    | "addTag" -> addReference ctx Reference.Tag model
    | "referenceActive" -> setActive ctx (defaultArg key "") checkedOn model

    | "openReview" ->
        match model.Place with
        | Places.Today -> navigate Places.ReviewToday model
        | _ -> navigate (Places.Review(selectedDate model)) model
    | "previousMonth" -> month -1 model
    | "nextMonth" -> month 1 model
    | "showMonth" ->
        // From the month picker (its value) or a month in the history (its key).
        let month = if String.IsNullOrEmpty value then defaultArg key "" else value

        match Format.parseIsoDate $"{month}-01" with
        | Some first -> refine (Places.Month(first.Year, first.Month)) model
        | None -> model, []

    | "amendActivityType" -> amendDraft (fun d -> { d with ActivityTypeId = value }) model
    | "amendProject" -> amendDraft (fun d -> { d with ProjectId = value }) model
    | "amendDescription" -> amendDraft (fun d -> { d with Description = value }) model
    | "amendPurpose" -> amendDraft (fun d -> { d with BusinessPurpose = value }) model
    | "amendReason" -> detail (fun d -> { d with AmendReason = value }) model
    | "saveAmend" -> saveAmend ctx model
    | "voidReason" -> detail (fun d -> { d with VoidReason = value }) model
    | "voidActivity" -> voidActivity ctx model
    | "restoreActivity" -> restoreActivity ctx model
    | "splitFirst" -> detail (fun d -> { d with SplitFirst = value }) model
    | "splitSecond" -> detail (fun d -> { d with SplitSecond = value }) model
    | "splitEvidence" -> detail (fun d -> { d with SplitEvidence = d.SplitEvidence.Add(defaultArg key "", minutesOf value) }) model
    | "saveSplit" -> saveSplit ctx model
    | "evidenceKind" -> detail (fun d -> { d with EvidenceKind = value }) model
    | "evidenceUrl" -> detail (fun d -> { d with EvidenceUrl = value }) model
    | "evidenceLabel" -> detail (fun d -> { d with EvidenceLabel = value }) model
    | "evidenceSource" -> detail (fun d -> { d with EvidenceSource = value }) model
    | "evidenceNotes" -> detail (fun d -> { d with EvidenceNotes = value }) model
    | "attachEvidence" -> attachEvidence ctx model
    | "unlinkEvidence" -> unlinkEvidence ctx (defaultArg key "") model

    | "mergeSelect" -> { model with MergeSelection = toggle (defaultArg key "") checkedOn model.MergeSelection }, []
    | "mergeActivityType" -> mergeDraft (fun d -> { d with ActivityTypeId = value }) model
    | "mergeProject" -> mergeDraft (fun d -> { d with ProjectId = value }) model
    | "mergeDescription" -> mergeDraft (fun d -> { d with Description = value }) model
    | "mergePurpose" -> mergeDraft (fun d -> { d with BusinessPurpose = value }) model
    | "saveMerge" -> saveMerge ctx model

    | "resolveObligation" ->
        // The obligation's id says where it is resolved (ProjectObligations.obligations).
        match (defaultArg key "").Split('-', 2) with
        | [| "stopped"; _ |] -> navigate Places.Track model
        | [| "attestation"; date |] ->
            match Format.parseIsoDate date with
            | Some on -> navigate (Places.Review on) model
            | None -> invalidArg (nameof key) $"Unknown obligation: {key}"
        | [| "store" |] -> navigate (Places.Settings(Some Places.Changes)) model
        | [| "period"; _ |] -> navigate Places.ThisPeriod model
        | [| "candidates"; _ |] -> navigate (Places.Candidates Places.awaitingCandidates) model
        | [| "inbox"; _ |] -> navigate (Places.Settings(Some Places.Inboxes)) model
        | [| "approval"; submissionId |] ->
            match PeriodReview.awaitingApproval model.PeriodConfig.ApprovalRequired model.Reviews |> List.tryFind (fun s -> s.SubmissionId = submissionId) with
            | Some submission -> navigate (Places.Period submission.Period.Start) model
            | None -> navigate Places.ThisPeriod model
        | _ -> invalidArg (nameof key) $"Unknown obligation: {key}"
    | "periodCadence" ->
        let cadence =
            match value with
            | "daily" -> Periods.Daily
            | "weekly" -> Periods.Weekly
            // A fortnight counted from the week that contains today.
            | "biweekly" -> Periods.Biweekly((Periods.containing { model.PeriodConfig with Cadence = Periods.Weekly } (Model.today model)).Start)
            | "semimonthly" -> Periods.SemiMonthly
            | "monthly" -> Periods.Monthly
            | other -> invalidArg (nameof value) $"Unknown cadence: {other}"

        { clear PeriodForm model with PeriodConfig = { model.PeriodConfig with Cadence = cadence } }, []
    | "periodWeekStart" ->
        match Enum.TryParse<DayOfWeek>(value) with
        | true, day -> { clear PeriodForm model with PeriodConfig = { model.PeriodConfig with WeekStart = day } }, []
        | _ -> invalidArg (nameof value) $"Unknown day: {value}"
    | "periodSubmissionExpected" -> { clear PeriodForm model with PeriodConfig = { model.PeriodConfig with SubmissionExpected = checkedOn } }, []
    | "periodApprovalRequired" -> { clear PeriodForm model with PeriodConfig = { model.PeriodConfig with ApprovalRequired = checkedOn } }, []
    | "savePeriodSettings" -> savePeriodSettings ctx model
    | "submitPeriod" -> submitPeriod ctx model
    | "reopenReason" -> { model with ReopenReason = value }, []
    | "candidateActivityType" -> { model with CandidateDraft = { model.CandidateDraft with ActivityTypeId = value } }, []
    | "candidateProject" -> { model with CandidateDraft = { model.CandidateDraft with ProjectId = value } }, []
    | "candidateDescription" -> { model with CandidateDraft = { model.CandidateDraft with Description = value } }, []
    | "candidatePurpose" -> { model with CandidateDraft = { model.CandidateDraft with BusinessPurpose = value } }, []
    | "candidateReason" -> { model with CandidateReason = value }, []
    | "acceptCandidate" -> acceptCandidate ctx model
    | "rejectCandidate" -> rejectCandidate ctx model
    | "readInboxes" ->
        match model.Store.Kind with
        | Durable _ -> { model with Inbox = { model.Inbox with Running = true } }, [ ReadInboxes ]
        | InMemory -> model, []
    | "reopenPeriod" -> reopenPeriod ctx model
    | "reviewNote" -> { model with ReviewNotes = model.ReviewNotes.Add(defaultArg key "", value) }, []
    | "approveSubmission" -> decideSubmission ctx true (defaultArg key "") model
    | "rejectSubmission" -> decideSubmission ctx false (defaultArg key "") model
    | "reportFrom" -> report (fun r -> { r with From = value })
    | "reportTo" -> report (fun r -> { r with To = value })
    | "reportProject" -> report (fun r -> { r with ProjectId = value })
    | "reportActivityType" -> report (fun r -> { r with ActivityTypeId = value })
    | "reportTag" -> report (fun r -> { r with Tag = value })
    | "reportMethod" -> report (fun r -> { r with Method = value })
    | "reportBillability" -> report (fun r -> { r with Billability = value })
    | "reportText" -> report (fun r -> { r with Text = value })
    | "reportIncludeRemoved" -> report (fun r -> { r with IncludeRemoved = checkedOn })
    | "reportPeople" -> report (fun r -> { r with People = value; Person = if value = "everyone" then r.Person else "" })
    | "reportPerson" -> report (fun r -> { r with Person = value })
    | "reportOrganizations" -> report (fun r -> { r with Organizations = value })
    | "reportGrouping" -> report (fun r -> { r with Grouping = value })
    | "reportFormat" ->
        let next, effects = report (fun r -> { r with Format = value })
        { next with CopyStatus = "" }, effects
    | "copyExport" -> { clear ExportForm model with CopyStatus = "" }, [ CopyText(ExportCopy, exportText model) ]
    | "printReport" -> clear ExportForm model, [ Print ]
    | "goReports" -> navigate (Places.Reports Places.allReports) model
    | "reportMonth" ->
        // The month's report, its dates in the address.
        let first = selectedMonth model
        navigate (Places.Reports { Places.allReports with From = Some first; To = Some(first.AddMonths(1).AddDays -1) }) model
    | "memberId" -> { model with MemberDraft = { model.MemberDraft with Id = value } }, []
    | "memberName" -> { model with MemberDraft = { model.MemberDraft with Name = value } }, []
    | "memberAccess" -> { model with MemberDraft = { model.MemberDraft with Access = value } }, []
    | "admitMember" -> admitMember ctx model
    | "changeMemberAccess" -> changeMemberAccess ctx (defaultArg key "") value model
    | "removeMember" -> removeMember ctx (defaultArg key "") model
    | "attestStatement" -> { model with AttestStatement = value }, []
    | "attestDay" -> attestDay ctx model

    // The page and the engine disagree: a defect, not an operational failure.
    | other -> invalidArg (nameof name) $"Unknown event name: {other}"
