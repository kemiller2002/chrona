/// Revision-safe lifecycle transitions over an actor's activities
/// (requirements expansion 12, 13, 21, 24, 25).
///
/// Every command names the revision it was decided against. A command
/// against any other revision is a `RevisionConflict`: two divergent edits
/// of the same activity never resolve by last-write-wins. Every accepted
/// command appends one audit entry (actor, time, source, command, prior and
/// resulting revisions, reason, correlation id); the audit trail is
/// append-only. Records are never deleted: void keeps the record, and split
/// and merge supersede their sources.
module Chrona.Domain.Ledger

open System
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity

type AuditEntry =
    { Performer: string
      At: DateTimeOffset
      Source: string
      Command: string
      ActivityIds: string list
      /// Revisions before and after, per affected activity, in id order.
      PriorRevisions: (string * int) list
      ResultingRevisions: (string * int) list
      Reason: string option
      CorrelationId: string option }

[<NoComparison>]
type Ledger =
    { Activities: Map<string, Activity>
      Audit: AuditEntry list }

let empty =
    { Activities = Map.empty
      Audit = [] }

/// Who, when and through what a command arrives; the zone is the actor's
/// business zone for overlap and day-boundary checks.
[<NoComparison>]
type CommandContext =
    { Performer: string
      At: DateTimeOffset
      Source: string
      Zone: Zone
      /// The organization's reference data: new assignments name active items.
      References: Reference.Catalogue
      CorrelationId: string option }

/// What an amendment may change. A timing change carries the re-derived
/// occurrence, timing and minutes (from `ManualEntry` rules).
type Amendment =
    { Classification: Classification option
      Billability: Billability option
      BillingReference: BillingReference option
      Retime: (Occurrence * Timing * int) option
      Reason: string }

/// One child of a split: its new id, its minutes, an optional
/// reclassification, and which of the source's evidence ids it carries.
type SplitPart =
    { ActivityId: string
      Minutes: int
      Classification: Classification option
      EvidenceIds: string list }

type Command =
    | Record of Activity
    | Amend of activityId: string * expectedRevision: int * Amendment
    | Void of activityId: string * expectedRevision: int * reason: string
    | Restore of activityId: string * expectedRevision: int
    | Split of activityId: string * expectedRevision: int * parts: SplitPart list
    | Merge of sources: (string * int) list * newId: string * classification: Classification option
    | LinkEvidence of activityId: string * expectedRevision: int * Evidence
    | UnlinkEvidence of activityId: string * expectedRevision: int * evidenceId: string

let private commandName =
    function
    | Record _ -> "create"
    | Amend _ -> "amend"
    | Void _ -> "void"
    | Restore _ -> "restore"
    | Split _ -> "split"
    | Merge _ -> "merge"
    | LinkEvidence _ -> "evidence-link"
    | UnlinkEvidence _ -> "evidence-unlink"

let private find (ledger: Ledger) (id: string) expected =
    match ledger.Activities.TryFind id with
    | None -> Error [ UnknownActivity id ]
    | Some activity when activity.Revision <> expected -> Error [ RevisionConflict(expected, activity.Revision) ]
    | Some activity -> Ok activity

let private requireRecorded (command: string) (activity: Activity) =
    match activity.Record with
    | Recorded -> Ok activity
    | other -> Error [ IllegalTransition(recordStateName other, command) ]

/// Publication consequences of changing an activity (17): unpublished time
/// just changes; time already published, or already invoiced by Summa, may
/// still be corrected here, but becomes AdjustmentRequired, an explicit
/// downstream correction obligation, never a silent rewrite.
let private afterChange (activity: Activity) : Result<PublicationState, Diagnostic list> =
    match activity.Publication with
    | Published
    | InvoicedExternally -> Ok AdjustmentRequired
    | other -> Ok other

/// Changing reviewed time never leaves the review silently valid (14).
let private reopenIfReviewed =
    function
    | Submitted
    | Approved -> Reopened
    | other -> other

let private bump (context: CommandContext) (activity: Activity) =
    { activity with
        Revision = activity.Revision + 1
        LastChangedAt = context.At }

/// The creation rules a record must satisfy whenever it is recorded, amended
/// or restored: a complete classification and no overlap (R2: amended
/// fields are revalidated by the same rules as creation).
///
/// `previous` is what the record already carried: references it keeps stay
/// valid after they are archived; newly assigned ones must be active.
let private valid (context: CommandContext) (ledger: Ledger) (previous: Classification list) (candidate: Activity) =
    let others = ledger.Activities |> Map.toList |> List.map snd

    match
        classificationProblems candidate.Classification
        @ Reference.assignmentProblems context.References previous candidate.Classification
        @ Overlap.check context.Zone others candidate
    with
    | [] -> Ok candidate
    | problems -> Error problems

/// Whether every activity has clock times and, in start order, each ends
/// exactly where the next begins.
let private contiguous (items: Activity list) =
    let spans = items |> List.choose interval |> List.sortBy fst
    spans.Length = items.Length && spans |> List.pairwise |> List.forall (fun ((_, e1), (s2, _)) -> e1 = s2)

