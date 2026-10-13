/// Everything an organization's folder holds for the application, as far as
/// Chrona may trust it: activities (`Persistence`), reference items and
/// attestations, loaded together, changed together in one commit, and
/// tracked with the revisions last read.
///
/// Pure.
///
/// What one command changes, and the Arca changes that store it, is
/// `StoredChanges`; what is stored once changes are laid over it or have
/// landed is `StoredOverlay`.
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

let internal decodeWith (schema: SchemaSupport) (ofBody: Json -> Codec.Decoded<'a>) (key: RecordKey) (stored: StoredObject) =
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
