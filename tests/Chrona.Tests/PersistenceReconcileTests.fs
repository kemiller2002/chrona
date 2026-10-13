/// Accepting an outside edit, deciding a change again on what is stored, and
/// changes read back from the queue of unsent changes (21, 26, 41; WI-0033,
/// WI-0035).
[<Xunit.Collection("Persistence")>]
module Chrona.Tests.PersistenceReconcileTests

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

// ---- Accepting an outside edit in the application (41, WI-0035) ---------------------

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
    let decide activities = Reconcile.decide stored { StoredChanges.nothing with Activities = activities }

    // Already stored as asked (an earlier attempt landed): nothing to write.
    let already = snapshot.Activities["A-1"].Activity
    Assert.Equal(Ok StoredChanges.nothing, decide [ already ])

    // Independent new time, and the next revision of a stored record, fit.
    let next = { already with Revision = 2; Classification = { already.Classification with Description = "Corrected" } }
    let independent = activity "A-3" (10, 7) (14, 0) 30
    Assert.Equal(Ok { StoredChanges.nothing with Activities = [ next; independent ] }, decide [ next; independent ])

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
    Assert.Equal(Error [ Reconcile.MemberGone "github:hubot" ], Reconcile.decide stored { StoredChanges.nothing with Removed = [ "github:hubot" ] })
    Assert.Equal("CHRONA.CONCURRENCY.KEPT_CHANGING", code (Reconcile.diagnostic Reconcile.KeptChanging))

// ---- Changes read back from the queue of unsent changes (WI-0033) -------------------

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

    let changed = { StoredChanges.nothing with Activities = [ moved; fresh ]; Members = [ membership ] }
    let changes = StoredChanges.changes stored changed |> ok

    // The month move is a delete and a create; it reads back as the activity.
    Assert.Contains(changes, fun change -> match change with Change.Delete _ -> true | _ -> false)
    let readBack = StoredChanges.changedOf changes |> ok
    Assert.Equal<Activity list>([ moved; fresh ] |> List.sortBy _.ActivityId, readBack.Activities |> List.sortBy _.ActivityId)
    Assert.Equal<Access.Membership list>([ membership ], readBack.Members)

    // A member's removal reads back as the principal removed.
    let path = MemberRecord.path "github:1001" |> ok
    Assert.Equal(Ok { StoredChanges.nothing with Removed = [ "github:1001" ] }, StoredChanges.changedOf [ Change.Delete(path, Revision "abc") ])

    // Content that is not a Chrona record is refused, not guessed at.
    Assert.True(StoredChanges.changedOf [ Change.Create(path, "{}") ] |> Result.isError)

[<Fact>]
let ``unsent changes are laid over what is stored for deciding and showing, keeping stored revisions`` () =
    let snapshot, _ = (Persistence.empty, InMemory.empty) |> store (Record(activity "A-1" (10, 7) (9, 0) 60))
    let stored = { Stored.empty with Activities = snapshot }
    let amended = { snapshot.Activities["A-1"].Activity with Revision = 2; Minutes = 60; Classification = { snapshot.Activities["A-1"].Activity.Classification with Description = "Amended" } }
    let fresh = activity "A-2" (10, 7) (11, 0) 30
    let shown = StoredOverlay.overlay { StoredChanges.nothing with Activities = [ amended; fresh ] } stored

    Assert.Equal("Amended", shown.Activities.Activities["A-1"].Activity.Classification.Description)
    Assert.Equal(snapshot.Activities["A-1"].Revision, shown.Activities.Activities["A-1"].Revision)
    Assert.Equal(Revision "", shown.Activities.Activities["A-2"].Revision)

    // A next change is decided on it: the amended record's next revision fits.
    let next = { amended with Revision = 3 }
    Assert.True(Reconcile.decide shown { StoredChanges.nothing with Activities = [ next ] } |> Result.isOk)
