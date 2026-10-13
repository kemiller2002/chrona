/// Reading the organization's records from GitHub (38, 39, 41): the folders a
/// period needs, validated, with records edited outside Chrona held for
/// review; what the page shows of them; and what GitHub holds after a commit
/// this page made, kept in the read cache.
module internal Chrona.Application.StoreReads

open System
open System.Collections.Generic
open Arca
open Arca.GitHub
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Application.Bridge
open Chrona.Application.Store
open Chrona.Application.StoreState
open Chrona.Application.StoreUnsentQueue
open Chrona.Application.StoreIndex
open Chrona.Application.StorePage

/// Reads these folders: each folder's records. The folders are listed
/// together, then their records read together, at most `InTurn.Limit`
/// requests at a time, each folder's records in listing order (WI-0077).
/// The first failure, in folder and file order, is the answer.
let readFolders (bridge: Bridge) (provider: StorageProvider) (folder: Namespace) (paths: Result<RelativePath list, Diagnostic>) =
    async {
        match paths with
        | Error diagnostic -> return Error(Unopened.Refused(code diagnostic))
        | Ok folders ->
            let! listings = folders |> List.map (provider.List folder) |> InTurn.together bridge

            match listings |> List.tryPick (function Error error -> Some error | Ok _ -> None) with
            | Some error -> return Error(unopened error)
            | None ->
                let listed =
                    List.zip folders listings
                    |> List.choose (fun (path, listing) ->
                        listing |> Result.toOption |> Option.map (fun listing -> path, Persistence.recordFiles path listing))

                let! reads =
                    listed
                    |> List.collect (fun (_, (files, _)) -> files |> List.map (provider.Read folder))
                    |> InTurn.together bridge

                match reads |> List.tryPick (function Error error -> Some error | Ok _ -> None) with
                | Some error -> return Error(unopened error)
                | None ->
                    // Each folder's share of the reads, in order.
                    let counts = listed |> List.map (fun (_, (files, _)) -> List.length files)

                    let shares =
                        counts
                        |> List.mapFold (fun (rest: Result<ReadOutcome, StorageFailure> list) count -> List.splitAt count rest) reads
                        |> fst

                    let found =
                        shares
                        |> List.map (
                            List.choose (function
                                | Ok(ReadOutcome.Found found) -> Some found
                                // Chrona erases nothing; a record erased by
                                // another tool is gone, not a problem.
                                | _ -> None)
                        )

                    return
                        Ok(
                            List.zip (listed |> List.map fst) found,
                            listed |> List.collect (fun (_, (_, incomplete)) -> incomplete)
                        )
    }

/// Reads the folders these dates need for this person.
let read (bridge: Bridge) (provider: StorageProvider) (folder: Namespace) (actorId: string) (dates: DateOnly list) =
    readFolders bridge provider folder (Stored.folders actorId dates)

