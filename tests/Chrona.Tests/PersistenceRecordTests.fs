/// An activity as an Arca record (WI-0051): every field round-trips, and the
/// partitioned layout (requirements expansion 5, 22, 38).
[<Xunit.Collection("Persistence")>]
module Chrona.Tests.PersistenceRecordTests

open System
open Xunit
open Arca
open Chrona.Tests.Support
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Domain.Ledger
open Chrona.Tests.PersistenceFixture

module Storage = Chrona.Domain.Storage
module ActivityRecord = Chrona.Domain.ActivityRecord
module ActivityRecordReading = Chrona.Domain.ActivityRecordReading
module Persistence = Chrona.Domain.Persistence
module Access = Chrona.Domain.Access
module Reconcile = Chrona.Domain.Reconcile
module Stored = Chrona.Domain.Stored
module StoredChanges = Chrona.Domain.StoredChanges
module StoredOverlay = Chrona.Domain.StoredOverlay
module MemberRecord = Chrona.Domain.MemberRecord
module ActivityIndex = Chrona.Domain.ActivityIndex
module AuditRecord = Chrona.Domain.AuditRecord

// ---- The record: every field, round trip (5) -----------------------------------

[<Fact>]
let ``every field of the activity record survives storage`` () =
    let start = at 10 7 9 0

    let rich =
        { activity "A-1" (10, 7) (9, 0) 45 with
            Timing = DurationOnDate 45
            Classification =
                { ProjectId = "PRJ-1"
                  ClientId = Some "CLI-1"
                  EngagementId = Some "ENG-1"
                  ActivityTypeId = "ACT-DEV"
                  Tags = [ "backend"; "review" ]
                  Description = "Pairing on the \"ledger\" été"
                  BusinessPurpose = "Delivery" }
            EntryMethod = Imported "github"
            Billability = PendingClassification
            BillingReference =
                { RateReference = Some "RATE-1"
                  BillingClass = Some "standard"
                  ContractReference = None }
            Record = Superseded [ "A-2"; "A-3" ]
            Review = Rejected "wrong project"
            Publication = AdjustmentRequired
            Revision = 4
            LastChangedAt = start.AddHours 3.0
            Reason = Some "reconstructed"
            WorkItemRef = Some "WI-7"
            ExternalRef = Some "PR-12"
            Evidence =
                [ { Id = "E-1"
                    Url = "https://example.test/commit/1"
                    Kind = "commit"
                    Label = "Commit 1"
                    CapturedAt = start.ToOffset(TimeSpan.FromHours -4.0)
                    Hash = Some "sha256:abc"
                    Source = Some "GitHub"
                    Notes = Some "The commit that fixed it." } ]
            Lineage = [ "A-0" ]
            Source =
                Some
                    { SourceSystem = "github"
                      ObservationId = "OBS-9"
                      ExternalUrl = None
                      IngestedAt = start } }

    let alternatives =
        [ rich
          { rich with
              Record = Voided "entered twice"
              Review = Approved
              EntryMethod = Timer
              Source = None }
          activity "A-1" (10, 7) (9, 0) 45 ]

    for original in alternatives do
        let text = ActivityRecord.encode original |> ok
        let record = Record.decode Record.DefaultMaxBytes text |> ok
        Assert.Equal(original, ActivityRecordReading.ofBody record.Body |> ok)
        // Stored text is canonical, so equal activities are equal bytes.
        Assert.Equal(text, ActivityRecord.encode original |> ok)

    // A field this version does not define is refused, never ignored.
    let body = ActivityRecord.body rich

    let extended =
        match body with
        | Json.Object members -> Json.Object(("rate", Json.Number 150m) :: members)
        | other -> other

    Assert.True(ActivityRecordReading.ofBody extended |> Result.isError)

// ---- Layout (22, 38) ----------------------------------------------------------

[<Fact>]
let ``activities live at deterministic paths partitioned by actor, year and month`` () =
    let a = activity "A-1" (10, 7) (9, 0) 60
    Assert.Equal("records/chrona.activity/github_3aoctocat/2026/10/A-1.json", ActivityRecord.path a |> ok |> RelativePath.render)

    // Actor ids that differ only in characters a path cannot hold stay distinct.
    Assert.NotEqual<string>(ActivityRecord.actorSegment "a:b", ActivityRecord.actorSegment "a_3ab")
    Assert.NotEqual<string>(ActivityRecord.actorSegment "a_b", ActivityRecord.actorSegment "a:b")
    Assert.Equal("_2eprofile", ActivityRecord.actorSegment ".profile")

    // The folders to read for a set of dates: one per month, in order.
    let folders = ActivityRecord.monthFolders actor [ october; september; october.AddDays 1 ] |> ok
    Assert.Equal<string list>(
        [ "records/chrona.activity/github_3aoctocat/2026/09"; "records/chrona.activity/github_3aoctocat/2026/10" ],
        folders |> List.map RelativePath.render
    )

    // An id that is not a stable record id is not stored.
    Assert.Equal("CHRONA.STORAGE.UNSTORABLE_ACTIVITY", ActivityRecord.path { a with ActivityId = "a/b" } |> Result.mapError code |> function Error c -> c | Ok _ -> "ok")

[<Fact>]
let ``the today view reads one month, never the whole history`` () =
    let _, state =
        (Persistence.empty, InMemory.empty)
        |> store (Record(activity "A-SEP" (9, 30) (9, 0) 60))
        |> store (Record(activity "A-OCT" (10, 7) (9, 0) 60))

    let today = readMonths [ october ] state
    Assert.Equal<string list>([ "A-OCT" ], today.Activities |> Map.keys |> List.ofSeq)
    Assert.Empty(today.Problems)
