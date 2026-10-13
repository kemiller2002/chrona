/// An activity's lifecycle in the engine: amend, void, split, merge, the day
/// review and the month (WI-0047).
[<Xunit.Collection("AppEngine")>]
module Chrona.Tests.AppEngineCorrectionTests

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

// ---- lifecycle, merge, review and month (WI-0047) ------------------------------------

[<Fact>]
let ``month, review and an activity have addresses`` () =
    let id = activityId "First" withThree
    // An activity's link carries its day, so only that month is read.
    let today, _ = update (ctxAt start) (LocationMoved "#/") withThree
    let first = itemsOf "dayRecords" today |> List.find (fun row -> field "id" row = id)
    Assert.Equal($"#/entries/{id}?on=2026-10-08", field "href" first)
    Assert.Equal<Effect list>([ push "/review/2026-10-08" ], snd (update (ctxAt start) (ui "openReview" "") ready))
    let october, _ = update (ctxAt start) (LocationMoved "#/month/2026-10") ready
    Assert.Equal<Effect list>([ replace "/month/2026-09" ], snd (update (ctxAt start) (ui "previousMonth" "") october))
    Assert.Equal<Effect list>([ replace "/month/2027-01" ], snd (update (ctxAt start) (ui "showMonth" "2027-01") october))

[<Fact>]
let ``an activity's detail opens at its revision and follows the route`` () =
    let model = opened "First" withThree
    Assert.Equal(Some 1, model.Detail |> Option.map _.Revision)
    Assert.Equal("First", textOf "detailTitle" model)
    Assert.Equal("9:00 AM – 10:00 AM", textOf "detailTimes" model)
    Assert.True(flagOf "detailCanSplit" model)
    let left, _ = update (ctxAt start) (LocationMoved "#/") model
    Assert.True(left.Detail.IsNone)
    // An activity that is not in the records is the not-found page.
    let missing, _ = update (ctxAt start) (LocationMoved "#/activity/nope") model
    Assert.True(flagOf "screenProblem" missing)
    Assert.False(flagOf "detailLoading" missing)
    Assert.Equal("Nothing in your records is the activity with the id \"nope\". It may have been removed, or the link may be wrong.", textOf "problemDetail" missing)

[<Fact>]
let ``a correction keeps the original in the history and is revalidated`` () =
    let model = opened "First" withThree
    let refused, _ = play [ start, ui "amendPurpose" ""; start, ui "saveAmend" "" ] model
    Assert.Equal<Diagnostic list>([ MissingField "businessPurpose" ], refused.Problems[AmendForm])

    let amended, effects = play [ start, ui "amendDescription" "First, corrected"; start, ui "amendReason" "Typo"; start, ui "saveAmend" "" ] model
    let a = amended.Ledger.Activities[activityId "First, corrected" amended]
    Assert.Equal(2, a.Revision)
    Assert.Equal(Some 2, amended.Detail |> Option.map _.Revision)
    Assert.Equal<string list>([ "Amended"; "Recorded" ], itemsOf "detailHistory" amended |> List.map (field "action"))
    Assert.Equal("Reason: Typo", itemsOf "detailHistory" amended |> List.head |> field "reason")
    Assert.True(effects |> List.exists (function Store r -> r.Activities = [ a ] | _ -> false))

[<Fact>]
let ``a command against a revision that changed since the detail opened is a conflict`` () =
    let model = opened "First" withThree
    let id = activityId "First" model
    let zone = model.Zone.Value

    let context: Ledger.CommandContext =
        { Performer = model.Session.ActorId; At = start; Source = "elsewhere"; Zone = zone; References = model.References; CorrelationId = None }

    // Another device changes the record after this screen opened it.
    let elsewhere = Ledger.execute context model.Ledger (Ledger.Void(id, 1, "duplicate")) |> Result.defaultWith (fun e -> failwith $"{e}")
    let stale = { model with Ledger = elsewhere }
    let refused, _ = update (ctxAt start) (ui "saveAmend" "") stale
    Assert.Equal<Diagnostic list>([ RevisionConflict(1, 2) ], refused.Problems[AmendForm])
    Assert.Equal<string list>([ "This changed since you opened it. Review the current version and try again." ], itemsOf "amendProblems" refused |> List.map (field "text"))