/// For someone who approves time where approval is required: the other
/// members' review steps of periods in these months, and the time their
/// submissions waiting for approval cover (WI-0036).
let readReviewing (bridge: Bridge) (provider: StorageProvider) (folder: Namespace) (actorId: string) (dates: DateOnly list) (stored: Stored.Stored) =
    async {
        let mayApprove =
            stored.Members.TryFind actorId
            |> Option.exists (fun found ->
                found.Membership.Capabilities.Contains Access.ApproveTime
                || found.Membership.Capabilities.Contains Access.RejectTime)

        let approvalRequired = stored.Periods |> Option.exists _.Periods.Config.ApprovalRequired
        let others = stored.Members |> Map.toList |> List.map fst |> List.filter ((<>) actorId)

        if not mayApprove || not approvalRequired || others.IsEmpty then
            return Ok([], [])
        else
            let months =
                dates
                |> List.collect (fun date -> [ date.AddMonths -1; date ])
                |> List.map (fun date -> DateOnly(date.Year, date.Month, 1))
                |> List.distinct

            let all (paths: Result<RelativePath, Diagnostic> list) =
                paths |> List.fold (fun state next -> state |> Result.bind (fun found -> next |> Result.map (fun one -> found @ [ one ]))) (Ok [])

            match! readFolders bridge provider folder (all [ for other in others do for month in months -> ReviewRecord.monthFolder other month ]) with
            | Error reason -> return Error reason
            | Ok(reviewFolders, problems) ->
                let reviews = Stored.load (reviewFolders |> List.collect snd) |> Stored.reviews

                let covered =
                    PeriodReview.awaitingApproval true reviews
                    |> List.filter (fun submission -> submission.ActorId <> actorId)
                    |> List.collect (fun submission ->
                        [ submission.Period.Start; submission.Period.Finish ]
                        |> List.map (fun date -> submission.ActorId, DateOnly(date.Year, date.Month, 1)))
                    |> List.distinct

                match! readFolders bridge provider folder (all [ for other, month in covered -> ActivityRecord.monthFolder other month ]) with
                | Error reason -> return Error reason
                | Ok(activityFolders, more) -> return Ok(reviewFolders @ activityFolders, problems @ more)
    }

/// The repository state, then what the folders hold at it: validated,
/// with records edited outside Chrona held for review. What was read is
/// kept in the read cache, with which records were held.
let load (page: Page)
    (connection: Connection)
    (folder: Namespace)
    (actorId: string)
    (months: Set<int * int>)
    (others: Set<string * (int * int)>)
    (known: Map<string, Revision * HistoryEntry list>)
    =
    async {
        match! connection.Provider.NamespaceState folder with
        | Error error -> return Error(unopened error)
        | Ok observed ->
            let token = InNamespace observed
            let dates = months |> Set.toList |> List.map firstOf
            // Every folder read at the state just observed: one snapshot,
            // and no branch resolution per read (Arca 0.5.0, arca#39).
            let provider = pinnedAt connection observed.RepositoryToken

            let! found =
                async {
                    match! read page.Bridge provider folder actorId dates with
                    | Error reason -> return Error reason
                    | Ok(own, incomplete) ->
                        match! readReviewing page.Bridge provider folder actorId dates (Stored.load (own |> List.collect snd)) with
                        | Error reason -> return Error reason
                        | Ok(reviewing, more) ->
                            // Other people's months a report asked for
                            // (WI-0039), each folder read once.
                            let already = own @ reviewing |> List.map (fst >> RelativePath.render) |> Set.ofList

                            let wanted =
                                others
                                |> Set.toList
                                |> List.filter (fun (other, _) -> other <> actorId)
                                |> List.choose (fun (other, month) -> ActivityRecord.monthFolder other (firstOf month) |> Result.toOption)
                                |> List.filter (fun path -> not (already.Contains(RelativePath.render path)))
                                |> List.distinctBy RelativePath.render

                            match! readFolders page.Bridge provider folder (Ok wanted) with
                            | Error reason -> return Error reason
                            | Ok(theirs, still) -> return Ok(own @ reviewing @ theirs, incomplete @ more @ still)
                }

            match found with
            | Error reason -> return Error reason
            | Ok(folders, incomplete) ->
                let objects = folders |> List.collect snd
                let stored = Stored.load objects

                // Each record's history, to hold what was edited outside
                // Chrona: read together, and only for a record whose
                // revision changed since this page last read it
                // (WI-0077). The same bytes at the same path were judged
                // already.
                let records =
                    stored.Activities.Activities
                    |> Map.toList
                    |> List.map (fun (_, found) -> RelativePath.render found.Path, found)

                let unknown =
                    records
                    |> List.filter (fun (path, found) ->
                        match known.TryFind path with
                        | Some(revision, _) -> revision <> found.Revision
                        | None -> true)

                let! asked =
                    unknown
                    |> List.map (fun (_, found) -> provider.History folder found.Path)
                    |> InTurn.together page.Bridge

                match asked |> List.tryPick (function Error error -> Some error | Ok _ -> None) with
                | Some error -> return Error(unopened error)
                | None ->
                    let histories =
                        List.zip unknown asked
                        |> List.fold
                            (fun (histories: Map<string, Revision * HistoryEntry list>) ((path, found), history) ->
                                match history with
                                | Ok history -> histories.Add(path, (found.Revision, history))
                                | Error _ -> histories)
                            (records
                             |> List.choose (fun (path, _) -> known.TryFind path |> Option.map (fun entry -> path, entry))
                             |> Map.ofList)

                    let activities = Persistence.holdExternalEdits (histories |> Map.map (fun _ -> snd)) stored.Activities

                    let held =
                        activities.HeldForReview |> Map.toList |> List.map (fun (_, found) -> RelativePath.render found.Path) |> Set.ofList

                    do!
                        keepRead page
                            actorId
                            folder
                            token
                            (folders
                             |> List.collect (fun (path, inFolder) ->
                                 [ RelativePath.render path, inFolder
                                   HeldPrefix + RelativePath.render path,
                                   inFolder |> List.filter (fun found -> held.Contains(RelativePath.render found.Path)) ]))

                    return
                        Ok(
                            token,
                            { stored with
                                Activities = { activities with Problems = incomplete @ activities.Problems } },
                            folders |> List.map (fun (path, inFolder) -> RelativePath.render path, inFolder) |> Map.ofList,
                            histories
                        )
    }

