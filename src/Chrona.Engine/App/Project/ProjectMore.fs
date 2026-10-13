/// More: reference data, the organization's periods and the producers'
/// inboxes.
module Chrona.Engine.App.ProjectMore

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

let internal referenceRows (model: Model) (kind: Reference.Kind) =
    Reference.all kind model.References
    |> List.map (fun item ->
        let active = item.Status = Reference.Active

        [ t "key" $"{Reference.kindName kind}:{item.Id}"
          t "name" item.Name
          f "active" active
          t "state" (if active then "Offered for new work" else "Archived: kept on past records") ])

/// The period containing the selected day, summarized (15).
let internal currentPeriod (model: Model) =
    let mine = model.Ledger.Activities |> Map.toList |> List.map snd |> List.filter (fun a -> a.ActorId = model.Session.ActorId)
    let period = Periods.containing model.PeriodConfig (selectedDate model)
    Periods.summarize model.PeriodConfig [ billing model ] (Model.today model) mine period

/// Someone on the roster by name.
let internal personName (model: Model) (actorId: string) =
    model.Roster.Members.TryFind actorId |> Option.map _.Principal.DisplayName |> Option.defaultValue actorId

/// Other people's submissions waiting for approval, oldest first (WI-0036).
let internal awaitingMine (model: Model) =
    PeriodReview.awaitingApproval model.PeriodConfig.ApprovalRequired model.Reviews
    |> List.filter (fun submission -> submission.ActorId <> model.Session.ActorId)

let internal periodLabel (period: Periods.Period) =
    let short (d: DateOnly) = d.ToString("MMM d", Globalization.CultureInfo.InvariantCulture)
    if period.Start = period.Finish then Format.longDate period.Start else $"{short period.Start} – {short period.Finish}"

let internal cadenceName =
    function
    | Periods.Daily -> "daily"
    | Periods.Weekly -> "weekly"
    | Periods.Biweekly _ -> "biweekly"
    | Periods.SemiMonthly -> "semimonthly"
    | Periods.Monthly -> "monthly"

let internal submissionText =
    function
    | Periods.NothingToSubmit -> "Nothing to submit"
    | Periods.NotSubmitted -> "Not submitted"
    | Periods.PartlySubmitted -> "Partly submitted"
    | Periods.FullySubmitted -> "Submitted"

let internal approvalText =
    function
    | Periods.ApprovalNotRequired -> "Not required"
    | Periods.NothingApproved -> "Not approved"
    | Periods.PartlyApproved -> "Partly approved"
    | Periods.FullyApproved -> "Approved"

let internal periodView (model: Model) =
    let summary = currentPeriod model
    let submission = submissionText summary.Submission
    let approval = approvalText summary.Approval

    let cadences =
        [ "daily", "Daily"; "weekly", "Weekly"; "biweekly", "Every two weeks"; "semimonthly", "Twice a month"; "monthly", "Monthly" ]
        |> List.map (fun (id, name) -> [ t "id" id; t "name" name; f "selected" (id = cadenceName model.PeriodConfig.Cadence) ])

    let days =
        [ DayOfWeek.Monday; DayOfWeek.Tuesday; DayOfWeek.Wednesday; DayOfWeek.Thursday; DayOfWeek.Friday; DayOfWeek.Saturday; DayOfWeek.Sunday ]
        |> List.map (fun d -> [ t "id" (string d); t "name" (string d); f "selected" (d = model.PeriodConfig.WeekStart) ])

    [ text "periodLabel" (periodLabel summary.Period)
      // The period's own page (CHX-460).
      text "periodHref" (hrefOf model (Places.Period summary.Period.Start))
      text "periodExact" (Format.minutes summary.ExactMinutes)
      text "periodBillable" (Format.minutes summary.BillableMinutes)
      text "periodNonBillable" (Format.minutes summary.NonBillableMinutes)
      text "periodUnclassified" (Format.minutes summary.UnclassifiedMinutes)
      text "periodSubmission" submission
      text "periodApproval" approval
      items "periodCadenceOptions" cadences
      items "periodWeekStartOptions" days
      flag "periodSubmissionExpected" model.PeriodConfig.SubmissionExpected
      flag "periodApprovalRequired" model.PeriodConfig.ApprovalRequired ]

/// What reading the producers' inboxes found (WI-0038).
let internal inboxView (model: Model) =
    let inbox = model.Inbox

    let summary =
        match inbox.Running, inbox.LastRun with
        | true, _ -> "Reading the inboxes…"
        | false, None -> "The inboxes are read each time your records open from GitHub."
        | false, Some at ->
            [ $"Last read {localStamp model at}."
              plural inbox.Received "observation was" "observations were" + " received on this page."
              if inbox.Waiting > 0 then plural inbox.Waiting "observation waits" "observations wait" + " for the next pass."
              if inbox.Failed > 0 then plural inbox.Failed "observation" "observations" + " could not be read or saved; read the inboxes again to retry." ]
            |> String.concat " "

    let durable =
        match model.Store.Kind with
        | Durable _ -> true
        | InMemory -> false

    [ text "inboxSummary" summary
      flag "hasInboxes" durable
      flag "canReadInboxes" (not inbox.Running && durable)
      text "inboxCandidatesHref" (hrefOf model (Places.Candidates Places.awaitingCandidates))
      flag "hasInboxInvalid" (not inbox.Invalid.IsEmpty)
      items
          "inboxInvalid"
          [ for source, observation, reasons in inbox.Invalid ->
                [ t "id" $"{source}/{observation}"
                  t "file" $"inbox/{source}/{observation}.json"
                  t "reasons" (String.concat "; " reasons) ] ]
      flag "hasInboxOrphaned" (not inbox.Orphaned.IsEmpty)
      items
          "inboxOrphaned"
          [ for source, observation, candidate in inbox.Orphaned ->
                [ t "id" $"{source}/{observation}"
                  t "receipt" $"records/chrona.receipt/{source}/{observation}.json"
                  t "candidate" candidate ] ] ]

let internal more (model: Model) =
    [ text "zoneId" (model.Zone |> Option.map _.Id |> Option.defaultValue "Not yet known")
      items "projects" (referenceRows model Reference.Project)
      items "activityTypes" (referenceRows model Reference.ActivityType)
      items "tags" (referenceRows model Reference.Tag)
      text "newProjectName" (model.NewNames.TryFind Reference.Project |> Option.defaultValue "")
      text "newActivityTypeName" (model.NewNames.TryFind Reference.ActivityType |> Option.defaultValue "")
      text "newTagName" (model.NewNames.TryFind Reference.Tag |> Option.defaultValue "")
      flag "hasReferenceProblems" (model.Problems.ContainsKey ReferenceForm)
      items "referenceProblems" (problemItems model ReferenceForm)
      yield! inboxView model ]
