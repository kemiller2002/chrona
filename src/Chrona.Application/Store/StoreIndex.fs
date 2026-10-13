/// The activity index as the store keeps it (WI-0034): kept current in each
/// commit while it fits, checked against the records read, and rebuilt on
/// request.
module internal Chrona.Application.StoreIndex

open System
open System.Collections.Generic
open Arca
open Arca.GitHub
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.Model
open Chrona.Application.Bridge
open Chrona.Application.Store
open Chrona.Application.StoreState

/// The most text the activity index may take; past it, the index is no
/// longer kept with each change, and is rebuilt on request.
[<Literal>]
let IndexBudget = 512000

/// The change that keeps the activity index current with `changes`, when it
/// is kept and they change it.
let indexChange (indexed: Indexed) (changes: Change list) =
    match indexed.Index with
    | None -> []
    | Some index ->
        let next = ActivityIndex.apply changes index

        if next.Entries = index.Entries && indexed.Revision.IsSome then
            []
        else
            let text = Derived.encode next

            if text.Length > IndexBudget then
                []
            else
                match indexed.Revision with
                | Some revision -> [ Change.Update(ActivityIndex.path, text, revision) ]
                | None -> [ Change.Create(ActivityIndex.path, text) ]

/// The index after an operation landed: what it wrote, or, when it changed
/// activities without writing the index, no longer kept.
let indexAfter (indexed: Indexed) (operation: Operation) (receipt: CommitReceipt) =
    let written =
        operation.Changes
        |> List.tryPick (function
            | Change.Create(path, content)
            | Change.Update(path, content, _) when path = ActivityIndex.path -> Some content
            | _ -> None)

    let touchesActivities =
        operation.Changes
        |> List.exists (function
            | Change.Create(path, _)
            | Change.Update(path, _, _)
            | Change.Delete(path, _) -> Layout.keyOf path |> Option.exists (fun key -> key.Type = ActivityRecord.recordType))

    match written |> Option.map Derived.decode, receipt.Revisions.TryFind(RelativePath.render ActivityIndex.path) with
    | Some(Ok index), Some(Some revision) ->
        { indexed with
            Index = Some index
            Revision = Some revision }
    | _ when touchesActivities && indexed.Index.IsSome ->
        { Index = None
          Revision = None
          Note = Some "The activity index grew too large to keep with every change. Rebuild it to bring it up to date." }
    | _ -> indexed

/// Each person's months that hold time, from the index.
let history (indexed: Indexed) =
    indexed.Index |> Option.map ActivityIndex.totals |> Option.defaultValue []

/// What the index covers, or why it is not kept, for the person.
let indexText (indexed: Indexed) =
    match indexed with
    | { Note = Some note } -> note
    | { Index = Some index } ->
        let months = ActivityIndex.totals index |> List.length
        let monthsText = if months = 1 then "1 month" else $"{months} months"
        let recordsText = if index.Source.Count = 1 then "1 record" else $"{index.Source.Count} records"
        $"Kept with every change: {recordsText} in {monthsText} of time."
    | _ -> ""

/// The stored activity index, checked against the month folders read:
/// where it disagrees with them (a record changed outside Chrona, for
/// example), it is said to be out of date there until it is rebuilt.
/// Other people's months read for a report are checked too (WI-0039).
let readIndex (provider: StorageProvider) (folder: Namespace) (actorId: string) (months: Set<int * int>) (others: Set<string * (int * int)>) (stored: Stored.Stored) (previous: Indexed) =
    async {
        match! Derived.read provider folder ActivityIndex.definition with
        | Ok(Some(index, revision)) when index.Version = ActivityIndex.definition.Version && index.Name = ActivityIndex.empty.Name ->
            let snapshot = stored.Activities

            let read =
                (snapshot.Activities |> Map.toList |> List.map snd)
                @ (snapshot.HeldForReview |> Map.toList |> List.map snd)
                |> List.map (fun found -> RelativePath.render found.Path, found.ContentHash)

            let differing =
                (months |> Set.toList |> List.map (fun month -> actorId, month))
                @ (others |> Set.toList |> List.filter (fun (other, _) -> other <> actorId))
                |> List.choose (fun (person, month) -> ActivityRecord.monthFolder person (firstOf month) |> Result.toOption)
                |> List.collect (fun monthFolder ->
                    let prefix = RelativePath.render monthFolder
                    let inFolder = read |> List.filter (fun (path, _) -> path.StartsWith(prefix + "/", StringComparison.Ordinal))
                    ActivityIndex.disagreements index prefix inFolder)

            return
                { Index = Some index
                  Revision = Some revision
                  Note =
                    match differing with
                    | [] -> None
                    | paths ->
                        let places = if paths.Length = 1 then "1 place" else $"{paths.Length} places"
                        Some $"The activity index differs from the records in {places} (changed outside Chrona?). Rebuild it from the records." }
        | Ok(Some _) ->
            return
                { Index = None
                  Revision = None
                  Note = Some "The activity index was built by another version of Chrona. Rebuild it from the records." }
        // A new organization's index starts empty, and is written with its first activity.
        | Ok None when previous.Index.IsSome && previous.Revision.IsNone -> return previous
        | Ok None ->
            return
                { Index = None
                  Revision = None
                  Note = Some "The activity index has not been built yet. An administrator can build it from the records under More." }
        | Error _ ->
            return
                { Index = None
                  Revision = None
                  Note = Some "The activity index cannot be read. An administrator can rebuild it from the records under More." }
    }

let describeDerived =
    function
    | DerivedError.Corrupt _ -> "The stored activity index could not be read."
    | DerivedError.Snapshot _ -> "The records kept changing while they were read. Try again."
    | DerivedError.InvalidSource(path, _) -> $"{path} is not a valid record, so the index cannot be built until it is repaired."
    | DerivedError.Provider failure -> describeFailure failure
    | DerivedError.InvalidDefinition reason -> $"The activity index is not defined correctly ({reason})."
