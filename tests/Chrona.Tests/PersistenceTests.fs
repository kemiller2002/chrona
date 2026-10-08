/// Activities stored on Arca (WI-0051): the record codec, the partitioned
/// layout, change sets, integrity on load and manual edits (requirements
/// expansion 5, 21, 22, 38, 39, 41), against Arca's in-memory provider.
module Chrona.Tests.PersistenceTests

open System
open Xunit
open Arca
open Chrona.Tests.Support
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Domain.Ledger

module Storage = Chrona.Domain.Storage
module ActivityRecord = Chrona.Domain.ActivityRecord
module Persistence = Chrona.Domain.Persistence

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private zone = tryZone "America/New_York" |> ok
let private actor = "github:octocat"

/// 09:00 local (EDT) on a day of 2026.
let private at (month: int) (day: int) (h: int) (m: int) = DateTimeOffset(2026, month, day, h + 4, m, 0, TimeSpan.Zero)

let private context =
    { Performer = actor
      At = at 10 7 18 0
      Source = "chrona-web"
      Zone = zone
      References = references
      CorrelationId = Some "corr-1" }

let private activity id (month, day) (startH, startM) minutes =
    let start = at month day startH startM

    { ActivityId = id
      OrganizationId = "ORG-1"
      ActorId = actor
      Occurrence = occurrence zone start
      Timing = Interval(start, start.AddMinutes(float minutes))
      Minutes = minutes
      Classification =
        { ProjectId = "PRJ-1"
          ClientId = None
          EngagementId = None
          ActivityTypeId = "ACT-DEV"
          Tags = []
          Description = "Work"
          BusinessPurpose = "Delivery" }
      EntryMethod = Manual
      Billability = Billable
      BillingReference = noBillingReference
      Record = Recorded
      Review = Unsubmitted
      Publication = Unpublished
      Revision = 1
      CreatedAt = at month day 8 0
      LastChangedAt = at month day 8 0
      Reason = None
      WorkItemRef = None
      ExternalRef = None
      Evidence = []
      Lineage = []
      Source = None }

let private config =
    """{"environment":"production","environmentName":"production","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":""}}"""
    |> Storage.parseDeploymentConfig
    |> ok

let private ns = Storage.organizationNamespace config (Storage.binding config |> ok) "ORG-1" |> ok

let mutable private sequence = 0

let private operationContext () : Storage.OperationContext =
    sequence <- sequence + 1

    { Actor =
        { Kind = ActorKind.Human
          Id = ActorId.create "github:octocat" |> ok }
      ProviderIdentity = Some "octocat"
      CorrelationId = CorrelationId.create "corr-1" |> ok
      IdempotencyKey = IdempotencyKey.create $"op-persistence-{sequence:D6}" |> ok
      At = at 10 7 18 0 }

/// Reads the actor's month folders for these dates, as the application does.
let private readMonths (dates: DateOnly list) (state: InMemoryState) =
    let folders = ActivityRecord.monthFolders actor dates |> ok

    let objects, problems =
        folders
        |> List.map (fun folder ->
            let listing = InMemory.list ns folder state |> fst

            match listing with
            | Error failure -> failwith $"%A{failure}"
            | Ok listing ->
                let files, problems = Persistence.recordFiles folder listing

                files
                |> List.choose (fun path ->
                    match InMemory.read ns path state |> fst |> ok with
                    | ReadOutcome.Found found -> Some found
                    | ReadOutcome.Absent -> None),
                problems)
        |> List.unzip

    let snapshot = Persistence.load (List.concat objects)

    { snapshot with
        Problems = List.concat problems @ snapshot.Problems }

/// Executes a command, stores its result as one commit, and returns the next snapshot and store.
let private store (command: Command) (snapshot: Persistence.Snapshot, state: InMemoryState) =
    let before = Persistence.ledgerOf snapshot
    let after = execute context before command |> ok
    let changed = Persistence.changedBetween before after
    let operation = Persistence.operation ns (operationContext ()) "record time" snapshot changed |> ok

    match InMemory.commit operation state with
    | Ok receipt, next -> Persistence.committed changed receipt snapshot, next
    | Error failure, _ -> failwith $"%A{failure}"

