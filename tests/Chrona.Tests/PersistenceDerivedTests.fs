/// The activity index (WI-0034) and audit records (WI-0056) on Arca.
[<Xunit.Collection("Persistence")>]
module Chrona.Tests.PersistenceDerivedTests

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

// ---- The activity index (WI-0034) -------------------------------------------------------

[<Fact>]
let ``the activity index follows each change, and its source set is the one Arca computes from the records`` () =
    let snapshot, state =
        (Persistence.empty, InMemory.empty)
        |> store (Record(activity "A-1" (10, 7) (9, 0) 60))
        |> store (Record(activity "A-2" (9, 30) (9, 0) 30))

    // Built by Arca from the stored records...
    let records =
        snapshot.Activities
        |> Map.toList
        |> List.map (fun (_, found) ->
            let stored = storedContent found.Path state
            let key = Layout.keyOf found.Path |> Option.get
            found.Path, Integrity.validate key ActivityRecord.schema Record.DefaultMaxBytes stored |> ok)

    let built = Derived.build ActivityIndex.definition records

    // ...and kept by Chrona from the changes alone.
    let changes =
        snapshot.Activities |> Map.toList |> List.map (fun (_, found) -> Change.Create(found.Path, ActivityRecord.encode found.Activity |> ok))

    let kept = ActivityIndex.apply changes ActivityIndex.empty
    Assert.Equal(built, kept)

    // Months and totals: a voided record does not count.
    let voided = { snapshot.Activities["A-1"].Activity with Record = Voided "Duplicate"; Revision = 2 }
    let path = snapshot.Activities["A-1"].Path
    let after = ActivityIndex.apply [ Change.Update(path, ActivityRecord.encode voided |> ok, snapshot.Activities["A-1"].Revision) ] kept

    Assert.Equal<(int * int * int * int) list>(
        [ 2026, 10, 0, 0; 2026, 9, 1, 30 ],
        ActivityIndex.totals after |> List.map (fun t -> t.Year, t.Month, t.Activities, t.Minutes)
    )

    // A deleted record leaves the index; the source set follows.
    let removed = ActivityIndex.apply [ Change.Delete(path, Revision "x") ] after
    Assert.Equal(1, removed.Source.Count)
    Assert.Equal(1, removed.Entries.Length)

    // Where the records read disagree with it, it says so.
    let folder = ActivityRecord.monthFolder actor (DateOnly(2026, 10, 1)) |> ok |> RelativePath.render
    Assert.Empty(ActivityIndex.disagreements kept folder [ RelativePath.render path, snapshot.Activities["A-1"].ContentHash ])
    Assert.Equal<string list>([ RelativePath.render path ], ActivityIndex.disagreements kept folder [ RelativePath.render path, "sha256:other" ])

// ---- Audit records (WI-0056) -------------------------------------------------------

[<Fact>]
let ``an audit entry is an immutable record with a stable id, read back as it was written`` () =
    let entry: AuditEntry =
        { Performer = actor
          At = at 10 7 18 0
          Source = "chrona-web"
          Command = "amend"
          ActivityIds = [ "A-1" ]
          PriorRevisions = [ "A-1", 1 ]
          ResultingRevisions = [ "A-1", 2 ]
          Reason = Some "fix"
          CorrelationId = None }

    let audited = AuditRecord.place [ activity "A-1" (10, 7) (9, 0) 60 ] [ entry ] |> List.exactlyOne
    Assert.Equal(actor, audited.Owner)
    Assert.Equal(AuditRecord.idOf audited, AuditRecord.idOf audited)
    Assert.NotEqual<string>(AuditRecord.idOf audited, AuditRecord.idOf { audited with Entry = { entry with Reason = Some "other" } })

    let path = AuditRecord.path audited |> ok
    Assert.Equal("records/chrona.audit/github_3aoctocat/2026/10", RelativePath.render path |> fun p -> p.Substring(0, p.LastIndexOf '/'))
    let content = AuditRecord.encode audited |> ok
    Assert.Equal(Ok { StoredChanges.nothing with Audit = [ audited ] }, StoredChanges.changedOf [ Change.Create(path, content) ])

    // Written once: an entry already stored is not written again.
    let stored = { Stored.empty with Audit = Map.ofList [ RelativePath.render path, audited ] }
    Assert.Equal(Ok [], StoredChanges.changes stored { StoredChanges.nothing with Audit = [ audited ] })
    Assert.Empty(AuditRecord.place [] [ entry ])