/// Evidence follows the record state (R4): a Recorded activity may link and
/// unlink evidence; a Voided one may only gain it (it can still be
/// restored); a Superseded one is final.
let private evidenceAllowed (command: string) (activity: Activity) =
    match activity.Record, command with
    | Recorded, _
    | Voided _, "evidence-link" -> Ok activity
    | other, _ -> Error [ IllegalTransition(recordStateName other, command) ]

let private apply (context: CommandContext) (ledger: Ledger) (command: Command) : Result<Activity list, Diagnostic list> =
    let ( >>= ) r f = Result.bind f r

    match command with
    | Record activity when ledger.Activities.ContainsKey activity.ActivityId ->
        Error [ IllegalTransition("Recorded", "create") ]
    | Record activity -> valid context ledger [] activity >>= fun a -> Ok [ a ]

    | Amend(id, expected, amendment) ->
        find ledger id expected
        >>= requireRecorded "amend"
        >>= fun activity ->
            afterChange activity
            >>= fun publication ->
                let changed =
                    { bump context activity with
                        Review = reopenIfReviewed activity.Review
                        Classification = defaultArg amendment.Classification activity.Classification
                        Billability = defaultArg amendment.Billability activity.Billability
                        BillingReference = defaultArg amendment.BillingReference activity.BillingReference
                        Publication =
                            match amendment.Billability, publication with
                            | Some NonBillable, (Unpublished | ReadyForPublication) -> NotBillable
                            | Some(Billable | PendingClassification), NotBillable -> Unpublished
                            | _ -> publication }

                let changed =
                    match amendment.Retime with
                    | None -> changed
                    | Some(occurrence, timing, minutes) -> { changed with Occurrence = occurrence; Timing = timing; Minutes = minutes }

                valid context ledger [ activity.Classification ] changed >>= fun a -> Ok [ a ]

    | Void(id, expected, reason) ->
        find ledger id expected
        >>= requireRecorded "void"
        >>= fun activity ->
            afterChange activity
            >>= fun publication ->
                Ok [ { bump context activity with Record = Voided reason; Publication = publication; Review = reopenIfReviewed activity.Review } ]

    | Restore(id, expected) ->
        find ledger id expected
        >>= fun activity ->
            match activity.Record with
            | Voided _ ->
                // Restoring rechecks overlap against the ledger as it is now.
                valid context ledger [ activity.Classification ] { bump context activity with Record = Recorded } >>= fun a -> Ok [ a ]
            | other -> Error [ IllegalTransition(recordStateName other, "restore") ]

    | Split(id, expected, parts) ->
        find ledger id expected
        >>= requireRecorded "split"
        >>= fun source ->
            let total = parts |> List.sumBy _.Minutes
            let evidenceIds = source.Evidence |> List.map _.Id |> Set.ofList
            let assigned = parts |> List.collect _.EvidenceIds

            let problems =
                [ if parts.Length < 2 then IllegalTransition("Recorded", "split into fewer than two parts")
                  if total <> source.Minutes then SplitDurationMismatch(source.Minutes, total)
                  if parts |> List.exists (fun p -> p.Minutes <= 0) then DurationNotPositive
                  // Evidence is assigned, never silently duplicated (13).
                  if List.distinct assigned <> assigned || not (Set.isSubset (Set.ofList assigned) evidenceIds) then
                      EvidenceAssignmentInvalid
                  // A reclassified child obeys the creation rules too.
                  yield!
                      parts
                      |> List.collect (fun p ->
                          match p.Classification with
                          | Some c -> classificationProblems c @ Reference.assignmentProblems context.References [ source.Classification ] c
                          | None -> [])
                      |> List.distinct ]

            if not problems.IsEmpty then
                Error problems
            else
                let starts =
                    parts |> List.scan (fun offset part -> offset + part.Minutes) 0 |> List.take parts.Length

                let children =
                    List.zip parts starts
                    |> List.map (fun (part, offset) ->
                        let timing, occurrence =
                            match source.Timing with
                            | Interval(start, _) ->
                                let s = start.AddMinutes(float offset)
                                Interval(s, s.AddMinutes(float part.Minutes)), occurrence context.Zone s
                            | DurationOnDate _ -> DurationOnDate part.Minutes, source.Occurrence

                        { source with
                            ActivityId = part.ActivityId
                            Occurrence = { occurrence with LocalDate = source.Occurrence.LocalDate }
                            Timing = timing
                            Minutes = part.Minutes
                            Classification = defaultArg part.Classification source.Classification
                            Evidence = source.Evidence |> List.filter (fun e -> List.contains e.Id part.EvidenceIds)
                            Lineage = [ source.ActivityId ]
                            Revision = 1
                            CreatedAt = context.At
                            LastChangedAt = context.At
                            Review = Unsubmitted })

                // Children are new, unpublished time; a published source
                // carries the downstream correction obligation.
                let publication = afterChange source |> Result.defaultValue source.Publication

                let children =
                    children
                    |> List.map (fun c ->
                        { c with
                            Publication = if c.Billability = NonBillable then NotBillable else Unpublished })

                Ok(
                    { bump context source with
                        Record = Superseded(parts |> List.map _.ActivityId)
                        Publication = publication }
                    :: children
                )

    | Merge(sources, newId, classification) ->
        let found = sources |> List.map (fun (id, rev) -> find ledger id rev >>= requireRecorded "merge")

        match found |> List.collect (function Error e -> e | Ok _ -> []) with
        | (_ :: _) as problems -> Error problems
        | [] ->
            let items = found |> List.choose (function Ok a -> Some a | Error _ -> None)
            let first = List.head items
            let distinctBy f = items |> List.map f |> List.distinct |> List.length

            let problems =
                [ if items.Length < 2 then IncompatibleMergeSources "a merge needs at least two sources"
                  if ledger.Activities.ContainsKey newId then IllegalTransition("Recorded", "merge into an existing id")
                  if distinctBy _.OrganizationId > 1 then OrganizationMismatch
                  if distinctBy _.ActorId > 1 then ActorMismatch
                  if distinctBy (fun a -> a.Occurrence.LocalDate) > 1 then IncompatibleMergeSources "sources are on different business dates"
                  // Incompatible downstream states need an explicit decision (13).
                  if distinctBy _.Publication > 1 then PublicationStateConflict "sources have different publication states"
                  if items |> List.exists (fun a -> a.Publication = Published || a.Publication = InvoicedExternally) then
                      PublicationStateConflict "published sources cannot be merged without reconciliation"
                  // Merge joins time that is already adjacent (R3): every
                  // source has clock times and each ends where the next
                  // begins, so the result spans exactly their time.
                  if not (contiguous items) then IncompatibleMergeSources "sources are not contiguous" ]

            if not problems.IsEmpty then
                Error problems
            else
                let ordered = items |> List.sortBy (fun a -> interval a |> Option.map fst)
                let minutes = items |> List.sumBy _.Minutes

                let timing =
                    let spans = ordered |> List.choose interval
                    Interval(fst spans.Head, snd (List.last spans))

                let merged =
                    { first with
                        ActivityId = newId
                        Occurrence = (List.head ordered).Occurrence
                        Timing = timing
                        Minutes = minutes
                        Classification = defaultArg classification first.Classification
                        Evidence = ordered |> List.collect _.Evidence |> List.distinctBy _.Id
                        Lineage = items |> List.map _.ActivityId |> List.sort
                        Revision = 1
                        CreatedAt = context.At
                        LastChangedAt = context.At
                        Review = Unsubmitted }

                match
                    classificationProblems merged.Classification
                    @ Reference.assignmentProblems context.References (items |> List.map _.Classification) merged.Classification
                with
                | [] -> Ok(merged :: (items |> List.map (fun a -> { bump context a with Record = Superseded [ newId ] })))
                | problems -> Error problems

    | LinkEvidence(id, expected, evidence) ->
        find ledger id expected
        >>= evidenceAllowed "evidence-link"
        >>= fun activity ->
            if activity.Evidence |> List.exists (fun e -> e.Id = evidence.Id) then
                Error [ IllegalTransition("linked", "evidence-link") ]
            elif not (evidenceProblems evidence).IsEmpty then
                Error(evidenceProblems evidence)
            else
                Ok [ { bump context activity with Evidence = activity.Evidence @ [ evidence ] } ]

    | UnlinkEvidence(id, expected, evidenceId) ->
        find ledger id expected
        >>= evidenceAllowed "evidence-unlink"
        >>= fun activity ->
            if activity.Evidence |> List.exists (fun e -> e.Id = evidenceId) then
                Ok [ { bump context activity with Evidence = activity.Evidence |> List.filter (fun e -> e.Id <> evidenceId) } ]
            else
                Error [ IllegalTransition("unlinked", "evidence-unlink") ]

