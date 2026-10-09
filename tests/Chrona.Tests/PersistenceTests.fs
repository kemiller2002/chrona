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
    """{"environment":"production","environmentName":"production","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":""},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://chrona.test/"},"organizations":[{"id":"org_acme","displayName":"Acme Consulting","slug":"acme","timeZone":"America/New_York"}]}"""
    |> Chrona.Domain.Deployment.parse
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
                    | ReadOutcome.Absent
                    | ReadOutcome.Erased _ -> None),
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
    | ReadOutcome.Absent
    | ReadOutcome.Erased _ -> failwith "absent"

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

// ---- Accepting an outside edit in the application (41, WI-0035) ---------------------

module Access = Chrona.Domain.Access
module Reconcile = Chrona.Domain.Reconcile
module Stored = Chrona.Domain.Stored

let private roster =
    Access.founded
        "ORG-1"
        { PrincipalId = actor
          Kind = Access.Human
          DisplayName = "octocat" }

[<Fact>]
let ``a person accepts their own outside edit as its next revision, after the rules of a recorded activity`` () =
    let held = { activity "A-1" (10, 7) (9, 0) 45 with Revision = 2 }
    let accepted = Persistence.acceptance context roster [ activity "A-2" (10, 7) (10, 0) 60 ] held |> ok
    Assert.Equal(3, accepted.Revision)
    Assert.Equal(context.At, accepted.LastChangedAt)
    Assert.Equal({ accepted with Revision = 2; LastChangedAt = held.LastChangedAt }, held)

    let refused trusted roster held =
        match Persistence.acceptance context roster trusted held with
        | Error problems -> codes problems
        | Ok accepted -> failwith $"accepted %A{accepted}"

    // Someone else's record, time that overlaps, a missing purpose.
    Assert.Equal<string list>([ "CHRONA.AUTH.ACTOR_MISMATCH" ], refused [] roster { held with ActorId = "github:hubot" })
    Assert.Equal<string list>([ "CHRONA.OVERLAP.OVERLAPS_ACTIVITY" ], refused [ activity "A-2" (10, 7) (9, 30) 60 ] roster held)

    Assert.Equal<string list>(
        [ "CHRONA.ENTRY.MISSING_FIELD" ],
        refused [] roster { held with Classification = { held.Classification with BusinessPurpose = "" } }
    )

    // A state only Chrona's own transitions give is never reached this way.
    Assert.Equal<string list>([ "CHRONA.INTEGRITY.EXTERNAL_STATE_CLAIM" ], refused [] roster { held with Review = Approved })

    Assert.Equal<string list>(
        [ "CHRONA.INTEGRITY.EXTERNAL_STATE_CLAIM"; "CHRONA.INTEGRITY.EXTERNAL_STATE_CLAIM" ],
        refused [] roster { held with Review = Submitted; Publication = Published }
    )

    // Without permission to amend one's own time, nothing is accepted.
    let viewer =
        { roster with
            Members = roster.Members |> Map.map (fun _ m -> { m with Capabilities = set [ Access.ViewOwnTime ] }) }

    Assert.Equal<string list>([ "CHRONA.AUTH.UNAUTHORIZED_CAPABILITY" ], refused [] viewer held)

[<Fact>]
let ``releasing a held record trusts it only as it was reviewed`` () =
    let _, state = (Persistence.empty, InMemory.empty) |> store (Record(activity "A-1" (10, 7) (9, 0) 60))
    let edited = { activity "A-1" (10, 7) (9, 0) 45 with Revision = 2 }
    let target = ActivityRecord.path edited |> ok
    let state = InMemory.writeExternally ns.Location (Namespace.resolve ns target |> ok).Path (Some(ActivityRecord.encode edited |> ok)) state
    let loaded = readMonths [ october ] state

    let histories =
        loaded.Activities
        |> Map.toList
        |> List.map (fun (_, found) -> RelativePath.render found.Path, InMemory.history ns found.Path state |> fst |> ok)
        |> Map.ofList

    let held = Persistence.holdExternalEdits histories loaded

    let other = Persistence.release [ { edited with Minutes = 50 } ] held
    Assert.True(other.HeldForReview.ContainsKey "A-1")
    Assert.Contains(ExternalEdit(RelativePath.render target), other.Problems)

    let released = Persistence.release [ edited ] held
    Assert.Empty(released.HeldForReview)
    Assert.Equal(edited, released.Activities["A-1"].Activity)
    Assert.DoesNotContain(ExternalEdit(RelativePath.render target), released.Problems)

// ---- Deciding a change again on what is stored (21, 26, WI-0035) --------------------