let private october = DateOnly(2026, 10, 7)
let private september = DateOnly(2026, 9, 30)

let private storedContent path (state: InMemoryState) =
    match InMemory.read ns path state |> fst |> ok with
    | ReadOutcome.Found found -> found
    | ReadOutcome.Absent -> failwith "absent"

let private codes (diagnostics: Diagnostic list) = diagnostics |> List.map code |> List.sort

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
                    Hash = Some "sha256:abc" } ]
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
        Assert.Equal(original, ActivityRecord.ofBody record.Body |> ok)
        // Stored text is canonical, so equal activities are equal bytes.
        Assert.Equal(text, ActivityRecord.encode original |> ok)

    // A field this version does not define is refused, never ignored.
    let body = ActivityRecord.body rich

    let extended =
        match body with
        | Json.Object members -> Json.Object(("rate", Json.Number 150m) :: members)
        | other -> other

    Assert.True(ActivityRecord.ofBody extended |> Result.isError)

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

// ---- Changing (21, 22) ----------------------------------------------------------

[<Fact>]
let ``each command is one commit, and what is stored reads back as the ledger`` () =
    let snapshot, state =
        (Persistence.empty, InMemory.empty)
        |> store (Record(activity "A-1" (10, 7) (9, 0) 60))
        |> store (Record(activity "A-2" (10, 7) (10, 0) 30))
        |> store (Void("A-2", 1, "entered twice"))

    // Three commands, three commits (plus the provider's initial state).
    Assert.Equal(4, state.History.Length)

    let reloaded = readMonths [ october ] state
    Assert.Empty(reloaded.Problems)
    Assert.Equal<Map<string, Activity>>((Persistence.ledgerOf snapshot).Activities, (Persistence.ledgerOf reloaded).Activities)
    Assert.Equal(snapshot.Activities["A-2"].Revision, reloaded.Activities["A-2"].Revision)
    Assert.Equal(Voided "entered twice", reloaded.Activities["A-2"].Activity.Record)

