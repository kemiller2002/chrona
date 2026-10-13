/// The week, a period and projects (CHX-460).
module Chrona.Engine.App.ProjectWeeks

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Engine.View
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

/// The person's own records that were read.
let internal mine (model: Model) =
    model.Ledger.Activities |> Map.toList |> List.map snd |> List.filter (fun a -> a.ActorId = model.Session.ActorId)

/// One row per day from `first` to `last`: its counted time, linked to the
/// day's ledger (with the same project filter).
let internal dayRows (model: Model) (project: string option) (first: DateOnly) (last: DateOnly) (counted: Activity list) =
    [ for offset in 0 .. last.DayNumber - first.DayNumber do
          let date = first.AddDays offset
          let onDay = counted |> List.filter (fun a -> a.Occurrence.LocalDate = date)

          [ t "id" (Format.isoDate date)
            t "label" (Format.longDate date)
            t "total" (Format.minutes (onDay |> List.sumBy _.Minutes))
            t "count" (plural onDay.Length "entry" "entries")
            t "href" (hrefOf model (Places.Day(date, project))) ] ]

let internal projectOptions (model: Model) (project: string option) =
    [ t "id" ""; t "name" "All projects"; f "selected" project.IsNone ]
    :: (Reference.all Reference.Project model.References
        |> List.map (fun item -> [ t "id" item.Id; t "name" item.Name; f "selected" (Some item.Id = project) ]))

let internal weekView (model: Model) =
    let on, project =
        match model.Place with
        | Places.Week(on, project) -> on, project
        | _ -> Model.today model, None

    let week = weekOf model on

    let counted =
        mine model
        |> List.filter (fun a -> consumesTime a && Periods.contains week a.Occurrence.LocalDate && project |> Option.forall ((=) a.Classification.ProjectId))

    let total = counted |> List.sumBy _.Minutes
    let billed = counted |> List.sumBy (fun a -> Billing.billableMinutes (billing model) a.Minutes)

    [ flag "screenWeek" (shows model (function Places.ThisWeek | Places.Week _ -> true | _ -> false))
      text "weekTitle" (periodLabel week)
      text "weekTotal" (Format.minutes total)
      text "weekBilled" (Format.minutes billed)
      text "weekDecimal" (Format.decimalHours total)
      text "weekEntryCount" (plural counted.Length "entry" "entries")
      items "weekDays" (dayRows model project week.Start week.Finish counted)
      items "weekProjectOptions" (projectOptions model project)
      flag "weekFiltered" project.IsSome
      text "weekFilterName" (project |> Option.map (referenceName model Reference.Project) |> Option.defaultValue "") ]

let internal cadenceLabel =
    function
    | Periods.Daily -> "Daily"
    | Periods.Weekly -> "Weekly"
    | Periods.Biweekly _ -> "Every two weeks"
    | Periods.SemiMonthly -> "Twice a month"
    | Periods.Monthly -> "Monthly"

/// The person's own period as reviewed, in words (14).
let internal reviewStateText (model: Model) (state: PeriodReview.PeriodState) =
    match state with
    | PeriodReview.Open -> "Open: not submitted."
    | PeriodReview.AwaitingApproval submission ->
        $"Submitted {localStamp model submission.At}; waiting for approval. Its time cannot be changed until it is approved, returned or reopened."
    | PeriodReview.Closed({ Kind = PeriodReview.Approval } as approval) ->
        let note = approval.Note |> Option.map (fun note -> $" \"{note}\"") |> Option.defaultValue ""
        $"Approved by {personName model approval.By} {localStamp model approval.At}.{note} Closed: reopen it to change its time."
    | PeriodReview.Closed submission -> $"Submitted {localStamp model submission.At}. Closed: reopen it to change its time."
    | PeriodReview.Returned rejection ->
        $"Returned by {personName model rejection.By} {localStamp model rejection.At}: {defaultArg rejection.Note String.Empty} Correct it and submit it again."

