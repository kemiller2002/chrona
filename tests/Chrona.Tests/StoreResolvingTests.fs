/// What was not stored, resolved by the person (WI-0035: 21, 23, 26, 34), and
/// records edited outside Chrona reviewed before they are trusted again (41).
[<Xunit.Collection("Store")>]
module Chrona.Tests.StoreResolvingTests

open System
open Xunit
open Arca
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update
open Chrona.Application
open Chrona.Tests.StoreBrowserFixture
open Chrona.Tests.StoreDeviceFixture
open Chrona.Tests.StoreFixtures

// ---- Resolving what was not stored (WI-0035: 21, 23, 26, 34) ------------------------------

[<Fact>]
let ``a stale edit is redone on the current version only when the person saves it again`` () =
    let github, first, second = twoDevices ()
    let id = first.Model.Ledger.Activities |> Map.toList |> List.head |> fst
    amendFrom first id "From the first device"
    amendFrom second id "From the second device"
    let case = second.Model.Store.Conflicts.Head
    Assert.Equal("Redo yours on the current version", rowText "conflicts" "redoLabel" second.Model)

    // Redoing puts the person's version into the correction form of the
    // current record; nothing is written until they save it.
    let commits = github.State.History.Length
    second.Ui("redoChange", case.Id, id)
    second.Send(LocationMoved $"#/activity/{id}")
    Assert.Empty(second.Model.Store.Conflicts)
    Assert.Equal(commits, github.State.History.Length)
    let detail = second.Model.Detail.Value
    Assert.Equal("From the second device", detail.Amend.Description)
    Assert.Equal(second.Model.Ledger.Activities[id].Revision, detail.Revision)

    second.Ui("amendReason", "Redone after a conflict")
    second.Ui("saveAmend", "")
    Assert.Empty(second.Model.Store.Conflicts)
    Assert.Equal(None, second.Model.Store.Problem)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal("From the second device", reader.Model.Ledger.Activities[id].Classification.Description)

[<Fact>]
let ``keeping what is stored sets the person's change aside, knowingly, and writes nothing`` () =
    let github, first, second = twoDevices ()
    let id = first.Model.Ledger.Activities |> List.ofSeq |> List.head |> _.Key
    amendFrom first id "From the first device"
    amendFrom second id "From the second device"
    let commits = github.State.History.Length

    // Until the person decides, it is an obligation, and the day is not saved.
    Assert.Contains("was not saved", rowText "obligations" "title" second.Model)
    Assert.Equal("Not saved: changed elsewhere", (Project.project second.Model |> Map.ofList)["storeHeadline"] |> function Chrona.Engine.View.Value(Chrona.Engine.View.Text t) -> t | other -> failwith $"%A{other}")

    second.Ui("keepStored", second.Model.Store.Conflicts.Head.Id, "")
    Assert.Empty(second.Model.Store.Conflicts)
    Assert.Equal("Your change was set aside. What is stored stays.", second.Model.Announcement)
    Assert.Equal(commits, github.State.History.Length)
    Assert.Equal("From the first device", second.Model.Ledger.Activities[id].Classification.Description)

[<Fact>]
let ``new time that overlaps what was stored since goes back into the form, to be changed and saved`` () =
    let github, first, second = twoDevices ()
    record first "09:00" "10:00" "Pairing"
    record second "09:30" "10:30" "Overlap"
    let case = second.Model.Store.Conflicts.Head
    let mine = match case.Divergences with [ Reconcile.OverlapsStored(mine, _) ] -> mine | other -> failwith $"%A{other}"

    second.Ui("redoChange", case.Id, mine.ActivityId)
    let draft = second.Model.Manual
    Assert.Equal<string list>([ "2026-10-07"; "09:30"; "2026-10-07"; "10:30"; "Overlap" ], [ draft.StartDate; draft.StartTime; draft.EndDate; draft.EndTime; draft.Classification.Description ])

    second.Ui("manualStartTime", "10:00")
    second.Ui("manualEndTime", "11:00")
    second.Ui("saveManual", "")
    Assert.Empty(second.Model.Store.Conflicts)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Overlap"; "Pairing"; "Setup" ], descriptions reader)