let private reasonOf =
    function
    | Amend(_, _, amendment) -> Some amendment.Reason
    | Void(_, _, reason) -> Some reason
    | _ -> None

/// Executes one command: the next ledger, or every reason it was refused.
/// A refused command changes nothing and is not audited as a transition.
let execute (context: CommandContext) (ledger: Ledger) (command: Command) : Result<Ledger, Diagnostic list> =
    apply context ledger command
    |> Result.map (fun changed ->
        let ids = changed |> List.map _.ActivityId |> List.sort

        let prior =
            ids |> List.choose (fun id -> ledger.Activities.TryFind id |> Option.map (fun a -> id, a.Revision))

        let resulting = changed |> List.map (fun a -> a.ActivityId, a.Revision) |> List.sortBy fst

        { Activities = changed |> List.fold (fun map a -> Map.add a.ActivityId a map) ledger.Activities
          Audit =
            ledger.Audit
            @ [ { Performer = context.Performer
                  At = context.At
                  Source = context.Source
                  Command = commandName command
                  ActivityIds = ids
                  PriorRevisions = prior
                  ResultingRevisions = resulting
                  Reason = reasonOf command
                  CorrelationId = context.CorrelationId } ] })

/// The actor's time that currently counts (12): Recorded records only.
let activeMinutes (ledger: Ledger) =
    ledger.Activities |> Map.toList |> List.map snd |> List.filter consumesTime |> List.sumBy _.Minutes
