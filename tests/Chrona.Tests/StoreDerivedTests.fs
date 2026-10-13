/// What the store derives and records beside the records: the activity
/// index, kept with every change and rebuilt from the records (WI-0034: 22,
/// 38, 40), and the stored audit trail (WI-0056: 25, 40).
[<Xunit.Collection("Store")>]
module Chrona.Tests.StoreDerivedTests

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

// ---- Derived state (WI-0034: 22, 38, 40) -------------------------------------------------------

let private storedIndex (github: InMemoryStore) =
    let config = configuration "production"

    match Derived.read github.Provider (folderOf config) ActivityIndex.definition |> Async.RunSynchronously with
    | Ok(Some(index, _)) -> Some index
    | Ok None -> None
    | Error error -> failwith $"%A{error}"

/// Arca's own rebuild from every stored record: None when the stored index
/// is already exactly what the records make.
let private rebuildWrites (github: InMemoryStore) =
    let config = configuration "production"

    let metadata: OperationMetadata =
        { Summary = "check"
          Actor = { Kind = ActorKind.Human; Id = ActorId.create "github:583231" |> ok }
          ProviderIdentity = None
          ExecutionId = None
          CorrelationId = CorrelationId.create "check-1" |> ok
          IdempotencyKey = IdempotencyKey.create "index-check-1" |> ok }

    match Derived.rebuild github.Provider (folderOf config) metadata ActivityIndex.definition |> Async.RunSynchronously with
    | Ok(index, receipt) -> index, receipt.IsSome
    | Error error -> failwith $"%A{error}"

[<Fact>]
let ``the activity index is kept with every change, exactly as a rebuild from the records makes it`` () =
    let github, first, second = twoDevices ()
    record first "09:00" "10:00" "Pairing"
    let id = first.Model.Ledger.Activities |> Map.toList |> List.find (fun (_, a) -> a.Classification.Description = "Pairing") |> fst
    amendFrom second id "Pairing, corrected"
    second.Send(LocationMoved $"#/activity/{id}")
    second.Ui("voidReason", "Duplicate")
    second.Ui("voidActivity", "")

    let kept = storedIndex github |> Option.get
    Assert.Equal(2, kept.Source.Count)

    // Arca's rebuild from every record finds nothing to change.
    let rebuilt, wrote = rebuildWrites github
    Assert.False(wrote)
    Assert.Equal(kept, rebuilt)

    // What the page shows comes from it: one month, the voided entry not counted.
    match second.Model.Store.History with
    | [ october ] ->
        Assert.Equal((2026, 10), (october.Year, october.Month))
        Assert.Equal(1, october.Activities)
        Assert.Equal(30, october.Minutes)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a month not read yet is listed from the index, and read only when the person goes to it`` () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "08:00" "08:30" "Setup"

    // Time recorded in August, months before the current period.
    first.Ui("manualActivityType", activityType first.Model)
    first.Ui("manualProject", project first.Model)
    first.Ui("manualStartDate", "2026-08-03")
    first.Ui("manualEndDate", "2026-08-03")
    first.Ui("manualStartTime", "09:00")
    first.Ui("manualEndTime", "10:00")
    first.Ui("manualDescription", "August work")
    first.Ui("manualPurpose", "Delivery")
    first.Ui("manualReason", "From notes")
    first.Ui("saveManual", "")
    Assert.Equal(None, first.Model.Store.Problem)

    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()
    Assert.DoesNotContain((2026, 8), second.Model.Store.Months)
    Assert.DoesNotContain("August work", descriptions second)

    let august = second.Model.Store.History |> List.find (fun total -> total.Month = 8)
    Assert.Equal(60, august.Minutes)
    second.Send(LocationMoved "#/month/2026-10")
    let historyRows =
        match (Project.project second.Model |> Map.ofList)["monthHistory"] with
        | Chrona.Engine.View.Items rows ->
            rows
            |> List.map (fun row ->
                row
                |> List.choose (function
                    | key, Chrona.Engine.View.Text value when key = "id" || key = "label" || key = "total" -> Some value
                    | _ -> None))
        | other -> failwith $"%A{other}"

    Assert.Contains([ "2026-08"; "August 2026"; "1h" ], historyRows)

    // Going to August reads its folder, and only then shows its entries.
    second.Ui("showMonth", "2026-08")
    second.Send(LocationMoved "#/month/2026-08")
    Assert.Contains((2026, 8), second.Model.Store.Months)
    Assert.Contains("August work", descriptions second)
    Assert.Empty(second.Model.Store.Reading)

[<Fact>]
let ``an organization without an index is told so, and an administrator builds it from the records`` () =
    let github, first, _ = twoDevices ()
    let config = configuration "production"
    github.WriteExternally((folderOf config).Location, $"deployments/chrona/datasets/org_acme/{RelativePath.render ActivityIndex.path}", None)

    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    Assert.Contains("has not been built yet", device.Model.Store.Index)
    Assert.Empty(device.Model.Store.History)

    // Changes go on without it; nothing derived is written.
    record device "09:00" "10:00" "Pairing"
    Assert.Equal(None, storedIndex github)

    device.Ui("rebuildIndex", "")
    Assert.Equal("The activity index was built from 2 records.", device.Model.Store.Index)
    Assert.Equal(2, (storedIndex github |> Option.get).Source.Count)
    Assert.Equal(90, device.Model.Store.History |> List.sumBy _.Minutes)
    Assert.False(snd (rebuildWrites github))

