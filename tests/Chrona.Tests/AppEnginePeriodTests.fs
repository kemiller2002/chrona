/// Timesheet periods (WI-0048), reports and export (WI-0049), and the week,
/// a period and projects (CHX-460).
[<Xunit.Collection("AppEngine")>]
module Chrona.Tests.AppEnginePeriodTests

open System
open Xunit
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.View
open Chrona.Engine.App
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update
open Chrona.Engine.App.UpdateDispatch
open Chrona.Tests.AppEngineFixture

// ---- timesheet periods (WI-0048) -------------------------------------------------------

[<Fact>]
let ``Today shows the configured period around the selected day`` () =
    Assert.Equal("Oct 5 – Oct 11", textOf "periodLabel" withThree)
    Assert.Equal("2h", textOf "periodExact" withThree)
    Assert.Equal("2h", textOf "periodBillable" withThree)
    Assert.Equal("Not submitted", textOf "periodSubmission" withThree)
    Assert.Equal("Not required", textOf "periodApproval" withThree)
    Assert.Equal("America/New_York", withThree.PeriodConfig.ZoneId)

    let sunday, _ = update (ctxAt start) (ui "periodWeekStart" "Sunday") withThree
    Assert.Equal("Oct 4 – Oct 10", textOf "periodLabel" sunday)
    let monthly, _ = update (ctxAt start) (ui "periodCadence" "monthly") withThree
    Assert.Equal("Oct 1 – Oct 31", textOf "periodLabel" monthly)
    let daily, _ = update (ctxAt start) (ui "periodCadence" "daily") withThree
    Assert.Equal("Thursday, October 8", textOf "periodLabel" daily)
    let fortnight, _ = update (ctxAt start) (ui "periodCadence" "biweekly") withThree
    Assert.Equal("Oct 5 – Oct 18", textOf "periodLabel" fortnight)
    Assert.Equal<string list>([ "biweekly" ], itemsOf "periodCadenceOptions" fortnight |> List.filter (fun r -> field "selected" r = "True") |> List.map (field "id"))

// ---- reports and export (WI-0049) ------------------------------------------------------

[<Fact>]
let ``the report filters the session's records and keeps exact and billable time apart`` () =
    let reports, _ = update (ctxAt start) (LocationMoved "#/reports") withThree
    Assert.Equal("2026-10-01", textOf "reportFrom" reports)
    Assert.Equal("2026-10-31", textOf "reportTo" reports)
    Assert.Equal("3 activities", textOf "reportCount" reports)
    Assert.Equal("2h", textOf "reportExact" reports)
    Assert.Equal("2h", textOf "reportBillable" reports)

    let searched, _ = update (ctxAt start) (ui "reportText" "SECOND") reports
    Assert.Equal<string list>([ "Second" ], itemsOf "reportRows" searched |> List.map (field "title"))
    let narrowed, _ = play [ start, ui "reportText" ""; start, ui "reportFrom" "2026-10-09" ] reports
    Assert.True(flagOf "reportEmpty" narrowed)

    let byDay, _ = update (ctxAt start) (ui "reportGrouping" "day") reports
    Assert.Equal<string list>([ "Thursday, October 8" ], itemsOf "reportGroups" byDay |> List.map (field "name"))

[<Fact>]
let ``the export is the domain's deterministic CSV or JSON, copied through the clipboard`` () =
    let reports, _ = update (ctxAt start) (LocationMoved "#/reports") withThree
    let csv = textOf "exportText" reports
    Assert.StartsWith("# schema: chrona.time-report/1\n# organization: org-1\n# generatedAt: 2026-10-08T18:10:00Z", csv)
    let json, _ = update (ctxAt start) (ui "reportFormat" "json") reports
    Assert.StartsWith("{\n  \"schema\": \"chrona.time-report/1\"", textOf "exportText" json)

    let copying, effects = update (ctxAt start) (ui "copyExport" "") json
    Assert.Equal<Effect list>([ CopyText(ExportCopy, textOf "exportText" json) ], effects)
    Assert.Equal("Copied to the clipboard.", textOf "copyStatus" (fst (update (ctxAt start) (Copied(ExportCopy, true)) copying)))
    Assert.StartsWith("This browser did not allow copying.", textOf "copyStatus" (fst (update (ctxAt start) (Copied(ExportCopy, false)) copying)))
    Assert.Equal<Effect list>([ Print ], snd (update (ctxAt start) (ui "printReport" "") json))

[<Fact>]
let ``the month reports on itself`` () =
    let month, _ = update (ctxAt start) (LocationMoved "#/month/2026-09") withThree
    let next, effects = update (ctxAt start) (ui "reportMonth" "") month
    Assert.Equal<Effect list>([ push "/reports?from=2026-09-01&to=2026-09-30" ], effects)
    Assert.Equal(("2026-09-01", "2026-09-30"), (next.Report.From, next.Report.To))

[<Fact>]
let ``a report's filters live in its address, and the address sets the form`` () =
    let reports, _ = update (ctxAt start) (LocationMoved "#/reports") withThree
    let narrowed, effects = update (ctxAt start) (ui "reportMethod" "manual") reports
    Assert.Equal<Effect list>([ replace "/reports?method=manual" ], effects)
    let grouped, effects = update (ctxAt start) (ui "reportGrouping" "activityType") narrowed
    Assert.Equal<Effect list>([ replace "/reports?method=manual&group=type" ], effects)
    // A cold open of that address sets the form the same way.
    let cold, _ = update (ctxAt start) (LocationMoved "#/reports?method=manual&group=type") withThree
    Assert.Equal(grouped.Report.Method, cold.Report.Method)
    Assert.Equal(grouped.Report.Grouping, cold.Report.Grouping)
    Assert.Equal(textOf "exportText" grouped, textOf "exportText" cold)