/// A timesheet period's own page: its summary and its days (15).
let internal periodPage (model: Model) =
    let on =
        match model.Place with
        | Places.Period on -> on
        | _ -> Model.today model

    let period = Periods.containing model.PeriodConfig on
    let summary = Periods.summarize model.PeriodConfig [ billing model ] (Model.today model) (mine model) period
    let counted = mine model |> List.filter (fun a -> consumesTime a && Periods.contains period a.Occurrence.LocalDate)
    let state = PeriodReview.stateOf model.PeriodConfig.ApprovalRequired model.Reviews model.Session.ActorId period

    [ flag "screenPeriod" (shows model (function Places.ThisPeriod | Places.Period _ -> true | _ -> false))
      text "periodPageTitle" (periodLabel period)
      text "periodPageCadence" $"{cadenceLabel model.PeriodConfig.Cadence} timesheet period"
      text "periodPageExact" (Format.minutes summary.ExactMinutes)
      text "periodPageBillable" (Format.minutes summary.BillableMinutes)
      text "periodPageNonBillable" (Format.minutes summary.NonBillableMinutes)
      text "periodPageUnclassified" (Format.minutes summary.UnclassifiedMinutes)
      text "periodPageSubmission" (submissionText summary.Submission)
      text "periodPageApproval" (approvalText summary.Approval)
      items "periodDays" (dayRows model None period.Start period.Finish counted)
      // Submitting, reopening and approving (WI-0036).
      text "periodReviewState" (reviewStateText model state)
      flag "canSubmitPeriod" (not (PeriodReview.isHeld state) && not counted.IsEmpty && permits model Access.SubmitOwnTime)
      flag "periodHeld" (PeriodReview.isHeld state)
      text "reopenReason" model.ReopenReason
      flag "hasReviewProblems" (model.Problems.ContainsKey ReviewForm)
      items "reviewProblems" (problemItems model ReviewForm)
      flag "hasApprovals" (permits model Access.ApproveTime && not (awaitingMine model).IsEmpty)
      items
          "approvals"
          [ for submission in awaitingMine model do
                let covered = submission.Covered |> List.map fst |> Set.ofList
                let theirs = model.Reviewing |> List.filter (fun a -> covered.Contains a.ActivityId)

                [ t "id" submission.SubmissionId
                  t "who" (personName model submission.ActorId)
                  t "period" (periodLabel submission.Period)
                  t "total" (Format.minutes (theirs |> List.sumBy _.Minutes))
                  t "count" (activities theirs.Length)
                  t "note" (model.ReviewNotes.TryFind submission.SubmissionId |> Option.defaultValue "")
                  f "cannotReject" (not (permits model Access.RejectTime)) ] ] ]

let internal projectState (item: Reference.Item) =
    if item.Status = Reference.Active then "Offered for new work" else "Archived: kept on past records"

let internal projectsView (model: Model) =
    let projects = Reference.all Reference.Project model.References

    [ flag "screenProjects" (shows model ((=) Places.Projects))
      flag "projectsEmpty" projects.IsEmpty
      items
          "projectList"
          (projects
           |> List.map (fun item -> [ t "id" item.Id; t "name" item.Name; t "state" (projectState item); t "href" (hrefOf model (Places.Project item.Id)) ])) ]

/// One project: the person's time on it, and where to look further.
let internal projectView (model: Model) =
    let id =
        match model.Place with
        | Places.Project id -> id
        | _ -> ""

    let item = Reference.all Reference.Project model.References |> List.tryFind (fun item -> item.Id = id)

    let entries =
        mine model
        |> List.filter (fun a -> a.Classification.ProjectId = id && (match a.Record with Superseded _ -> false | _ -> true))
        |> List.sortByDescending (fun a -> a.Occurrence.LocalDate, a.Occurrence.LocalTime, a.ActivityId)

    let counted = entries |> List.filter consumesTime
    let total = counted |> List.sumBy _.Minutes
    let today = Model.today model

    [ flag "screenProject" (shows model (function Places.Project _ -> true | _ -> false))
      text "projectName" (item |> Option.map _.Name |> Option.defaultValue "")
      text "projectState" (item |> Option.map projectState |> Option.defaultValue "")
      text "projectTotal" (Format.minutes total)
      text "projectBilled" (Format.minutes (counted |> List.sumBy (fun a -> Billing.billableMinutes (billing model) a.Minutes)))
      text "projectEntryCount" (plural counted.Length "entry" "entries")
      flag "projectEmpty" (entries.IsEmpty && model.Store.Reading.IsEmpty)
      flag "projectReading" (not model.Store.Reading.IsEmpty)
      items
          "projectEntries"
          (entries
           |> List.truncate 50
           |> List.map (fun a ->
               [ t "id" a.ActivityId
                 t "detail" (Format.longDate a.Occurrence.LocalDate + (match a.Record with Voided _ -> " · removed from totals" | _ -> ""))
                 t "title" a.Classification.Description
                 t "duration" (Format.minutes a.Minutes)
                 t "href" (hrefOf model (Places.Entry(a.ActivityId, Some a.Occurrence.LocalDate))) ]))
      text "projectTodayHref" (hrefOf model (Places.Day(today, Some id)))
      text "projectWeekHref" (hrefOf model (Places.Week((weekOf model today).Start, Some id)))
      text "projectReportHref" (hrefOf model (Places.Reports { Places.allReports with ProjectId = Some id })) ]