[<Fact>]
let ``a record changed outside Chrona shows where the index is out of date, and a rebuild repairs it`` () =
    let github, second, _, _, _ = editedOutside (described "Edited on github.com")
    Assert.Contains("differs from the records in 1 place ", second.Model.Store.Index)

    second.Ui("rebuildIndex", "")
    Assert.Equal("The activity index was rebuilt from 1 record; 2 entries had differed.", second.Model.Store.Index)
    Assert.False(snd (rebuildWrites github))

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.StartsWith("Kept with every change", reader.Model.Store.Index)

[<Fact>]
let ``only an administrator rebuilds the index`` () =
    let github = InMemoryStore()
    let owner = Device(github, RepositoryVisibility.Private, "production")
    owner.Open()
    owner.Ui("memberId", "1001")
    owner.Ui("memberName", "hubot")
    owner.Ui("memberAccess", "ownTime")
    owner.Ui("admitMember", "")
    let member' = Device(github, RepositoryVisibility.Private, "production", hubot, configuration "production")
    member'.Open()
    let commits = github.State.History.Length
    member'.Ui("rebuildIndex", "")
    Assert.Equal<string list>([ "CHRONA.AUTH.UNAUTHORIZED_CAPABILITY" ], member'.Model.Problems[IndexForm] |> List.map code)
    Assert.Equal(commits, github.State.History.Length)

[<Fact>]
let ``scenario 40: every projection is rebuilt from the stored records alone`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    record device "08:00" "09:00" "Setup"
    record device "09:00" "10:00" "Pairing"
    record device "10:00" "11:00" "Review"
    let idOf text = device.Model.Ledger.Activities |> Map.toList |> List.find (fun (_, a) -> a.Classification.Description = text) |> fst
    let pairing, review, setup = idOf "Pairing", idOf "Review", idOf "Setup"
    amendFrom device pairing "Pairing, corrected"
    device.Send(LocationMoved $"#/activity/{review}")
    device.Ui("voidReason", "Duplicate")
    device.Ui("voidActivity", "")
    device.Send(LocationMoved $"#/activity/{setup}")
    device.Ui("splitFirst", "20")
    device.Ui("splitSecond", "40")
    device.Ui("saveSplit", "")
    device.Send(LocationMoved "#/review/2026-10-07")
    device.Ui("attestStatement", "Complete and accurate.")
    device.Ui("attestDay", "")
    Assert.Equal(None, device.Model.Store.Problem)

    // A device that only read what is stored.
    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()

    // Announcements are transitions, not state. The activity screen's
    // history is read from the stored audit trail (WI-0056).
    let ephemeral (key: string) = key = "announcement" || key = "copyStatus"

    for route in
        [ "#/today/2026-10-07"
          "#/review/2026-10-07"
          "#/month/2026-10"
          "#/reports"
          "#/more"
          $"#/activity/{pairing}"
          $"#/activity/{review}"
          $"#/activity/{setup}" ] do
        device.Send(LocationMoved route)
        reader.Send(LocationMoved route)
        let view (d: Device) = Project.project d.Model |> List.filter (fst >> ephemeral >> not)
        let differing = List.zip (view device) (view reader) |> List.filter (fun (a, b) -> a <> b)
        Assert.True(differing.IsEmpty, $"{route}: %A{differing}")

// ---- The stored audit trail (WI-0056: 25, 40) ---------------------------------------------------

let private auditPaths (github: InMemoryStore) =
    storedPaths github |> List.filter (fun path -> path.Contains "/records/chrona.audit/")

[<Fact>]
let ``who changed what is stored with the records, once, beside the activity it concerns`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    record device "09:00" "10:00" "Pairing"
    let id = device.Model.Ledger.Activities |> Map.toList |> List.head |> fst
    amendFrom device id "Pairing, corrected"

    // One immutable record per command, in the person's month folder.
    let paths = auditPaths github
    Assert.Equal(2, paths.Length)
    Assert.All(paths, fun path -> Assert.Contains("/records/chrona.audit/github_3a583231/2026/10/", path))

    // A device that only read the records shows the same history.
    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    reader.Send(LocationMoved $"#/activity/{id}")

    let actions =
        match (Project.project reader.Model |> Map.ofList)["detailHistory"] with
        | Chrona.Engine.View.Items rows ->
            rows |> List.map (List.pick (function "action", Chrona.Engine.View.Text action -> Some action | _ -> None))
        | other -> failwith $"%A{other}"

    Assert.Equal<string list>([ "Amended"; "Recorded" ], actions)
    Assert.Equal(Chrona.Engine.View.Value(Chrona.Engine.View.Text ""), (Project.project reader.Model |> Map.ofList)["detailHistoryNote"])
    Assert.Equal("github:583231", reader.Model.Ledger.Audit.Head.Performer)

[<Fact>]
let ``a change refused as a conflict stores no audit entry`` () =
    let github, first, second = twoDevices ()
    record first "09:00" "10:00" "Pairing"
    let before = auditPaths github
    record second "09:30" "10:30" "Overlap"
    Assert.Single(second.Model.Store.Conflicts) |> ignore
    Assert.Equal<string list>(before, auditPaths github)

[<Fact>]
let ``accepting an outside edit is audited, and revisions made before the trail was stored say so`` () =
    let github, second, id, _, _ = editedOutside (described "Edited on github.com")
    second.Ui("acceptOutsideEdit", id, "")

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    reader.Send(LocationMoved $"#/activity/{id}")
    Assert.Contains(reader.Model.Ledger.Audit, fun entry -> entry.Command = "accept-outside-edit" && entry.ResultingRevisions = [ id, 3 ])

    // Revision 2 was written on github.com: no entry tells of it.
    Assert.Contains("before Chrona kept its history", (Project.project reader.Model |> Map.ofList)["detailHistoryNote"] |> function Chrona.Engine.View.Value(Chrona.Engine.View.Text t) -> t | other -> failwith $"%A{other}")