[<Fact>]
let ``the export copied is the export shown, whatever happens between`` () =
    let reports, _ = update (ctxAt start) (LocationMoved "#/reports") withThree
    let shown = textOf "exportText" reports
    // Time passes (a timer wake-up, a click a second later) before the copy.
    let later = start.AddSeconds 1.0
    let ticked, _ = update (ctxAt later) (Ticked 99) reports
    let _, effects = update (ctxAt later) (ui "copyExport" "") ticked
    Assert.Equal<Effect list>([ CopyText(ExportCopy, shown) ], effects)
    // Changing the report generates it afresh.
    let changed, _ = update (ctxAt later) (ui "reportText" "first") reports
    Assert.Contains("# generatedAt: 2026-10-08T18:10:01Z", textOf "exportText" changed)

// ---- the week, a period and projects (CHX-460) -------------------------------------

[<Fact>]
let ``a week is named by its first day: another of its days is corrected in place`` () =
    // The organization's week starts on Monday: Thursday 8 October is in the week of the 5th.
    let week, effects = update (ctxAt start) (LocationMoved "#/week/2026-10-08") withThree
    Assert.Equal<Effect list>([ replace "/week/2026-10-05" ], effects)
    Assert.Equal(Places.Week(DateOnly(2026, 10, 5), None), week.Place)
    Assert.True(flagOf "screenWeek" week)
    Assert.Equal("Oct 5 – Oct 11", textOf "weekTitle" week)
    Assert.Equal<string list>([ "0m"; "0m"; "0m"; "2h"; "0m"; "0m"; "0m" ], itemsOf "weekDays" week |> List.map (field "total"))
    Assert.Equal("#/day/2026-10-08", field "href" (List.item 3 (itemsOf "weekDays" week)))
    // Moving between weeks and filtering refine it.
    Assert.Equal<Effect list>([ replace "/week/2026-09-28" ], snd (update (ctxAt start) (ui "previousWeek" "") week))
    Assert.Equal<Effect list>([ replace "/week/2026-10-12" ], snd (update (ctxAt start) (ui "nextWeek" "") week))
    let filtered, effects = update (ctxAt start) (ui "weekProject" project) week
    Assert.Equal<Effect list>([ replace $"/week/2026-10-05?project={project}" ], effects)
    Assert.Equal($"#/day/2026-10-08?project={project}", field "href" (List.item 3 (itemsOf "weekDays" filtered)))
    // This week's link is explicit, by its first day.
    let thisWeek, _ = update (ctxAt start) (LocationMoved "#/week") withThree
    Assert.Equal<Effect list>([ CopyText(LinkCopy, "http://127.0.0.1:4321/web/index.html#/week/2026-10-05") ], snd (update (ctxAt start) (ui "copyLink" "") thisWeek))
    // The day links to its week.
    let today, _ = update (ctxAt start) (LocationMoved "#/") withThree
    Assert.Equal("#/week/2026-10-05", textOf "weekHref" today)

[<Fact>]
let ``a timesheet period has its own page, named by its first day`` () =
    let period, effects = update (ctxAt start) (LocationMoved "#/periods/2026-10-08") withThree
    Assert.Equal<Effect list>([ replace "/periods/2026-10-05" ], effects)
    Assert.True(flagOf "screenPeriod" period)
    Assert.Equal("2h", textOf "periodPageExact" period)
    Assert.Equal("Weekly timesheet period", textOf "periodPageCadence" period)
    Assert.Equal(7, (itemsOf "periodDays" period).Length)
    Assert.Equal<Effect list>([ replace "/periods/2026-09-28" ], snd (update (ctxAt start) (ui "previousPeriod" "") period))
    let today, _ = update (ctxAt start) (LocationMoved "#/") withThree
    Assert.Equal("#/periods/2026-10-05", textOf "periodHref" today)

[<Fact>]
let ``a project has its own page with the person's time on it; an unknown one is not found`` () =
    let projects, _ = update (ctxAt start) (LocationMoved "#/projects") withThree
    Assert.True(flagOf "screenProjects" projects)
    let row = (itemsOf "projectList" projects).Head
    Assert.Equal("HelixNote", field "name" row)
    Assert.Equal($"#/projects/{project}", field "href" row)

    let page, _ = update (ctxAt start) (LocationMoved $"#/projects/{project}") withThree
    Assert.True(flagOf "screenProject" page)
    Assert.Equal("HelixNote", textOf "projectName" page)
    Assert.Equal("2h", textOf "projectTotal" page)
    Assert.Equal<string list>([ "Third"; "Second"; "First" ], itemsOf "projectEntries" page |> List.map (field "title"))
    Assert.Equal($"#/reports?project={project}", textOf "projectReportHref" page)
    Assert.Equal($"#/day/2026-10-08?project={project}", textOf "projectTodayHref" page)

    let missing, _ = update (ctxAt start) (LocationMoved "#/projects/PRJ-none") withThree
    Assert.True(flagOf "screenProblem" missing)
    Assert.Equal("Not found", textOf "problemTitle" missing)