[<Fact>]
let ``a change decided again keeps what still fits and names every divergence with a stable code`` () =
    let snapshot, _ =
        (Persistence.empty, InMemory.empty)
        |> store (Record(activity "A-1" (10, 7) (9, 0) 60))
        |> store (Record(activity "A-2" (10, 7) (11, 0) 60))

    let stored = { Stored.empty with Activities = snapshot }
    let decide activities = Reconcile.decide stored { Stored.nothing with Activities = activities }

    // Already stored as asked (an earlier attempt landed): nothing to write.
    let already = snapshot.Activities["A-1"].Activity
    Assert.Equal(Ok Stored.nothing, decide [ already ])

    // Independent new time, and the next revision of a stored record, fit.
    let next = { already with Revision = 2; Classification = { already.Classification with Description = "Corrected" } }
    let independent = activity "A-3" (10, 7) (14, 0) 30
    Assert.Equal(Ok { Stored.nothing with Activities = [ next; independent ] }, decide [ next; independent ])

    // Decided on an older revision: a semantic conflict, never last-write-wins.
    let stale = { already with Revision = 1; Classification = { already.Classification with Description = "Stale" } }

    match decide [ stale ] with
    | Error [ Reconcile.ActivityChanged(mine, Some current) as divergence ] ->
        Assert.Equal("Stale", mine.Classification.Description)
        Assert.Equal(already, current)
        Assert.Equal("CHRONA.CONCURRENCY.SEMANTIC_CONFLICT", code (Reconcile.diagnostic divergence))
    | other -> failwith $"%A{other}"

    // New time that overlaps time stored since.
    match decide [ activity "A-4" (10, 7) (11, 30) 60 ] with
    | Error [ Reconcile.OverlapsStored(mine, other) as divergence ] ->
        Assert.Equal("A-4", mine.ActivityId)
        Assert.Equal("A-2", other.ActivityId)
        Assert.Equal("CHRONA.OVERLAP.OVERLAPS_ACTIVITY", code (Reconcile.diagnostic divergence))
    | other -> failwith $"%A{other}"

    // A member removed elsewhere already.
    Assert.Equal(Error [ Reconcile.MemberGone "github:hubot" ], Reconcile.decide stored { Stored.nothing with Removed = [ "github:hubot" ] })
    Assert.Equal("CHRONA.CONCURRENCY.KEPT_CHANGING", code (Reconcile.diagnostic Reconcile.KeptChanging))

// ---- Changes read back from the queue of unsent changes (WI-0033) -------------------

module MemberRecord = Chrona.Domain.MemberRecord

[<Fact>]
let ``an actor's path segment reads back as the actor, and nothing else does`` () =
    for actorId in [ "github:583231"; "github:octocat"; "local_person"; "a.b-c"; "é:ü" ] do
        Assert.Equal(Some actorId, ActivityRecord.actorOfSegment (ActivityRecord.actorSegment actorId))

    Assert.Equal(None, ActivityRecord.actorOfSegment "github_3")
    Assert.Equal(None, ActivityRecord.actorOfSegment "github_zz")
    Assert.Equal(None, ActivityRecord.actorOfSegment "-leading")

[<Fact>]
let ``a queued operation's changes read back as the records a command changed`` () =
    let snapshot, _ = (Persistence.empty, InMemory.empty) |> store (Record(activity "A-1" (10, 7) (9, 0) 60))
    let stored = { Stored.empty with Activities = snapshot }
    let moved = { activity "A-1" (11, 2) (9, 0) 60 with Revision = 2 }
    let fresh = activity "A-2" (10, 7) (11, 0) 30

    let membership: Access.Membership =
        { Principal =
            { PrincipalId = "github:1001"
              Kind = Access.Human
              DisplayName = "hubot" }
          Capabilities = Access.Grants.ownTime
          Revision = 1 }

    let changed = { Stored.nothing with Activities = [ moved; fresh ]; Members = [ membership ] }
    let changes = Stored.changes stored changed |> ok

    // The month move is a delete and a create; it reads back as the activity.
    Assert.Contains(changes, fun change -> match change with Change.Delete _ -> true | _ -> false)
    let readBack = Stored.changedOf changes |> ok
    Assert.Equal<Activity list>([ moved; fresh ] |> List.sortBy _.ActivityId, readBack.Activities |> List.sortBy _.ActivityId)
    Assert.Equal<Access.Membership list>([ membership ], readBack.Members)

    // A member's removal reads back as the principal removed.
    let path = MemberRecord.path "github:1001" |> ok
    Assert.Equal(Ok { Stored.nothing with Removed = [ "github:1001" ] }, Stored.changedOf [ Change.Delete(path, Revision "abc") ])

    // Content that is not a Chrona record is refused, not guessed at.
    Assert.True(Stored.changedOf [ Change.Create(path, "{}") ] |> Result.isError)

[<Fact>]
let ``unsent changes are laid over what is stored for deciding and showing, keeping stored revisions`` () =
    let snapshot, _ = (Persistence.empty, InMemory.empty) |> store (Record(activity "A-1" (10, 7) (9, 0) 60))
    let stored = { Stored.empty with Activities = snapshot }
    let amended = { snapshot.Activities["A-1"].Activity with Revision = 2; Minutes = 60; Classification = { snapshot.Activities["A-1"].Activity.Classification with Description = "Amended" } }
    let fresh = activity "A-2" (10, 7) (11, 0) 30
    let shown = Stored.overlay { Stored.nothing with Activities = [ amended; fresh ] } stored

    Assert.Equal("Amended", shown.Activities.Activities["A-1"].Activity.Classification.Description)
    Assert.Equal(snapshot.Activities["A-1"].Revision, shown.Activities.Activities["A-1"].Revision)
    Assert.Equal(Revision "", shown.Activities.Activities["A-2"].Revision)

    // A next change is decided on it: the amended record's next revision fits.
    let next = { amended with Revision = 3 }
    Assert.True(Reconcile.decide shown { Stored.nothing with Activities = [ next ] } |> Result.isOk)

// ---- The activity index (WI-0034) -------------------------------------------------------

module ActivityIndex = Chrona.Domain.ActivityIndex

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

module AuditRecord = Chrona.Domain.AuditRecord

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
    Assert.Equal(Ok { Stored.nothing with Audit = [ audited ] }, Stored.changedOf [ Change.Create(path, content) ])

    // Written once: an entry already stored is not written again.
    let stored = { Stored.empty with Audit = Map.ofList [ RelativePath.render path, audited ] }
    Assert.Equal(Ok [], Stored.changes stored { Stored.nothing with Audit = [ audited ] })
    Assert.Empty(AuditRecord.place [] [ entry ])
