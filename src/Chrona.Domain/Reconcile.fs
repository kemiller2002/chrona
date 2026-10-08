/// Deciding a change again after the stored records moved (requirements
/// expansion 12, 21, 23, 26, 34; WI-0035).
///
/// A change is decided on the records as they were read. When the
/// repository has moved by the time it is committed, Chrona's own rules
/// decide it again on what is stored now; a clean Git merge is not proof.
/// What still fits is written, records already stored as asked are not
/// written again, and others' independent changes are kept. What no longer
/// fits is a divergence: never resolved by last-write-wins, never silently
/// dropped, but kept for the person to resolve, with a stable diagnostic.
///
/// Pure.
module Chrona.Domain.Reconcile

open Chrona.Domain.Activity
open Chrona.Domain.Diagnostics

/// What no longer fits what is stored. Each case carries the person's
/// version (`mine`) and, where there is one, what is stored now.
type Divergence =
    /// The activity changed elsewhere after this change was decided on an
    /// earlier revision of it (or it is no longer stored as Chrona's).
    | ActivityChanged of mine: Activity * stored: Activity option
    /// The reference item changed elsewhere first.
    | ReferenceChanged of mine: Reference.Item * stored: Reference.Item option
    /// The membership changed elsewhere first.
    | MembershipChanged of mine: Access.Membership * stored: Access.Membership option
    /// The member to remove was already removed.
    | MemberGone of principalId: string
    /// The person's time overlaps time stored since.
    | OverlapsStored of mine: Activity * stored: Activity
    /// The repository moved on every attempt; nothing was found wrong.
    | KeptChanging

/// The divergence's stable diagnostic (26: unresolved semantic merge
/// conflict; overlap and a busy repository keep their own codes).
let diagnostic =
    function
    | ActivityChanged(mine, _) -> SemanticConflict("activity", mine.ActivityId)
    | ReferenceChanged(mine, _) -> SemanticConflict(Reference.kindName mine.Kind, mine.Id)
    | MembershipChanged(mine, _) -> SemanticConflict("member", mine.Principal.PrincipalId)
    | MemberGone principalId -> SemanticConflict("member", principalId)
    | OverlapsStored(_, stored) -> OverlapsActivity stored.ActivityId
    | KeptChanging -> StoreKeptChanging

/// Whether `revision` is the next one after what is stored (or the first,
/// when nothing is).
let private follows (stored: int option) (revision: int) =
    match stored with
    | Some current -> revision = current + 1
    | None -> revision = 1

/// The change decided again on what is stored: the records still to write,
/// or every divergence found. A record already stored exactly as asked (an
/// earlier attempt landed) needs nothing; one the change made must be the
/// next revision of the stored one; new or changed time must not overlap
/// the time stored for other activities.
let decide (stored: Stored.Stored) (change: Stored.Changed) : Result<Stored.Changed, Divergence list> =
    let activities = stored.Activities.Activities
    let storedActivity (id: string) = activities.TryFind id |> Option.map _.Activity
    let storedReference (item: Reference.Item) = stored.References.TryFind(Stored.referenceKey item) |> Option.map _.Item
    let storedMember (id: string) = stored.Members.TryFind id |> Option.map _.Membership

    let pending = change.Activities |> List.filter (fun mine -> storedActivity mine.ActivityId <> Some mine)
    let references = change.References |> List.filter (fun mine -> storedReference mine <> Some mine)
    let members = change.Members |> List.filter (fun mine -> storedMember mine.Principal.PrincipalId <> Some mine)

    let changedActivities =
        pending
        |> List.filter (fun mine -> not (follows (storedActivity mine.ActivityId |> Option.map _.Revision) mine.Revision))
        |> List.map (fun mine -> ActivityChanged(mine, storedActivity mine.ActivityId))

    let changedReferences =
        references
        |> List.filter (fun mine -> not (follows (storedReference mine |> Option.map _.Revision) mine.Revision))
        |> List.map (fun mine -> ReferenceChanged(mine, storedReference mine))

    let changedMembers =
        members
        |> List.filter (fun mine -> not (follows (storedMember mine.Principal.PrincipalId |> Option.map _.Revision) mine.Revision))
        |> List.map (fun mine -> MembershipChanged(mine, storedMember mine.Principal.PrincipalId))

    let gone =
        change.Removed |> List.filter (stored.Members.ContainsKey >> not) |> List.map MemberGone

    let mineIds = change.Activities |> List.map _.ActivityId |> Set.ofList

    let others =
        activities
        |> Map.toList
        |> List.map (fun (_, found) -> found.Activity)
        |> List.filter (fun other -> not (mineIds.Contains other.ActivityId) && consumesTime other)

    let overlapping =
        pending
        |> List.filter consumesTime
        |> List.collect (fun mine ->
            Overlap.overlapping others mine
            |> List.choose (fun id -> others |> List.tryFind (fun other -> other.ActivityId = id))
            |> List.map (fun other -> OverlapsStored(mine, other)))

    match changedActivities @ changedReferences @ changedMembers @ gone @ overlapping with
    | [] ->
        Ok
            { change with
                Activities = pending
                References = references
                Members = members }
    | divergences -> Error divergences
