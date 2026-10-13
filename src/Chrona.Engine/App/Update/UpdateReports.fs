/// Reports (WI-0039, WI-0075): the form and its address, whose time and
/// which organizations a report covers, and its export.
module Chrona.Engine.App.UpdateReports

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

/// The report form for an address's filters: its dates as the form holds
/// them, an absent filter as "any".
let draftOf (query: Places.ReportQuery) (draft: ReportDraft) =
    let iso = Option.map Format.isoDate >> Option.defaultValue ""
    let text = Option.defaultValue ""

    { draft with
        From = iso query.From
        To = iso query.To
        ProjectId = text query.ProjectId
        ActivityTypeId = text query.ActivityTypeId
        Tag = text query.Tag
        Method = text query.Method
        Billability = text query.Billability
        Text = text query.Text
        IncludeRemoved = query.IncludeRemoved
        People = query.People
        Person = text query.Person
        Organizations = query.Organizations
        Grouping = query.Grouping
        Format = query.Format }

/// The address's filters for a report form.
let queryOf (draft: ReportDraft) : Places.ReportQuery =
    let some (value: string) = if value = "" then None else Some value

    { From = Format.parseIsoDate draft.From
      To = Format.parseIsoDate draft.To
      ProjectId = some draft.ProjectId
      ActivityTypeId = some draft.ActivityTypeId
      Tag = some draft.Tag
      Method = some draft.Method
      Billability = some draft.Billability
      Text = some draft.Text
      IncludeRemoved = draft.IncludeRemoved
      People = draft.People
      // One person is chosen only among everyone's time.
      Person = if draft.People = "everyone" then some draft.Person else None
      Organizations = draft.Organizations
      Grouping = draft.Grouping
      Format = draft.Format }

/// Whether the person may see the organization's time, and so report on
/// everyone's (WI-0039).
let seesOrganization (model: Model) =
    Access.permits model.Roster model.Session.ActorId Access.ViewOrganizationTime

/// The people whose time a report across everyone covers: the
/// organization's members, and anyone the activity index records time for
/// (someone who has left still has time in it).
let reportPeople (model: Model) =
    (model.Roster.Members |> Map.toList |> List.map fst)
    @ (model.Store.History |> List.map _.ActorId)
    |> List.distinct
    |> List.sort

/// The days a report covers: the dates its form names, or this month's
/// (`Places.defaultRange`).
let reportRange (model: Model) =
    let first, last = Places.defaultRange (Model.today model)

    Format.parseIsoDate model.Report.From |> Option.defaultValue first,
    Format.parseIsoDate model.Report.To |> Option.defaultValue last

/// Every (year, month) from the first day's month to the last day's.
let internal monthsBetween (first: DateOnly) (last: DateOnly) =
    let start = DateOnly(first.Year, first.Month, 1)

    List.unfold (fun (month: DateOnly) -> if month > last then None else Some((month.Year, month.Month), month.AddMonths 1)) start

/// The months of a report's range that hold someone's time: where the
/// activity index is current, the months it lists for them; otherwise
/// every month of the range, so nothing stored is left out (WI-0039).
let internal reportMonths (model: Model) (actorId: string) =
    let months = reportRange model ||> monthsBetween

    if model.Store.IndexCurrent then
        let held =
            model.Store.History
            |> List.filter (fun total -> total.ActorId = actorId)
            |> List.map (fun total -> total.Year, total.Month)

        months |> List.filter (fun month -> List.contains month held)
    else
        months

/// The deployment's other organizations, in its order (WI-0075).
let otherOrganizations (model: Model) =
    model.Deployment
    |> Option.map (fun deployment -> deployment.Organizations |> List.map _.Id |> List.filter ((<>) model.Session.OrganizationId))
    |> Option.defaultValue []

/// Whether a report covers all of the deployment's organizations the person
/// belongs to (WI-0075).
let acrossOrganizations (model: Model) =
    match model.Place with
    | Places.Reports query -> query.Organizations = "all"
    | _ -> false

/// The report filter a draft names: over the session's own records, or,
/// for someone who may see the organization's time, everyone's or one
/// person's. A date the address does not name is this month's
/// (`Places.defaultRange`).
let reportFilter (model: Model) : Reports.Filter =
    let draft = model.Report
    let from, ``to`` = reportRange model
    let one (value: string) = if value = "" then [] else [ value ]

    { Reports.between from ``to`` with
        OrganizationIds =
            model.Session.OrganizationId
            :: (if acrossOrganizations model then
                    model.Elsewhere |> Map.toList |> List.filter (fun (_, read) -> read.Member) |> List.map fst
                else
                    [])
        ActorIds =
            match draft.People, draft.Person with
            | "everyone", "" when seesOrganization model -> []
            | "everyone", person when seesOrganization model -> [ person ]
            | _ -> [ model.Session.ActorId ]
        ProjectIds = one draft.ProjectId
        ActivityTypeIds = one draft.ActivityTypeId
        Tags = one draft.Tag
        EntryKinds =
            match draft.Method with
            | "manual" -> [ Reports.ManualEntries ]
            | "timer" -> [ Reports.TimerEntries ]
            | _ -> []
        Billability =
            match draft.Billability with
            | "billable" -> [ Billable ]
            | "non-billable" -> [ NonBillable ]
            | "pending" -> [ PendingClassification ]
            | _ -> []
        Text = draft.Text
        IncludeRemoved = draft.IncludeRemoved }

/// The time a report selects from: the person's own, and other people's
/// as read (the filter decides whose counts, WI-0039).
let reportActivities (model: Model) =
    (model.Ledger.Activities |> Map.toList |> List.map snd)
    @ model.Reviewing
    @ (if acrossOrganizations model then
           model.Elsewhere |> Map.toList |> List.collect (fun (_, read) -> read.Activities)
       else
           [])

/// The billing policies of every organization a report covers: each
/// organization's own (WI-0075).
let reportPolicies (model: Model) =
    (reportFilter model).OrganizationIds |> List.map Billing.legacyDefault

/// The export the report form describes, generated at the model's instant.

let exportText (model: Model) =
    let filter = reportFilter model

    let header: Reports.Header =
        { // Every organization the report covers (WI-0075).
          OrganizationId = String.Join(";", filter.OrganizationIds)
          GeneratedAt = model.Report.GeneratedAt |> Option.defaultValue model.Now
          Filter = filter }

    let all = reportActivities model
    let policies = reportPolicies model

    match model.Report.Format with
    | "json" -> Reports.json policies header all
    | _ -> Reports.csv policies header all