[<Fact>]
let ``removing from totals keeps the record; restoring rechecks it`` () =
    let model = opened "First" withThree
    let voided, _ = play [ start, ui "voidReason" "Duplicate"; start, ui "voidActivity" "" ] model
    Assert.True(flagOf "detailVoided" voided)
    Assert.Equal("1h", textOf "dayTotal" voided)
    let today = itemsOf "dayRecords" voided |> List.find (fun r -> field "title" r = "First")
    Assert.Equal("Removed from totals", field "state" today)
    let restored, _ = update (ctxAt start) (ui "restoreActivity" "") voided
    Assert.True(flagOf "detailRecorded" restored)
    Assert.Equal("2h", textOf "dayTotal" restored)

[<Fact>]
let ``evidence links with a label and a web address, and unlinks`` () =
    let model = opened "First" withThree
    let bad, _ = play [ start, ui "evidenceUrl" "javascript:alert(1)"; start, ui "attachEvidence" "" ] model
    Assert.Equal<Diagnostic list>([ MissingField "evidenceLabel"; InvalidEvidenceUrl "javascript:alert(1)" ], bad.Problems[EvidenceForm])
    let linked, _ = play [ start, ui "evidenceUrl" "https://example.test/pr/4"; start, ui "evidenceKind" "pull-request"; start, ui "evidenceLabel" "PR 4"; start, ui "attachEvidence" "" ] model
    let evidence = itemsOf "detailEvidence" linked |> List.exactlyOne
    Assert.Equal("Pull request", field "kind" evidence)
    let unlinked, _ = update (ctxAt start) (keyed "unlinkEvidence" (field "id" evidence) "") linked
    Assert.Empty(itemsOf "detailEvidence" unlinked)
    Assert.Equal("Evidence unlinked", itemsOf "detailHistory" unlinked |> List.head |> field "action")

[<Fact>]
let ``a split shares the exact minutes and assigns each piece of evidence once`` () =
    let linked, _ = play [ start, ui "evidenceLabel" "Commit"; start, ui "attachEvidence" "" ] (opened "First" withThree)
    let evidenceId = linked.Ledger.Activities[linked.Detail.Value.ActivityId].Evidence.Head.Id
    let refused, _ = play [ start, ui "splitFirst" "20"; start, ui "splitSecond" "20"; start, ui "saveSplit" "" ] linked
    Assert.Equal<Diagnostic list>([ SplitDurationMismatch(60, 40) ], refused.Problems[SplitForm])
    let split, _ = play [ start, ui "splitSecond" "40"; start, keyed "splitEvidence" evidenceId "2"; start, ui "saveSplit" "" ] refused
    Assert.True(flagOf "detailSuperseded" split)
    let source = split.Detail.Value.ActivityId
    let children = split.Ledger.Activities |> Map.toList |> List.map snd |> List.filter (fun a -> a.Lineage = [ source ])
    Assert.Equal<int list>([ 20; 40 ], children |> List.map _.Minutes |> List.sort)
    Assert.Equal<int list>([ 0; 1 ], children |> List.sortBy _.Minutes |> List.map (fun c -> c.Evidence.Length))
    Assert.StartsWith("Replaced by \"First\" (20m), \"First\" (40m)", textOf "detailReplacedBy" split)