/// What is stored with this page's unsent changes laid over it.
let shown (state: Opened) =
    state.Queue.Entries
    |> List.filter unsent
    |> List.choose (recordsOf state.Folder >> Result.toOption)
    |> List.fold (fun stored changed -> StoredOverlay.overlay changed stored) state.Stored

let contents (state: Opened) : StoreContents =
    let state = { state with Stored = shown state }
    let actorId = state.Session.ActorId
    let all = state.Stored.Activities.Activities |> Map.toList |> List.map (fun (_, found) -> found.Activity)

    { Name = state.Name
      Cached =
        match state.Basis with
        | CachedAt at -> Some at
        | ReadAt _ -> None
      // The person's own time; others' is read only where it waits for
      // their approval (WI-0036), or a report across everyone asked for
      // it (WI-0039).
      Activities = all |> List.filter (fun a -> a.ActorId = actorId)
      Reviewing = all |> List.filter (fun a -> a.ActorId <> actorId)
      Periods = state.Stored.Periods |> Option.map _.Periods
      Reviews = Stored.reviews state.Stored
      Candidates = state.Stored.Candidates |> Map.toList |> List.map (fun (_, found) -> found.Candidate)
      References = state.Stored.References |> Map.toList |> List.map (fun (_, found) -> found.Item)
      Attestations = Stored.attestations state.Stored
      Members = state.Stored.Members |> Map.toList |> List.map (fun (_, found) -> found.Membership)
      Held = state.Stored.Activities.HeldForReview |> Map.toList |> List.map (fun (_, found) -> found.Activity)
      Audit = Stored.audit state.Stored
      Problems = Stored.problems state.Stored
      Months = state.Months |> Set.toList
      Others = state.Others |> Set.toList
      History = history state.Index
      Index = indexText state.Index
      IndexCurrent = state.Index.Note.IsNone && state.Index.Index.IsSome }

/// Keeps the activity index as read in the read cache, with the change
/// token the folders were read at.
let keepIndex (page: Page) (state: Opened) (token: Condition) =
    async {
        match state.Index.Index, state.Index.Revision, Derived.path ActivityIndex.definition with
        | Some index, Some revision, Ok path ->
            do!
                keepRead page
                    state.Session.ActorId
                    state.Folder
                    token
                    [ IndexPartition, [ ({ Path = path; Content = Derived.encode index; Revision = revision }: StoredObject) ] ]
        | _ -> ()
    }

