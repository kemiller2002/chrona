/// The activity index: derived state, never authoritative (requirements
/// expansion 22, 38, 40; WI-0034).
///
/// One Arca derived index (`derived/indexes/activity-months.json`) with an
/// entry per stored activity record: its path, its content hash, and what
/// the month totals need (actor, month, minutes, whether it counts, review
/// and publication state). It lets the application know every month that
/// holds time, and its totals, from one read instead of reading every
/// month folder (38: cached derived indexes; 22: bounded reads).
///
/// It is rebuildable from the authoritative records at any time with
/// Arca's `Derived.rebuild` (40), and Chrona keeps it current in the same
/// commit as the records it changes: because every entry carries its
/// record's path and content hash, the index's source set (Arca's
/// `Derived.sourceSet`) follows from the entries alone, so an index kept up
/// to date commit by commit is exactly the index a rebuild makes, and
/// `Derived.check` and `Derived.compare` hold for it.
///
/// Pure.
module Chrona.Domain.ActivityIndex

open System
open Arca
open Chrona.Domain.Activity

/// One stored activity record, as the index keeps it.
type Entry =
    { Path: string
      /// The record's `sha256:` content hash.
      Hash: string
      ActivityId: string
      ActorId: string
      Year: int
      Month: int
      Minutes: int
      /// Whether its time counts (voided and superseded records do not).
      Counted: bool
      Review: string
      Publication: string }

let private name =
    match Segment.create "activity-months" with
    | Ok segment -> segment
    | Error error -> invalidOp (LocationError.describe error)

let private reviewName =
    function
    | Unsubmitted -> "unsubmitted"
    | Submitted -> "submitted"
    | Approved -> "approved"
    | Rejected _ -> "rejected"
    | Reopened -> "reopened"

let private publicationName =
    function
    | NotBillable -> "not-billable"
    | Unpublished -> "unpublished"
    | ReadyForPublication -> "ready-for-publication"
    | Published -> "published"
    | InvoicedExternally -> "invoiced-externally"
    | AdjustmentRequired -> "adjustment-required"

let private valueOf (activity: Activity) (hash: string) =
    Json.objectOf
        [ "hash", Json.String hash
          "id", Json.String activity.ActivityId
          "actor", Json.String activity.ActorId
          "month", Json.String(activity.Occurrence.LocalDate.ToString("yyyy-MM", Globalization.CultureInfo.InvariantCulture))
          "minutes", Json.Number(decimal activity.Minutes)
          "counted", Json.Bool(consumesTime activity)
          "review", Json.String(reviewName activity.Review)
          "publication", Json.String(publicationName activity.Publication) ]

/// The entry a stored activity record projects to, keyed by its path.
let private project (record: Record) =
    match ActivityRecord.ofBody record.Body with
    | Ok activity ->
        match ActivityRecord.path activity with
        | Ok path -> [ RelativePath.render path, valueOf activity (Record.contentHash record) ]
        | Error _ -> []
    | Error _ -> []

/// The index's definition. Its version changes whenever `project` does, so
/// a stored index of another version is rebuilt.
let definition: IndexDefinition =
    { Name = name
      Version = 1
      Sources = [ ActivityRecord.schema ]
      Project = project }

/// Where the index is stored, relative to the organization's folder.
let path =
    match Derived.path definition with
    | Ok path -> path
    | Error error -> invalidOp (LocationError.describe error)

/// An entry as data, or None when it is not one this index writes.
let entryOf (key: string, value: Json) : Entry option =
    let text name =
        match Json.field name value with
        | Some(Json.String found) -> Some found
        | _ -> None

    match text "hash", text "id", text "actor", text "month", Json.field "minutes" value, Json.field "counted" value, text "review", text "publication" with
    | Some hash, Some id, Some actor, Some month, Some(Json.Number minutes), Some(Json.Bool counted), Some review, Some publication ->
        match DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.None) with
        | true, first ->
            Some
                { Path = key
                  Hash = hash
                  ActivityId = id
                  ActorId = actor
                  Year = first.Year
                  Month = first.Month
                  Minutes = int minutes
                  Counted = counted
                  Review = review
                  Publication = publication }
        | _ -> None
    | _ -> None

