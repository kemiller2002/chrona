/// What is stored once changes are laid over it, while they wait to be sent
/// (WI-0033), or once a commit of them has landed. Pure.
module Chrona.Domain.StoredOverlay

open Arca
open Chrona.Domain.Diagnostics
open Chrona.Domain.Stored
open Chrona.Domain.StoredChanges

/// What is stored with changes not yet stored laid over it, as the person
/// sees it while those changes wait to be sent (WI-0033). Records keep the
/// path and revision of what is stored where there is one; a record not
/// stored yet has no revision (`Revision ""`). It is for deciding and
/// showing, never for building a commit.
let overlay (changed: Changed) (stored: Stored) =
    let unsent = Revision ""

    let activities =
        changed.Activities
        |> List.fold
            (fun (snapshot: Persistence.Snapshot) (activity: Activity.Activity) ->
                match ActivityRecord.path activity with
                | Error _ -> snapshot
                | Ok path ->
                    let revision =
                        snapshot.Activities.TryFind activity.ActivityId |> Option.map _.Revision |> Option.defaultValue unsent

                    // An accepted outside edit waiting to be sent is no longer held.
                    let held = snapshot.HeldForReview.TryFind activity.ActivityId

                    { snapshot with
                        Activities =
                            Map.add
                                activity.ActivityId
                                { Activity = activity
                                  Path = path
                                  Revision = held |> Option.map _.Revision |> Option.defaultValue revision
                                  ContentHash = "" }
                                snapshot.Activities
                        HeldForReview = snapshot.HeldForReview.Remove activity.ActivityId
                        Problems =
                            match held with
                            | Some found ->
                                snapshot.Problems
                                |> List.filter (fun problem -> problem <> ExternalEdit(RelativePath.render found.Path))
                            | None -> snapshot.Problems })
            stored.Activities

    let references =
        changed.References
        |> List.fold
            (fun map (item: Reference.Item) ->
                match ReferenceRecord.path item with
                | Error _ -> map
                | Ok path ->
                    let revision =
                        map |> Map.tryFind (referenceKey item) |> Option.map (fun (found: StoredReference) -> found.Revision) |> Option.defaultValue unsent

                    Map.add (referenceKey item) ({ Item = item; Path = path; Revision = revision }: StoredReference) map)
            stored.References

    let members =
        changed.Members
        |> List.fold
            (fun map (membership: Access.Membership) ->
                match MemberRecord.path membership.Principal.PrincipalId with
                | Error _ -> map
                | Ok path ->
                    let id = membership.Principal.PrincipalId
                    let revision = map |> Map.tryFind id |> Option.map (fun (found: StoredMember) -> found.Revision) |> Option.defaultValue unsent
                    Map.add id ({ Membership = membership; Path = path; Revision = revision }: StoredMember) map)
            stored.Members
        |> fun map -> changed.Removed |> List.fold (fun map id -> Map.remove id map) map

    let attestations =
        changed.Attestations
        |> List.fold
            (fun map (attestation: Review.Attestation) ->
                match AttestationRecord.path attestation with
                | Ok path -> Map.add (RelativePath.render path) attestation map
                | Error _ -> map)
            stored.Attestations

    let audit =
        changed.Audit
        |> List.fold
            (fun map (audited: AuditRecord.Audited) ->
                match AuditRecord.path audited with
                | Ok path -> Map.add (RelativePath.render path) audited map
                | Error _ -> map)
            stored.Audit

    let periods =
        match changed.Periods, PeriodConfigRecord.path () with
        | Some periods, Ok path ->
            Some
                { Periods = periods
                  Path = path
                  Revision = stored.Periods |> Option.map _.Revision |> Option.defaultValue unsent }
        | _ -> stored.Periods

    let reviews =
        changed.Reviews
        |> List.fold
            (fun map (review: PeriodReview.PeriodReview) ->
                match ReviewRecord.path review with
                | Ok path -> Map.add (RelativePath.render path) review map
                | Error _ -> map)
            stored.Reviews

    let candidates =
        changed.Candidates
        |> List.fold
            (fun map (candidate: Observations.Candidate) ->
                match CandidateRecord.path candidate with
                | Ok path ->
                    let revision = map |> Map.tryFind candidate.CandidateId |> Option.map (fun (found: StoredCandidate) -> found.Revision) |> Option.defaultValue unsent
                    Map.add candidate.CandidateId ({ Candidate = candidate; Path = path; Revision = revision }: StoredCandidate) map
                | Error _ -> map)
            stored.Candidates

    { stored with
        Activities = activities
        References = references
        Members = members
        Attestations = attestations
        Audit = audit
        Periods = periods
        Reviews = reviews
        Candidates = candidates }

/// What was stored after a commit of these records landed with `receipt`.
let committed (changed: Changed) (receipt: CommitReceipt) (stored: Stored) =
    let revisionOf path =
        receipt.Revisions.TryFind(RelativePath.render path) |> Option.flatten

    let references' =
        changed.References
        |> List.fold
            (fun map (item: Reference.Item) ->
                match ReferenceRecord.path item with
                | Ok target ->
                    match revisionOf target with
                    | Some revision -> Map.add (referenceKey item) { Item = item; Path = target; Revision = revision } map
                    | None -> map
                | Error _ -> map)
            stored.References

    let attestations' =
        changed.Attestations
        |> List.fold
            (fun map (attestation: Review.Attestation) ->
                match AttestationRecord.path attestation with
                | Ok target -> Map.add (RelativePath.render target) attestation map
                | Error _ -> map)
            stored.Attestations

    let members' =
        changed.Members
        |> List.fold
            (fun map (membership: Access.Membership) ->
                match MemberRecord.path membership.Principal.PrincipalId with
                | Ok target ->
                    match revisionOf target with
                    | Some revision ->
                        Map.add
                            membership.Principal.PrincipalId
                            { Membership = membership
                              Path = target
                              Revision = revision }
                            map
                    | None -> map
                | Error _ -> map)
            stored.Members
        |> fun map -> changed.Removed |> List.fold (fun map principalId -> Map.remove principalId map) map

    let audit' =
        changed.Audit
        |> List.fold
            (fun map (audited: AuditRecord.Audited) ->
                match AuditRecord.path audited with
                | Ok target -> Map.add (RelativePath.render target) audited map
                | Error _ -> map)
            stored.Audit

    let periods' =
        match changed.Periods, PeriodConfigRecord.path () with
        | Some periods, Ok target ->
            match revisionOf target with
            | Some revision ->
                Some
                    { Periods = periods
                      Path = target
                      Revision = revision }
            | None -> stored.Periods
        | _ -> stored.Periods

    let reviews' =
        changed.Reviews
        |> List.fold
            (fun map (review: PeriodReview.PeriodReview) ->
                match ReviewRecord.path review with
                | Ok target -> Map.add (RelativePath.render target) review map
                | Error _ -> map)
            stored.Reviews

    let candidates' =
        changed.Candidates
        |> List.fold
            (fun map (candidate: Observations.Candidate) ->
                match CandidateRecord.path candidate with
                | Ok target ->
                    match revisionOf target with
                    | Some revision -> Map.add candidate.CandidateId { Candidate = candidate; Path = target; Revision = revision } map
                    | None -> map
                | Error _ -> map)
            stored.Candidates

    { stored with
        Activities = Persistence.committed changed.Activities receipt stored.Activities
        References = references'
        Attestations = attestations'
        Members = members'
        Audit = audit'
        Periods = periods'
        Reviews = reviews'
        Candidates = candidates' }
