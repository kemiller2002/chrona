/// Reports (WI-0039, WI-0075): the form, the result and its export.
module Chrona.Engine.App.ProjectReports

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

let internal reportView (model: Model) =
    let filter = UpdateReports.reportFilter model
    let draft = model.Report
    let all = UpdateReports.reportActivities model
    let policies = UpdateReports.reportPolicies model
    let rows = Reports.select filter all
    let totals = Reports.totals policies rows

    // Across organizations (WI-0075): each organization names its own
    // reference data and people.
    let across = UpdateReports.acrossOrganizations model
    let elsewhere = model.Elsewhere |> Map.toList |> List.map snd

    let organizationName (id: string) =
        model.Deployment
        |> Option.bind (fun deployment -> deployment.Organizations |> List.tryFind (fun o -> o.Id = id))
        |> Option.map _.DisplayName
        |> Option.defaultValue id

    let referenceName (model: Model) (kind: Reference.Kind) (id: string) =
        match Reference.tryName kind id model.References with
        | Some name -> name
        | None ->
            elsewhere
            |> List.tryPick (fun read -> read.References |> List.tryFind (fun item -> item.Kind = kind && item.Id = id) |> Option.map _.Name)
            |> Option.defaultValue id

    let personName (model: Model) (actorId: string) =
        match model.Roster.Members.TryFind actorId with
        | Some found -> found.Principal.DisplayName
        | None ->
            elsewhere
            |> List.tryPick (fun read -> read.Members |> List.tryFind (fun m -> m.Principal.PrincipalId = actorId) |> Option.map _.Principal.DisplayName)
            |> Option.defaultValue actorId

    let choice (id: string) (name: string) (current: string) = [ t "id" id; t "name" name; f "selected" (id = current) ]

    let anyOf (kind: Reference.Kind) (current: string) =
        choice "" "Any" current
        :: (Reference.all kind model.References |> List.map (fun item -> choice item.Id item.Name current))

    // Everyone's time, for someone who may see the organization's (WI-0039).
    let everyone = draft.People = "everyone" && UpdateReports.seesOrganization model

    let grouping, groupName =
        match draft.Grouping with
        | "person" -> Reports.ByActor, personName model
        | "organization" -> Reports.ByOrganization, organizationName
        | "activityType" -> Reports.ByActivityType, (fun id -> referenceName model Reference.ActivityType id)
        | "tag" -> Reports.ByTag, (fun id -> if id = "" then "No tag" else referenceName model Reference.Tag id)
        | "day" -> Reports.ByDay, (fun id -> Format.parseIsoDate id |> Option.map Format.longDate |> Option.defaultValue id)
        | _ -> Reports.ByProject, (fun id -> referenceName model Reference.Project id)

    let groups =
        Reports.groupBy policies grouping rows
        |> List.map (fun (key, sum) ->
            [ t "id" (if key = "" then "(none)" else key)
              t "name" (groupName key)
              t "exact" (Format.minutes sum.ExactMinutes)
              t "billable" (Format.minutes sum.BillableMinutes)
              t "count" (string sum.Count) ])

    let rowItems =
        rows
        |> List.map (fun a ->
            let times =
                match interval a with
                | Some(start, finish) -> $"{Format.clock start} – {Format.clock finish}"
                | None -> ""

            [ t "id" a.ActivityId
              t "date" (Format.longDate a.Occurrence.LocalDate)
              t "time" times
              t "title" a.Classification.Description
              t
                  "classification"
                  ((if across then $"{organizationName a.OrganizationId} · " else "")
                   + (if everyone then $"{personName model a.ActorId} · " else "")
                   + $"{referenceName model Reference.ActivityType a.Classification.ActivityTypeId} · {referenceName model Reference.Project a.Classification.ProjectId}")
              t "method" (methodLabel a.EntryMethod)
              t "exact" (Format.minutes a.Minutes)
              t "billed" (Format.minutes (Reports.billed policies a))
              t "state" (match a.Record with Voided _ -> "Removed from totals" | _ -> "Recorded") ])

    let range = $"{Format.longDate filter.From} to {Format.longDate filter.To}"

    [ flag "screenReports" (shows model (function Places.Reports _ -> true | _ -> false) && canWork model)
      text "reportFrom" (Format.isoDate filter.From)
      text "reportTo" (Format.isoDate filter.To)
      text "reportText" draft.Text
      text "reportRange" range
      flag "reportIncludeRemoved" draft.IncludeRemoved
      items "reportProjectOptions" (anyOf Reference.Project draft.ProjectId)
      items "reportTypeOptions" (anyOf Reference.ActivityType draft.ActivityTypeId)
      items "reportTagOptions" (anyOf Reference.Tag draft.Tag)
      items "reportMethodOptions" [ choice "" "Any" draft.Method; choice "manual" "Manual entry" draft.Method; choice "timer" "Timer" draft.Method ]
      items "reportBillabilityOptions" [ choice "" "Any" draft.Billability; choice "billable" "Billable" draft.Billability; choice "non-billable" "Non-billable" draft.Billability; choice "pending" "Not yet classified" draft.Billability ]
      flag "reportSeesOrganization" (UpdateReports.seesOrganization model)
      items "reportPeopleOptions" [ choice "mine" "Mine" draft.People; choice "everyone" "Everyone's" draft.People ]
      flag "reportChoosesPerson" everyone
      items
          "reportPersonOptions"
          (choice "" "Everyone" draft.Person
           :: (UpdateReports.reportPeople model |> List.map (fun id -> choice id (personName model id) draft.Person)))
      items
          "reportGroupingOptions"
          [ choice "project" "Project" draft.Grouping
            choice "activityType" "Activity type" draft.Grouping
            choice "tag" "Tag" draft.Grouping
            choice "day" "Day" draft.Grouping
            if everyone then
                choice "person" "Person" draft.Grouping
            if across then
                choice "organization" "Organization" draft.Grouping ]
      // Several organizations in the deployment: a report may cover all of
      // those the person belongs to (WI-0075).
      flag "reportHasOrganizations" (not (UpdateReports.otherOrganizations model).IsEmpty)
      items
          "reportOrganizationsOptions"
          [ choice "this" (organizationName model.Session.OrganizationId) draft.Organizations
            choice "all" "All my organizations" draft.Organizations ]
      flag "hasReportOrganizationsNote" (across && elsewhere |> List.exists (fun read -> read.Failure.IsSome || not read.Member || (everyone && not read.SeesOrganization)))
      text
          "reportOrganizationsNote"
          (if not across then
               ""
           else
               [ for read in elsewhere do
                     if read.Failure.IsSome then
                         $"{organizationName read.OrganizationId} could not be read: {read.Failure.Value}"
                     elif not read.Member then
                         $"You are not a member of {organizationName read.OrganizationId}, so none of its time is here."
                     elif everyone && not read.SeesOrganization then
                         $"In {organizationName read.OrganizationId} you may see only your own time, so only yours is here." ]
               |> String.concat " ")
      items "reportFormatOptions" [ choice "csv" "CSV" draft.Format; choice "json" "JSON" draft.Format ]
      text "reportCount" (plural totals.Count "activity" "activities")
      text "reportExact" (Format.minutes totals.ExactMinutes)
      text "reportBillable" (Format.minutes totals.BillableMinutes)
      text "reportNonBillable" (Format.minutes totals.NonBillableMinutes)
      text "reportTimerManual" $"{Format.minutes totals.TimerMinutes} / {Format.minutes totals.ManualMinutes}"
      text "reportApproved" $"{Format.minutes totals.ApprovedMinutes} / {Format.minutes totals.UnapprovedMinutes}"
      text "reportCorrections" $"{totals.AmendedAfterReview} / {totals.CorrectedAfterPublication}"
      flag "reportEmpty" rows.IsEmpty
      items "reportRows" rowItems
      items "reportGroups" groups
      text "exportText" (UpdateReports.exportText model)
      text "copyStatus" model.CopyStatus
      flag "hasExportProblems" (model.Problems.ContainsKey ExportForm)
      items "exportProblems" (problemItems model ExportForm)
      text "printTitle" "Time report"
      text
          "printSubtitle"
          (match everyone, draft.Person with
           | true, "" when across -> $"Everyone in all my organizations · {range}"
           | true, "" -> $"Everyone in the organization · {range}"
           | true, person -> $"{personName model person} · {range}"
           | false, _ -> $"{model.Session.DisplayName} · {range}")
      text "printGenerated" $"Generated {localStamp model model.Now} · {Reports.Schema}" ]