/// Reads the state again from GitHub: the same months, plus any these
/// dates add. From the read cache, this is what makes the records
/// current again.
let refreshWith (page: Page) (state: Opened) (dates: DateOnly list) (others: Set<string * (int * int)>) =
    async {
        let months = state.Months + (dates |> List.map monthOf |> Set.ofList)

        match! load page { Provider = state.Provider; PinAt = state.PinAt } state.Folder state.Session.ActorId months others state.Histories with
        | Error reason -> return Error(reasonOf reason)
        | Ok(token, stored, read, histories) ->
            let! indexed =
                readIndex (pinnedFor { Provider = state.Provider; PinAt = state.PinAt } token) state.Folder state.Session.ActorId months others stored state.Index

            let next =
                { state with
                    Basis = ReadAt token
                    Stored = stored
                    Read = read
                    Months = months
                    Others = others
                    Histories = histories
                    Index = indexed }

            do! keepIndex page next token
            page.Opened <- Some next
            return Ok next
    }

let refresh (page: Page) (state: Opened) (dates: DateOnly list) = refreshWith page state dates state.Others

/// What the next write is conditioned on after a commit this page made:
/// the folder's state at exactly that commit when GitHub's head is still
/// the commit; otherwise someone committed since, and the next write is
/// held to the repository at the commit, as before Arca 0.4.0, so it is
/// never conditioned on a folder state this page did not read.
let conditionAfter (state: Opened) (receipt: CommitReceipt) =
    async {
        match! state.Provider.NamespaceState state.Folder with
        | Ok observed when observed.RepositoryToken = receipt.ChangeToken -> return InNamespace observed
        | _ -> return InRepository receipt.ChangeToken
    }

/// What GitHub holds after a commit this page made: the folders it
/// touched, as read plus the commit's changes at the receipt's revisions,
/// kept in the read cache under the condition the commit leaves
/// (LCP-083). `state` is the state after the commit.
let afterCommit (page: Page) (state: Opened) (operation: Operation) (receipt: CommitReceipt) =
    async {
        let revisionOf path =
            receipt.Revisions.TryFind(RelativePath.render path) |> Option.flatten

        let indexPath = Derived.path ActivityIndex.definition |> Result.toOption
        let mutable read = state.Read
        let mutable touched = Set.empty
        let mutable index = None

        for change in operation.Changes do
            let path = Change.path change
            let rendered = RelativePath.render path

            let stored =
                match Change.content change, revisionOf path with
                | Some content, Some revision -> Some({ Path = path; Content = content; Revision = revision }: StoredObject)
                | _ -> None

            if Some path = indexPath then
                index <- stored
            else
                match read |> Map.tryFindKey (fun folder _ -> rendered.StartsWith(folder + "/", StringComparison.Ordinal)) with
                | Some folder ->
                    let others = read[folder] |> List.filter (fun found -> found.Path <> path)
                    read <- read.Add(folder, others @ Option.toList stored)
                    touched <- touched.Add folder
                | None -> ()

        let held =
            state.Stored.Activities.HeldForReview |> Map.toList |> List.map (fun (_, found) -> RelativePath.render found.Path) |> Set.ofList

        do!
            keepRead page
                state.Session.ActorId
                state.Folder
                (match state.Basis with
                 | ReadAt condition -> condition
                 | CachedAt _ -> InRepository receipt.ChangeToken)
                ((touched
                  |> Set.toList
                  |> List.collect (fun folder ->
                      [ folder, read[folder]
                        HeldPrefix + folder, read[folder] |> List.filter (fun found -> held.Contains(RelativePath.render found.Path)) ]))
                 @ (index |> Option.map (fun found -> IndexPartition, [ found ]) |> Option.toList))

        return { state with Read = read }
    }

/// Others' independent changes are shown once nothing of this page's own
/// is waiting to be decided; unsent changes are shown over them.
let refreshed (page: Page) (state: Opened) =
    if page.Jobs.Count = 0 then [ Update.StoreOpened(contents state) ] else []