[<Fact>]
let ``another application's commits to the shared repository never move Chrona's records under a change`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    record device "08:00" "08:30" "Setup"
    let commits = github.State.History.Length

    // Another application commits elsewhere in the repository before each of
    // the next three commits (Arca 0.4.0's namespace token, ARCA-CON-005).
    device.Beaten 3
    record device "09:00" "10:00" "Pairing"
    record device "10:00" "10:30" "Review"
    Assert.Empty(device.Model.Store.Conflicts)
    Assert.Equal(None, device.Model.Store.Problem)
    Assert.Equal<string list>([ "Pairing"; "Review"; "Setup" ], descriptions device)
    // Each change was one commit, never tried again: two of Chrona's and
    // the other application's two that came before them.
    Assert.Equal(commits + 4, github.State.History.Length)

[<Fact>]
let ``a change refused only because the folder kept moving is tried again when the person asks`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    record device "08:00" "08:30" "Setup"

    // Something commits inside Chrona's folder before each of the three
    // attempts.
    device.BeatenInside 3
    record device "09:00" "10:00" "Pairing"
    let case = device.Model.Store.Conflicts.Head
    Assert.Equal<Reconcile.Divergence list>([ Reconcile.KeptChanging ], case.Divergences)
    Assert.Equal("CHRONA.CONCURRENCY.KEPT_CHANGING", rowText "conflicts" "code" device.Model)
    Assert.Equal<string list>([ "Setup" ], descriptions device)

    device.Ui("retryChange", case.Id, "")
    Assert.Empty(device.Model.Store.Conflicts)
    Assert.Equal(None, device.Model.Store.Problem)
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions device)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions reader)

// ---- Reviewing records edited outside Chrona (WI-0035: 41) ----------------------------------

[<Fact>]
let ``a record edited outside Chrona is accepted by its person after the usual rules, and is trusted again`` () =
    let github, second, id, _, _ = editedOutside (described "Edited on github.com")
    Assert.Equal<string list>([ id ], second.Model.Store.Held |> List.map _.ActivityId)
    Assert.Contains("Edited on github.com", rowText "outsideEdits" "title" second.Model)
    Assert.Contains("changed outside Chrona", rowText "obligations" "title" second.Model)

    second.Ui("acceptOutsideEdit", id, "")
    Assert.Empty(second.Model.Store.Held)
    Assert.Empty(second.Model.Store.Integrity)
    Assert.Equal(None, second.Model.Store.Problem)
    Assert.Equal(3, second.Model.Ledger.Activities[id].Revision)

    // Its newest commit is Chrona's now: another device trusts it.
    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Empty(reader.Model.Store.Held)
    Assert.Empty(reader.Model.Store.Integrity)
    Assert.Equal("Edited on github.com", reader.Model.Ledger.Activities[id].Classification.Description)
    Assert.Equal(3, reader.Model.Ledger.Activities[id].Revision)

[<Fact>]
let ``an outside edit that claims a state only Chrona gives is never accepted, and stays held`` () =
    let github, second, id, _, _ =
        editedOutside (fun activity -> { described "Approved on github.com" activity with Review = Activity.Approved })

    let commits = github.State.History.Length
    second.Ui("acceptOutsideEdit", id, "")
    Assert.Equal<string list>([ "CHRONA.INTEGRITY.EXTERNAL_STATE_CLAIM" ], second.Model.Problems[OutsideEditForm] |> List.map code)
    Assert.Equal<string list>([ id ], second.Model.Store.Held |> List.map _.ActivityId)
    Assert.Equal(commits, github.State.History.Length)

[<Fact>]
let ``an outside edit changed again after it was reviewed is not accepted on the strength of the earlier review`` () =
    let github, second, id, original, write = editedOutside (described "First outside edit")
    write (described "Second outside edit" original)

    second.Ui("acceptOutsideEdit", id, "")

    match second.Model.Store.Conflicts with
    | [ { Divergences = [ Reconcile.ActivityChanged(mine, None) ] } ] -> Assert.Equal("First outside edit", mine.Classification.Description)
    | other -> failwith $"%A{other}"

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ id ], reader.Model.Store.Held |> List.map _.ActivityId)
    Assert.Equal("Second outside edit", reader.Model.Store.Held.Head.Classification.Description)