[<Fact>]
let ``only adjacent activities merge; the originals are kept, superseded`` () =
    let first, second, third = activityId "First" withThree, activityId "Second" withThree, activityId "Third" withThree
    let gap, _ = play [ start, check "mergeSelect" first true; start, check "mergeSelect" third true; start, ui "saveMerge" "" ] withThree
    Assert.True(flagOf "canMerge" gap)
    Assert.Equal<Diagnostic list>([ IncompatibleMergeSources "sources are not contiguous" ], gap.Problems[MergeForm])

    let merged, _ =
        play [ start, check "mergeSelect" third false; start, check "mergeSelect" second true; start, ui "mergeDescription" "First and second"; start, ui "saveMerge" "" ] gap

    Assert.Empty merged.MergeSelection
    Assert.Equal("2h", textOf "dayTotal" merged)
    Assert.Equal<string list>([ "First and second"; "Third" ], itemsOf "dayRecords" merged |> List.map (field "title"))
    Assert.Equal("Merged 2 activities into one. The originals are kept, superseded.", merged.Announcement)

[<Fact>]
let ``attestation needs a statement, keeps every earlier one, and a later change is an obligation`` () =
    let review, _ = update (ctxAt start) (LocationMoved "#/review/2026-10-08") withThree
    let refused, _ = update (ctxAt start) (ui "attestDay" "") review
    Assert.Equal<Diagnostic list>([ MissingField "statement" ], refused.Problems[AttestForm])

    let attested, effects = play [ start, ui "attestStatement" "Complete."; start, ui "attestDay" "" ] review
    Assert.True(effects |> List.exists (function Store r -> r.Attestations.Length = 1 | _ -> false))
    Assert.Equal<string list>([ "Complete." ], itemsOf "attestations" attested |> List.map (field "statement"))
    Assert.False(flagOf "hasObligations" attested)

    let second = activityId "Second" attested
    let changed, _ = play [ start, LocationMoved $"#/activity/{second}"; start, ui "amendReason" "Late fix"; start, ui "amendDescription" "Second, fixed"; start, ui "saveAmend" "" ] attested
    let obligations = itemsOf "obligations" changed
    Assert.Equal<string list>([ "Thursday, October 8 changed after you attested it" ], obligations |> List.map (field "title"))
    Assert.Equal<Effect list>([ push "/review/2026-10-08" ], snd (update (ctxAt start) (keyed "resolveObligation" (field "id" obligations.Head) "") changed))

    let again, _ = play [ start, LocationMoved "#/review/2026-10-08"; start, ui "attestStatement" "Rechecked."; start, ui "attestDay" "" ] changed
    Assert.Equal<string list>([ "Rechecked."; "Complete." ], itemsOf "attestations" again |> List.map (field "statement"))
    Assert.False(flagOf "hasObligations" again)

[<Fact>]
let ``a held timer is an obligation until it is completed`` () =
    let held, _ = play (chooseForTimer @ [ start, ui "startTimer" ""; at 5.0, ui "stopTimer" "" ]) ready
    Assert.Equal<string list>([ "stopped-timer" ], itemsOf "obligations" held |> List.map (field "id"))
    // Resolved on Track: from elsewhere, a move there; on Track already, nothing.
    let elsewhere, _ = update (ctxAt start) (LocationMoved "#/") held
    Assert.Equal<Effect list>([ push "/track" ], snd (update (ctxAt start) (keyed "resolveObligation" "stopped-timer" "") elsewhere))
    Assert.Equal<Effect list>([], snd (update (ctxAt start) (keyed "resolveObligation" "stopped-timer" "") held))

[<Fact>]
let ``the month sums effective records by type and by day`` () =
    let model, _ = update (ctxAt start) (LocationMoved "#/month/2026-10") withThree
    Assert.Equal("October 2026", textOf "monthTitle" model)
    Assert.Equal("2h", textOf "monthTotal" model)
    Assert.Equal("1", textOf "monthActiveDays" model)
    let byType = itemsOf "monthByType" model |> List.exactlyOne
    Assert.Equal("Research", field "name" byType)
    Assert.Equal("100%", field "shareText" byType)
    Assert.Equal<string list>([ "Thursday, October 8" ], itemsOf "monthDays" model |> List.map (field "label"))
    let september, _ = update (ctxAt start) (LocationMoved "#/month/2026-09") withThree
    Assert.True(flagOf "monthEmpty" september)
