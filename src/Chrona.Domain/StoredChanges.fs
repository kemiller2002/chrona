/// The records one command changes (`Changed`), and the Arca changes that
/// store them on top of what was read, or read them back from a commit.
/// Pure.
module Chrona.Domain.StoredChanges

open Arca
open Chrona.Domain.Diagnostics
open Chrona.Domain.Stored

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
                ActivityRecordReading.ofBody body |> Result.map (fun a -> { nothing with Activities = [ a ] }) |> Result.mapError invalid
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
