/// Refreshing without asking again (WI-0077), and reads pinned at the state
/// observed (WI-0081).
[<Xunit.Collection("Store")>]
module Chrona.Tests.StoreRefreshTests

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

// ---- Refreshing without asking again (WI-0077) ----------------------------------------

[<Fact>]
let ``a refresh asks for the history only of records that changed since this page read them`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    record device "08:00" "08:30" "Setup"
    recordOn device "2026-08-03" "August work"

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    let opened = reader.HistoryAsked
    Assert.Single(opened) |> ignore

    // Reading August asks only about August's record; October's is known.
    reader.Send(LocationMoved "#/reports?from=2026-08-01&to=2026-10-31")
    Assert.Contains("August work", reportTitles reader.Model)
    let added = reader.HistoryAsked |> List.skip opened.Length
    Assert.Single(added) |> ignore
    Assert.Contains("/2026/08/", added.Head)

    // Someone edits October's record elsewhere: only it is asked about again.
    let id = reader.Model.Ledger.Activities |> Map.toList |> List.map snd |> List.find (fun a -> a.Classification.Description = "Setup") |> _.ActivityId
    amendFrom device id "Setup, amended"
    let before = reader.HistoryAsked.Length
    // Coming back to the page reads everything open again.
    reader.Return()
    let again = reader.HistoryAsked |> List.skip before
    Assert.True(again |> List.forall (fun path -> path.Contains "/2026/10/"), $"%A{again}")
    Assert.Contains("Setup, amended", reportTitles reader.Model)
    Assert.Empty(reader.Model.Store.Integrity |> List.filter (function Chrona.Domain.Diagnostics.ExternalEdit _ -> true | _ -> false))

[<Fact>]
let ``a refresh reads every folder through a pin at the state it observed, and writes are never pinned`` () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "08:00" "08:30" "Setup"

    let device = Device(github, RepositoryVisibility.Private, "production")
    device.UsePins()
    device.Open()
    Assert.Contains("Setup", descriptions device)
    Assert.NotEmpty(device.PinnedAt)
    Assert.True(device.PinnedReads > 0)

    // Writing still goes through the provider, conditioned as before, and
    // the next refresh pins the new state.
    let pinsBefore = device.PinnedAt.Length
    record device "09:00" "09:30" "Pairing"
    Assert.Empty(device.Model.Store.Pending)
    device.Return()
    Assert.True(device.PinnedAt.Length > pinsBefore)
    // The repository moved, so the pin moved with the state observed.
    Assert.NotEqual(device.PinnedAt[pinsBefore - 1], List.last device.PinnedAt)
    Assert.Contains("Pairing", descriptions device)

[<Fact>]
let ``opening reads the organization's manifest, its activity index and the inboxes through pins, never resolving the branch for each`` () =
    let github, _ = withInbox ()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.UsePins()
    device.Open()
    Assert.Equal(2, device.Model.Inbox.Received)

    let unpinned = device.Unpinned
    // Read only through a pin: the organization's manifest, the activity
    // index, the inbox and receipt listings, and every folder load reads.
    for path in unpinned do
        Assert.False(path.Contains "derived/indexes", $"index read unpinned: {path}")
        Assert.False(path = "inbox" || path = "inbox/github", $"inbox listed unpinned: {path}")
        Assert.False(path.StartsWith "records/chrona.receipt" && not (path.EndsWith ".json"), $"receipts listed unpinned: {path}")
        Assert.False(path.StartsWith "records/chrona.activity", $"a folder load reads was unpinned: {path}")

    // Manifests: the application's, read once as it was; the organization's
    // two reads (whether it exists, then opening it) went through the pin.
    Assert.Equal(1, unpinned |> List.filter ((=) "arca-manifest.json") |> List.length)

    // What stays current, by design: the application's manifest (one read),
    // and each inbox file as it is decided, since the pass's own commits move
    // the folder under it.
    Assert.All(unpinned, fun path -> Assert.True(path = "arca-manifest.json" || path.StartsWith "inbox/github/" || path.StartsWith "records/chrona.receipt/github/", path))
