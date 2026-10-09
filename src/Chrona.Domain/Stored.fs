/// Everything an organization's folder holds for the application, as far as
/// Chrona may trust it: activities (`Persistence`), reference items and
/// attestations, loaded together, changed together in one commit, and
/// tracked with the revisions last read.
///
/// Pure.
module Chrona.Domain.Stored

open Arca
open Chrona.Domain.Diagnostics

/// A reference item as read from storage.
type StoredReference =
    { Item: Reference.Item
      Path: RelativePath
      Revision: Revision }

/// The period configuration as read from storage.
type StoredConfiguration =
    { Periods: PeriodConfigRecord.StoredPeriods
      Path: RelativePath
      Revision: Revision }

/// A candidate as read from storage.
type StoredCandidate =
    { Candidate: Observations.Candidate
      Path: RelativePath
      Revision: Revision }

/// A membership as read from storage.
type StoredMember =
    { Membership: Access.Membership
      Path: RelativePath
      Revision: Revision }

[<NoComparison>]
type Stored =
    { Activities: Persistence.Snapshot
      /// Reference items, by `<kind>:<id>`.
      References: Map<string, StoredReference>
      /// Attestations, by namespace-relative path. They are immutable.
      Attestations: Map<string, Review.Attestation>
      /// The organization's members, by principal id.
      Members: Map<string, StoredMember>
      /// Audit entries, by namespace-relative path. They are immutable.
      Audit: Map<string, AuditRecord.Audited>
      /// The organization's period configuration, when it was ever saved.
      Periods: StoredConfiguration option
      /// Periods' review steps, by namespace-relative path. They are immutable.
      Reviews: Map<string, PeriodReview.PeriodReview>
      /// Observations' candidates read, by candidate id (WI-0038).
      Candidates: Map<string, StoredCandidate>
      /// Integrity problems of reference items and attestations.
      Problems: Diagnostic list }

let empty =
    { Activities = Persistence.empty
      References = Map.empty
      Attestations = Map.empty
      Members = Map.empty
      Audit = Map.empty
      Periods = None
      Reviews = Map.empty
      Candidates = Map.empty
      Problems = [] }

/// The key a reference item is tracked by.
let referenceKey (item: Reference.Item) = $"{Reference.kindName item.Kind}:{item.Id}"

/// Every problem found, activities' first.
let problems (stored: Stored) = stored.Activities.Problems @ stored.Problems