[<Fact>]
let ``an activity moved to another month moves in the same commit`` () =
    let snapshot, state = (Persistence.empty, InMemory.empty) |> store (Record(activity "A-1" (10, 1) (9, 0) 60))
    let moved = activity "A-1" (9, 30) (9, 0) 60

    let retime =
        { Classification = None
          Billability = None
          BillingReference = None
          Retime = Some(moved.Occurrence, moved.Timing, moved.Minutes)
          Reason = "wrong day" }

    let commitsBefore = state.History.Length
    let snapshot, state = (snapshot, state) |> store (Amend("A-1", 1, retime))

    Assert.Equal(commitsBefore + 1, state.History.Length)
    Assert.Empty((readMonths [ DateOnly(2026, 10, 1) ] state).Activities)
    let september' = readMonths [ september ] state
    Assert.Equal(2, september'.Activities["A-1"].Activity.Revision)
    Assert.Equal(snapshot.Activities["A-1"].Path, september'.Activities["A-1"].Path)

[<Fact>]
let ``independent changes survive, and a stale edit is a conflict decided again by Chrona's rules`` () =
    let base', state =
        (Persistence.empty, InMemory.empty)
        |> store (Record(activity "A-1" (10, 7) (9, 0) 60))
        |> store (Record(activity "A-2" (10, 7) (10, 0) 60))

    let describe text id snapshot =
        Amend(
            id,
            1,
            { Classification = Some { (Persistence.ledgerOf snapshot).Activities[id].Classification with Description = text }
              Billability = None
              BillingReference = None
              Retime = None
              Reason = "edit" }
        )

    // Device A edits A-1.
    let _, state = (base', state) |> store (describe "Device A" "A-1" base')

    // Device B, still on the old state, edits A-2: an independent change, which lands.
    let _, state = (base', state) |> store (describe "Device B" "A-2" base')
    let both = readMonths [ october ] state
    Assert.Equal("Device A", both.Activities["A-1"].Activity.Classification.Description)
    Assert.Equal("Device B", both.Activities["A-2"].Activity.Classification.Description)

    // Device B now edits A-1 from the old state: Arca refuses the stale write...
    let stale = describe "Device B" "A-1" base'
    let before = Persistence.ledgerOf base'
    let changed = execute context before stale |> ok |> Persistence.changedBetween before
    let operation = Persistence.operation ns (operationContext ()) "edit" base' changed |> ok

    match InMemory.commit operation state with
    | Error(StorageFailure.Conflicted _), _ -> ()
    | other -> failwith $"expected a conflict, got %A{fst other}"

    // ...and after reloading, Chrona decides again: the edit was made against
    // revision 1, which no longer exists, so the person must review it.
    Assert.Equal<Diagnostic list>([ RevisionConflict(1, 2) ], Persistence.rerun context (readMonths [ october ] state) stale |> function Error e -> e | Ok _ -> [])

    // A command that does not depend on what changed simply applies again.
    let independent = Record(activity "A-3" (10, 7) (11, 0) 30)
    Assert.Equal<string list>([ "A-3" ], Persistence.rerun context (readMonths [ october ] state) independent |> ok |> List.map _.ActivityId)

// ---- Integrity on load (39) ------------------------------------------------------

let private object (activity: Activity) =
    { Path = ActivityRecord.path activity |> ok
      Content = ActivityRecord.encode activity |> ok
      Revision = Revision $"r-{activity.ActivityId}-{activity.Revision}" }

[<Fact>]
let ``corrupt, misplaced and impossible records are reported and held aside`` () =
    let good = activity "A-1" (10, 7) (9, 0) 60
    let path id = ActivityRecord.path { good with ActivityId = id } |> ok
    let text = (object good).Content

    let objects =
        [ object good
          // Invalid JSON.
          { Path = path "A-2"; Content = "{ broken"; Revision = Revision "r2" }
          // A record from a newer Chrona.
          { Path = path "A-3"
            Content = (object { good with ActivityId = "A-3" }).Content.Replace("\"schemaVersion\":1", "\"schemaVersion\":2")
            Revision = Revision "r3" }
          // A record whose file name is not its id.
          { Path = path "A-4"; Content = text; Revision = Revision "r4" }
          // A record in another month's folder.
          { object { good with ActivityId = "A-5" } with Path = ActivityRecord.path { good with ActivityId = "A-5"; Occurrence = { good.Occurrence with LocalDate = september } } |> ok }
          // An impossible revision.
          object { good with ActivityId = "A-6"; Revision = 0; Timing = DurationOnDate 60 } ]

    let snapshot = Persistence.load objects

    Assert.Equal<string list>(
        [ "CHRONA.INTEGRITY.IMPOSSIBLE_REVISION"
          "CHRONA.INTEGRITY.MISPLACED_RECORD"
          "CHRONA.STORAGE.INVALID_RECORD"
          "CHRONA.STORAGE.INVALID_RECORD"
          "CHRONA.STORAGE.INVALID_RECORD" ],
        codes snapshot.Problems
    )

    Assert.Equal<string list>([ "A-1" ], snapshot.Activities |> Map.keys |> List.ofSeq)
    Assert.Equal(5, snapshot.Unusable.Count)

    // An unusable record is never overwritten on a guess.
    let repaired = { good with ActivityId = "A-2" }
    Assert.Equal<string list>([ "CHRONA.STORAGE.UNSTORABLE_ACTIVITY" ], Persistence.changes snapshot [ repaired ] |> function Error e -> codes e | Ok _ -> [])

[<Fact>]
let ``duplicate ids, overlapping time and broken lineage are detected across what was read`` () =
    let a1 = activity "A-1" (10, 7) (9, 0) 60
    let a1September = activity "A-1" (9, 30) (9, 0) 60
    let a2 = activity "A-2" (10, 7) (11, 0) 60
    let a3 = activity "A-3" (10, 7) (11, 30) 60
    let split = { activity "A-4" (10, 7) (14, 0) 60 with Record = Superseded [ "A-5" ] }
    let child = activity "A-5" (10, 7) (14, 0) 60 // does not name A-4 as its source
    let orphan = { activity "A-6" (10, 7) (16, 0) 30 with Lineage = [ "A-7" ] } // A-7 not read: not judged

    let snapshot = Persistence.load ([ a1; a1September; a2; a3; split; child; orphan ] |> List.map object)

    Assert.Equal<Diagnostic list>(
        [ DuplicateActivityId "A-1"; StoredOverlap("A-2", "A-3"); InvalidLineage("A-4", "A-5") ],
        snapshot.Problems
    )

    Assert.False(snapshot.Activities.ContainsKey "A-1")
    Assert.Equal(2, snapshot.Unusable.Count)

[<Fact>]
let ``a listing cut short is reported, because what was not listed was not validated`` () =
    let _, state =
        (Persistence.empty, InMemory.empty)
        |> store (Record(activity "A-1" (10, 7) (9, 0) 60))
        |> store (Record(activity "A-2" (10, 7) (10, 0) 60))

    let limited = InMemory.arrange (InMemoryFault.ListingLimit 1) state
    Assert.Contains(IncompleteRead "records/chrona.activity/github_3aoctocat/2026/10", (readMonths [ october ] limited).Problems)

// ---- Manual edits (41) -------------------------------------------------------------

[<Fact>]
let ``a record edited outside Chrona is held until it passes Chrona's rules`` () =
    let _, state =
        (Persistence.empty, InMemory.empty)
        |> store (Record(activity "A-1" (10, 7) (9, 0) 60))
        |> store (Record(activity "A-2" (10, 7) (10, 0) 60))

    let edit (replacement: Activity) state =
        let target = ActivityRecord.path replacement |> ok
        let full = (Namespace.resolve ns target |> ok).Path
        InMemory.writeExternally ns.Location full (Some(ActivityRecord.encode replacement |> ok)) state

    let historyOf state (snapshot: Persistence.Snapshot) =
        snapshot.Activities
        |> Map.toList
        |> List.map (fun (_, found) -> RelativePath.render found.Path, InMemory.history ns found.Path state |> fst |> ok)
        |> Map.ofList

    let review state =
        let loaded = readMonths [ october ] state
        Persistence.holdExternalEdits (historyOf state loaded) loaded

    // Someone edits A-1 on github.com so that it overlaps A-2.
    let overlapping = { activity "A-1" (10, 7) (9, 0) 90 with Revision = 2 }
    let held = review (edit overlapping state)

    Assert.Equal<string list>([ "A-2" ], held.Activities |> Map.keys |> List.ofSeq)
    Assert.True(held.HeldForReview.ContainsKey "A-1")
    Assert.Contains(ExternalEdit "records/chrona.activity/github_3aoctocat/2026/10/A-1.json", held.Problems)

    // Chrona does not accept it, and will not write over it either.
    Assert.Equal<Diagnostic list>([ OverlapsActivity "A-2" ], Persistence.acceptExternalEdit context "A-1" held |> function Error e -> e | Ok _ -> [])
    Assert.True(Persistence.changes held [ activity "A-1" (10, 7) (9, 0) 30 ] |> Result.isError)

    // An edit that keeps to the rules is accepted after review.
    let valid = { activity "A-1" (10, 7) (9, 0) 45 with Revision = 2 }
    let accepted = review (edit valid state) |> Persistence.acceptExternalEdit context "A-1" |> ok
    Assert.Equal(45, accepted.Activities["A-1"].Activity.Minutes)
    Assert.Empty(accepted.HeldForReview)

    // Records Chrona wrote itself are trusted as read.
    Assert.Empty((review state).HeldForReview)