/// The source set of the records the entries were made from: Arca's
/// `Derived.sourceSet`, computed from the entries' paths and hashes.
let sourceOf (entries: (string * Json) list) : SourceSet =
    let ordered =
        entries
        |> List.choose (fun (key, value) ->
            match Json.field "hash" value with
            | Some(Json.String hash) -> Some(key, hash)
            | _ -> None)
        |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

    { Count = ordered.Length
      Hash = Json.contentHash (Json.Array(ordered |> List.map (fun (path, hash) -> Json.Array [ Json.String path; Json.String hash ]))) }

let private ordered (entries: (string * Json) list) =
    entries
    |> List.sortWith (fun (k1, v1) (k2, v2) ->
        match String.CompareOrdinal(k1, k2) with
        | 0 -> String.CompareOrdinal(Json.canonicalText v1, Json.canonicalText v2)
        | order -> order)

/// The index of no records: a new organization's.
let empty: DerivedIndex =
    { Name = Segment.value name
      Version = definition.Version
      Source = sourceOf []
      Entries = [] }

/// The index after these changes landed: entries for the activity records
/// they create or update replaced, entries for those they delete removed,
/// and the source set recomputed. Changes to other records are not read.
let apply (changes: Change list) (index: DerivedIndex) : DerivedIndex =
    let isActivity (path: RelativePath) =
        Layout.keyOf path |> Option.exists (fun key -> key.Type = ActivityRecord.recordType)

    let entries =
        changes
        |> List.fold
            (fun (entries: Map<string, Json>) change ->
                match change with
                | Change.Create(target, content)
                | Change.Update(target, content, _) when isActivity target ->
                    let key = RelativePath.render target

                    match Record.decode Record.DefaultMaxBytes content |> Result.toOption |> Option.map project with
                    | Some [ (projected, value) ] when projected = key -> Map.add key value entries
                    // Not an activity this index can read: no entry, as a rebuild would refuse it.
                    | _ -> Map.remove key entries
                | Change.Delete(target, _) when isActivity target -> Map.remove (RelativePath.render target) entries
                | _ -> entries)
            (index.Entries |> Map.ofList)
        |> Map.toList
        |> ordered

    { index with
        Version = definition.Version
        Entries = entries
        Source = sourceOf entries }

/// The index's entries as data.
let entries (index: DerivedIndex) = index.Entries |> List.choose entryOf

/// One person's time in one month, from the index.
type MonthTotal =
    { ActorId: string
      Year: int
      Month: int
      Activities: int
      /// Minutes that count.
      Minutes: int
      ApprovedMinutes: int
      PublishedMinutes: int }

/// Each person's months that hold time, newest first.
let totals (index: DerivedIndex) : MonthTotal list =
    entries index
    |> List.groupBy (fun entry -> entry.ActorId, entry.Year, entry.Month)
    |> List.map (fun ((actor, year, month), found) ->
        let counted = found |> List.filter _.Counted

        { ActorId = actor
          Year = year
          Month = month
          Activities = counted.Length
          Minutes = counted |> List.sumBy _.Minutes
          ApprovedMinutes = counted |> List.filter (fun e -> e.Review = "approved") |> List.sumBy _.Minutes
          PublishedMinutes =
            counted
            |> List.filter (fun e -> e.Publication = "published" || e.Publication = "invoiced-externally")
            |> List.sumBy _.Minutes })
    |> List.sortBy (fun total -> total.ActorId, -total.Year, -total.Month)

/// The paths in a month folder where the index and the records read
/// disagree (a record changed outside Chrona, or one the index misses):
/// the index is then out of date there, until it is rebuilt.
let disagreements (index: DerivedIndex) (folder: string) (read: (string * string) list) =
    let prefix = folder.TrimEnd('/') + "/"

    let indexed =
        index.Entries
        |> List.choose (fun (key, value) ->
            match Json.field "hash" value with
            | Some(Json.String hash) when key.StartsWith(prefix, StringComparison.Ordinal) -> Some(key, hash)
            | _ -> None)
        |> Set.ofList

    Set.difference indexed (Set.ofList read) + Set.difference (Set.ofList read) indexed
    |> Set.toList
    |> List.map fst
    |> List.distinct