let private decodeWith (schema: SchemaSupport) (ofBody: Json -> Codec.Decoded<'a>) (key: RecordKey) (stored: StoredObject) =
    let where = RelativePath.render stored.Path

    Integrity.validate key schema Record.DefaultMaxBytes stored
    |> Result.mapError (fun _ -> InvalidStoredRecord(where, "not a valid record of its type"))
    |> Result.bind (fun valid -> ofBody valid.Record.Body |> Result.mapError (fun detail -> InvalidStoredRecord(where, detail)))

/// Validates the objects read from the organization's folder: activity month
/// folders, reference folders and attestation month folders.
let load (objects: StoredObject list) : Stored =
    let typed (stored: StoredObject) =
        Layout.keyOf stored.Path |> Option.map (fun key -> key, stored)

    let ofType recordType =
        objects |> List.choose typed |> List.filter (fun (key, _) -> key.Type = recordType)

    let activities =
        objects
        |> List.filter (fun stored ->
            match Layout.keyOf stored.Path with
            | Some key -> key.Type = ActivityRecord.recordType
            | None -> false)

    let references, referenceProblems =
        ofType ReferenceRecord.recordType
        |> List.map (fun (key, stored) ->
            decodeWith ReferenceRecord.schema ReferenceRecord.ofBody key stored
            |> Result.bind (fun item ->
                match ReferenceRecord.path item with
                | Ok expected when expected = stored.Path ->
                    Ok
                        { Item = item
                          Path = stored.Path
                          Revision = stored.Revision }
                | Ok _ -> Error(MisplacedRecord(RelativePath.render stored.Path))
                | Error diagnostic -> Error diagnostic))
        |> List.partition Result.isOk

    let attestations, attestationProblems =
        ofType AttestationRecord.recordType
        |> List.map (fun (key, stored) ->
            decodeWith AttestationRecord.schema AttestationRecord.ofBody key stored
            |> Result.bind (fun attestation ->
                match AttestationRecord.path attestation with
                | Ok expected when expected = stored.Path -> Ok(RelativePath.render stored.Path, attestation)
                | Ok _ -> Error(MisplacedRecord(RelativePath.render stored.Path))
                | Error diagnostic -> Error diagnostic))
        |> List.partition Result.isOk

    let audits, auditProblems =
        ofType AuditRecord.recordType
        |> List.map (fun (key, stored) ->
            decodeWith AuditRecord.schema AuditRecord.ofBody key stored
            |> Result.bind (fun audited ->
                match AuditRecord.path audited with
                | Ok expected when expected = stored.Path -> Ok(RelativePath.render stored.Path, audited)
                | Ok _ -> Error(MisplacedRecord(RelativePath.render stored.Path))
                | Error diagnostic -> Error diagnostic))
        |> List.partition Result.isOk

    let periods, periodProblems =
        ofType PeriodConfigRecord.recordType
        |> List.map (fun (key, stored) ->
            // The zone is the organization's; it is not part of the record.
            decodeWith PeriodConfigRecord.schema (PeriodConfigRecord.ofBody "") key stored
            |> Result.bind (fun periods ->
                match PeriodConfigRecord.path () with
                | Ok expected when expected = stored.Path ->
                    Ok
                        { Periods = periods
                          Path = stored.Path
                          Revision = stored.Revision }
                | Ok _ -> Error(MisplacedRecord(RelativePath.render stored.Path))
                | Error diagnostic -> Error diagnostic))
        |> List.partition Result.isOk

    let reviews, reviewProblems =
        ofType ReviewRecord.recordType
        |> List.map (fun (key, stored) ->
            decodeWith ReviewRecord.schema ReviewRecord.ofBody key stored
            |> Result.bind (fun review ->
                match ReviewRecord.path review with
                | Ok expected when expected = stored.Path -> Ok(RelativePath.render stored.Path, review)
                | Ok _ -> Error(MisplacedRecord(RelativePath.render stored.Path))
                | Error diagnostic -> Error diagnostic))
        |> List.partition Result.isOk

    let candidates, candidateProblems =
        ofType CandidateRecord.recordType
        |> List.map (fun (key, stored) ->
            decodeWith CandidateRecord.schema CandidateRecord.ofBody key stored
            |> Result.bind (fun candidate ->
                match CandidateRecord.path candidate with
                | Ok expected when expected = stored.Path ->
                    Ok
                        { Candidate = candidate
                          Path = stored.Path
                          Revision = stored.Revision }
                | Ok _ -> Error(MisplacedRecord(RelativePath.render stored.Path))
                | Error diagnostic -> Error diagnostic))
        |> List.partition Result.isOk

    let members, memberProblems =
        ofType MemberRecord.recordType
        |> List.map (fun (key, stored) ->
            decodeWith MemberRecord.schema MemberRecord.ofBody key stored
            |> Result.bind (fun membership ->
                match MemberRecord.path membership.Principal.PrincipalId with
                | Ok expected when expected = stored.Path ->
                    Ok
                        { Membership = membership
                          Path = stored.Path
                          Revision = stored.Revision }
                | Ok _ -> Error(MisplacedRecord(RelativePath.render stored.Path))
                | Error diagnostic -> Error diagnostic))
        |> List.partition Result.isOk

    let foreign =
        objects
        |> List.filter (fun stored ->
            match Layout.keyOf stored.Path with
            | Some key ->
                key.Type <> ActivityRecord.recordType
                && key.Type <> ReferenceRecord.recordType
                && key.Type <> AttestationRecord.recordType
                && key.Type <> MemberRecord.recordType
                && key.Type <> AuditRecord.recordType
                && key.Type <> PeriodConfigRecord.recordType
                && key.Type <> ReviewRecord.recordType
                && key.Type <> CandidateRecord.recordType
            | None -> true)
        |> List.map (fun stored -> InvalidStoredRecord(RelativePath.render stored.Path, "not a record Chrona keeps here"))

    let oks results =
        results
        |> List.choose (function
            | Ok value -> Some value
            | Error _ -> None)

    let errors results =
        results
        |> List.choose (function
            | Error diagnostic -> Some diagnostic
            | Ok _ -> None)

    { Activities = Persistence.load activities
      References = oks references |> List.map (fun found -> referenceKey found.Item, found) |> Map.ofList
      Attestations = oks attestations |> Map.ofList
      Members = oks members |> List.map (fun found -> found.Membership.Principal.PrincipalId, found) |> Map.ofList
      Audit = oks audits |> Map.ofList
      Periods = oks periods |> List.tryHead
      Reviews = oks reviews |> Map.ofList
      Candidates = oks candidates |> List.map (fun found -> found.Candidate.CandidateId, found) |> Map.ofList
      Problems =
        errors referenceProblems
        @ errors attestationProblems
        @ errors memberProblems
        @ errors auditProblems
        @ errors periodProblems
        @ errors reviewProblems
        @ errors candidateProblems
        @ foreign }

/// The organization's roster, from what was read.
let roster (organizationId: string) (stored: Stored) : Access.Roster =
    { OrganizationId = organizationId
      Members = stored.Members |> Map.map (fun _ found -> found.Membership) }

/// The organization's reference catalogue, from what was read.
let catalogue (organizationId: string) (stored: Stored) : Reference.Catalogue =
    { OrganizationId = organizationId
      Items = stored.References |> Map.toList |> List.map (fun (_, found) -> (found.Item.Kind, found.Item.Id), found.Item) |> Map.ofList }

/// The attestations read, oldest first.
let attestations (stored: Stored) =
    stored.Attestations |> Map.toList |> List.map snd |> List.sortBy _.At

/// The periods' review steps read, oldest first.
let reviews (stored: Stored) =
    stored.Reviews |> Map.toList |> List.map snd |> List.sortBy _.At

/// The audit trail as stored, oldest first.
let audit (stored: Stored) =
    stored.Audit
    |> Map.toList
    |> List.map (fun (_, audited) -> audited.Entry)
    |> List.sortBy (fun entry -> entry.At, entry.ResultingRevisions |> List.map snd |> List.fold max 0, entry.Command)

/// The records one command changes.
type Changed =
    { Activities: Activity.Activity list
      References: Reference.Item list
      Attestations: Review.Attestation list
      /// Memberships admitted or changed.
      Members: Access.Membership list
      /// Principals removed from the roster.
      Removed: string list
      /// The audit entries of the command, where each is kept.
      Audit: AuditRecord.Audited list
      /// The period configuration saved.
      Periods: PeriodConfigRecord.StoredPeriods option
      /// Periods' review steps made.
      Reviews: PeriodReview.PeriodReview list
      /// Observations' candidates made or decided (WI-0038).
      Candidates: Observations.Candidate list
      /// Processing receipts issued; each written once.
      Receipts: Observations.Receipt list
      /// Producer inbox files done with, at the revision read: removed with
      /// their receipt.
      Consumed: (RelativePath * Revision) list }

/// Nothing changed.
let nothing =
    { Activities = []
      References = []
      Attestations = []
      Members = []
      Removed = []
      Audit = []
      Periods = None
      Reviews = []
      Candidates = []
      Receipts = []
      Consumed = [] }

/// The Arca changes that store a command's records on top of what was read:
/// activities as `Persistence.changes`, reference items and memberships
/// created or updated at the revision last read, removed members deleted
/// at it, and attestations created once (an attestation already stored is
/// never written again).
let changes (stored: Stored) (changed: Changed) : Result<Change list, Diagnostic list> =
    let referenceChange (item: Reference.Item) =
        match ReferenceRecord.path item, ReferenceRecord.encode item with
        | Ok target, Ok content ->
            match stored.References.TryFind(referenceKey item) with
            | Some found -> Ok [ Change.Update(found.Path, content, found.Revision) ]
            | None -> Ok [ Change.Create(target, content) ]
        | Error diagnostic, _
        | _, Error diagnostic -> Error [ diagnostic ]

    let attestationChange (attestation: Review.Attestation) =
        match AttestationRecord.path attestation, AttestationRecord.encode attestation with
        | Ok target, Ok _ when stored.Attestations.ContainsKey(RelativePath.render target) -> Ok []
        | Ok target, Ok content -> Ok [ Change.Create(target, content) ]
        | Error diagnostic, _
        | _, Error diagnostic -> Error [ diagnostic ]

    let memberChange (membership: Access.Membership) =
        match MemberRecord.path membership.Principal.PrincipalId, MemberRecord.encode membership with
        | Ok target, Ok content ->
            match stored.Members.TryFind membership.Principal.PrincipalId with
            | Some found -> Ok [ Change.Update(found.Path, content, found.Revision) ]
            | None -> Ok [ Change.Create(target, content) ]
        | Error diagnostic, _
        | _, Error diagnostic -> Error [ diagnostic ]

    let auditChange (audited: AuditRecord.Audited) =
        match AuditRecord.path audited, AuditRecord.encode audited with
        | Ok target, Ok _ when stored.Audit.ContainsKey(RelativePath.render target) -> Ok []
        | Ok target, Ok content -> Ok [ Change.Create(target, content) ]
        | Error diagnostic, _
        | _, Error diagnostic -> Error [ diagnostic ]

    let periodsChange (periods: PeriodConfigRecord.StoredPeriods) =
        match PeriodConfigRecord.path (), PeriodConfigRecord.encode periods with
        | Ok target, Ok content ->
            match stored.Periods with
            | Some found -> Ok [ Change.Update(found.Path, content, found.Revision) ]
            | None -> Ok [ Change.Create(target, content) ]
        | Error diagnostic, _
        | _, Error diagnostic -> Error [ diagnostic ]

    let reviewChange (review: PeriodReview.PeriodReview) =
        match ReviewRecord.path review, ReviewRecord.encode review with
        | Ok target, Ok _ when stored.Reviews.ContainsKey(RelativePath.render target) -> Ok []
        | Ok target, Ok content -> Ok [ Change.Create(target, content) ]
        | Error diagnostic, _
        | _, Error diagnostic -> Error [ diagnostic ]

    let candidateChange (candidate: Observations.Candidate) =
        match CandidateRecord.path candidate, CandidateRecord.encode candidate with
        | Ok target, Ok content ->
            match stored.Candidates.TryFind candidate.CandidateId with
            | Some found when found.Path = target -> Ok [ Change.Update(target, content, found.Revision) ]
            // Decided: it moves from the open folder to its month.
            | Some found -> Ok [ Change.Delete(found.Path, found.Revision); Change.Create(target, content) ]
            | None -> Ok [ Change.Create(target, content) ]
        | Error diagnostic, _
        | _, Error diagnostic -> Error [ diagnostic ]

    let receiptChange (receipt: Observations.Receipt) =
        match ReceiptRecord.path receipt, ReceiptRecord.encode receipt with
        | Ok target, Ok content -> Ok [ Change.Create(target, content) ]
        | Error diagnostic, _
        | _, Error diagnostic -> Error [ diagnostic ]

    let removal (principalId: string) =
        match stored.Members.TryFind principalId with
        | Some found -> Ok [ Change.Delete(found.Path, found.Revision) ]
        | None -> Error [ NotAMember(principalId, "") ]

    let results =
        [ Persistence.changes stored.Activities changed.Activities ]
        @ (changed.References |> List.map referenceChange)
        @ (changed.Attestations |> List.map attestationChange)
        @ (changed.Members |> List.map memberChange)
        @ (changed.Removed |> List.map removal)
        @ (changed.Audit |> List.map auditChange)
        @ (changed.Periods |> Option.toList |> List.map periodsChange)
        @ (changed.Reviews |> List.map reviewChange)
        @ (changed.Candidates |> List.map candidateChange)
        @ (changed.Receipts |> List.map receiptChange)
        @ (changed.Consumed |> List.map (fun (path, revision) -> Ok [ Change.Delete(path, revision) ]))

    match results |> List.collect (function Error problems -> problems | Ok _ -> []) with
    | [] -> Ok(results |> List.collect (function Ok found -> found | Error _ -> []))
    | problems -> Error problems

/// The records an operation's changes carry, as a command's change (for a
/// change queued while offline and read back, WI-0033): what it creates or
/// updates, decoded by record type, and the members it deletes as removals.
/// An activity deleted to move it to another month is carried by its create.
let changedOf (changes: Change list) : Result<Changed, Diagnostic list> =
    let decoded (path: RelativePath) (content: string) =
        let where = RelativePath.render path

        match Layout.keyOf path, Record.decode Record.DefaultMaxBytes content with
        | Some key, Ok record ->
            let body = record.Body
            let invalid detail = InvalidStoredRecord(where, detail)

            if key.Type = ActivityRecord.recordType then
                ActivityRecord.ofBody body |> Result.map (fun a -> { nothing with Activities = [ a ] }) |> Result.mapError invalid
            elif key.Type = ReferenceRecord.recordType then
                ReferenceRecord.ofBody body |> Result.map (fun item -> { nothing with References = [ item ] }) |> Result.mapError invalid
            elif key.Type = AttestationRecord.recordType then
                AttestationRecord.ofBody body |> Result.map (fun a -> { nothing with Attestations = [ a ] }) |> Result.mapError invalid
            elif key.Type = MemberRecord.recordType then
                MemberRecord.ofBody body |> Result.map (fun m -> { nothing with Members = [ m ] }) |> Result.mapError invalid
            elif key.Type = AuditRecord.recordType then
                AuditRecord.ofBody body |> Result.map (fun a -> { nothing with Audit = [ a ] }) |> Result.mapError invalid
            elif key.Type = PeriodConfigRecord.recordType then
                PeriodConfigRecord.ofBody "" body |> Result.map (fun p -> { nothing with Periods = Some p }) |> Result.mapError invalid
            elif key.Type = ReviewRecord.recordType then
                ReviewRecord.ofBody body |> Result.map (fun review -> { nothing with Reviews = [ review ] }) |> Result.mapError invalid
            elif key.Type = CandidateRecord.recordType then
                CandidateRecord.ofBody body |> Result.map (fun c -> { nothing with Candidates = [ c ] }) |> Result.mapError invalid
            elif key.Type = ReceiptRecord.recordType then
                ReceiptRecord.ofBody body |> Result.map (fun receipt -> { nothing with Receipts = [ receipt ] }) |> Result.mapError invalid
            else
                Error(invalid "not a record Chrona keeps here")
        | _ -> Error(InvalidStoredRecord(where, "not a valid record"))

    let removal (path: RelativePath) =
        match Layout.keyOf path with
        | Some key when key.Type = MemberRecord.recordType ->
            match ActivityRecord.actorOfSegment (RecordId.value key.Id) with
            | Some principalId -> Ok { nothing with Removed = [ principalId ] }
            | None -> Error(InvalidStoredRecord(RelativePath.render path, "not a member's record id"))
        | Some key when key.Type = ActivityRecord.recordType -> Ok nothing
        // A decided candidate leaves the open folder; it is carried by its create.
        | Some key when key.Type = CandidateRecord.recordType -> Ok nothing
        | _ when (RelativePath.segments path |> List.tryHead |> Option.map Segment.value) = Some "inbox" -> Ok nothing
        | _ -> Error(InvalidStoredRecord(RelativePath.render path, "not a record Chrona removes"))

    // Derived state written with the records (the activity index) is not a record.
    let derived (path: RelativePath) =
        RelativePath.segments path |> List.tryHead |> Option.map Segment.value = Some "derived"

    let parts =
        changes
        |> List.filter (function
            | Change.Create(path, _)
            | Change.Update(path, _, _)
            | Change.Delete(path, _) -> not (derived path))
        |> List.map (function
            | Change.Create(path, content)
            | Change.Update(path, content, _) -> decoded path content
            | Change.Delete(path, _) -> removal path)

    match parts |> List.choose (function Error d -> Some d | Ok _ -> None) with
    | [] ->
        let found = parts |> List.choose (function Ok c -> Some c | Error _ -> None)

        Ok
            { Activities = found |> List.collect _.Activities
              References = found |> List.collect _.References
              Attestations = found |> List.collect _.Attestations
              Members = found |> List.collect _.Members
              Removed = found |> List.collect _.Removed
              Audit = found |> List.collect _.Audit
              Periods = found |> List.tryPick _.Periods
              Reviews = found |> List.collect _.Reviews
              Candidates = found |> List.collect _.Candidates
              Receipts = found |> List.collect _.Receipts
              // Read back from a queued operation, an inbox file's removal is
              // its delete there; it is decided again from the inbox.
              Consumed = [] }
    | problems -> Error problems

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

/// The folders the application reads for an actor and a set of dates: the
/// members, the reference folders, the organization's configuration, and
/// the activity, attestation and audit month folders, with the review
/// folders of periods starting that month or the one before (a period
/// reaches into the next month).
let folders (actorId: string) (dates: System.DateOnly list) : Result<RelativePath list, Diagnostic> =
    let months = dates |> List.distinctBy (fun date -> date.Year, date.Month) |> List.sort

    let reviewMonths =
        months
        |> List.collect (fun date -> [ date.AddMonths -1; date ])
        |> List.map (fun date -> System.DateOnly(date.Year, date.Month, 1))
        |> List.distinct
        |> List.sort

    let all =
        [ MemberRecord.folder () ]
        @ ReferenceRecord.folders ()
        @ [ PeriodConfigRecord.folder (); CandidateRecord.openFolder () ]
        @ (months |> List.map (ActivityRecord.monthFolder actorId))
        @ (months |> List.map (AttestationRecord.monthFolder actorId))
        @ (months |> List.map (AuditRecord.monthFolder actorId))
        @ (reviewMonths |> List.map (ReviewRecord.monthFolder actorId))
        @ (months |> List.map CandidateRecord.decidedFolder)

    all
    |> List.fold (fun state next -> state |> Result.bind (fun found -> next |> Result.map (fun folder -> found @ [ folder ]))) (Ok [])
