/// The producers' inboxes (WI-0038): what one inbox file becomes, decided at
/// once when its source is trusted (expansion 19, WI-0074), and the receipts
/// whose candidate no folder holds (expansion 39).
module internal Chrona.Application.InboxReading

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
open Chrona.Application.StoreUnsentQueue
open Chrona.Application.StoreIndex
open Chrona.Application.StorePage
open Chrona.Application.StoreReads

/// What became of one inbox file (WI-0038).
type Taken =
    /// Received: its candidate and receipt are stored, the file removed.
    | TakenIn
    /// Not an observation: its receipt says why, and the file stays.
    | NotObservation of reasons: string list

/// A folder's files and subfolders; a folder that does not exist has none.
let entriesOf (reading: StorageProvider) (state: Opened) (path: RelativePath) =
    async {
        match! reading.List state.Folder path with
        | Error failure -> return Error failure
        | Ok listing ->
            let files = listing.Entries |> List.filter (fun entry -> not entry.IsFolder) |> List.map _.Path
            let folders = listing.Entries |> List.filter _.IsFolder |> List.map _.Path
            return Ok(files, folders)
    }

let lastSegment (path: RelativePath) =
    let rendered = RelativePath.render path
    rendered.Substring(rendered.LastIndexOf '/' + 1)

let receiveFile (page: Page) (state: Opened) (sourceSystem: string) (observationId: string) (path: RelativePath) =
    async {
        match! state.Provider.Read state.Folder path with
        | Error failure -> return Error failure
        // Taken away meanwhile: by its producer, or by another page's pass.
        | Ok ReadOutcome.Absent
        | Ok(ReadOutcome.Erased _) -> return Ok None
        | Ok(ReadOutcome.Found found) ->
            let known: Observations.Inbox =
                { Observations.empty with Candidates = state.Stored.Candidates |> Map.map (fun _ stored -> stored.Candidate) }

            let consumed = [ path, found.Revision ]

            return
                Ok(
                    Some(
                        match Intake.decide (page.Now ()) state.Organization.Id known sourceSystem observationId found.Content with
                        // Its file stays, for its producer to see beside the receipt.
                        | Intake.Refused receipt -> { Stored.nothing with Receipts = [ receipt ] }, NotObservation(Intake.reasonsOf receipt |> Option.defaultValue [])
                        | Intake.Received(Observations.NewCandidate(candidate, receipt)) ->
                            // Decided below, when the source is trusted.
                            { Stored.nothing with
                                Candidates = [ candidate ]
                                Receipts = [ receipt ]
                                Consumed = consumed },
                            TakenIn
                        | Intake.Received(Observations.ReceiptRepair receipt) ->
                            { Stored.nothing with
                                Receipts = [ receipt ]
                                Consumed = consumed },
                            TakenIn
                        | Intake.Received(Observations.AlreadyReceived _) -> { Stored.nothing with Consumed = consumed }, TakenIn
                    )
                )
    }

/// Receipts that say an observation became a candidate no folder holds
/// (expansion 39): a receipt written outside Chrona, or its candidate
/// removed. Chrona writes a candidate and its receipt in one commit, so
/// this never comes from Chrona. Each receipt is read once per revision;
/// its candidate is looked for among the open candidates, then in the
/// decided months from the receipt's on.
let receiptsWithoutCandidate (page: Page) (reading: StorageProvider) (state: Opened) =
    async {
        match ReceiptRecord.rootFolder () with
        | Error _ -> return Ok []
        | Ok root ->
            match! reading.List state.Folder root with
            | Error failure -> return Error failure
            | Ok listing ->
                let sources = listing.Entries |> List.filter _.IsFolder |> List.map _.Path
                let! listings = sources |> List.map (reading.List state.Folder) |> InTurn.together page.Bridge

                match listings |> List.tryPick (function Error failure -> Some failure | Ok _ -> None) with
                | Some failure -> return Error failure
                | None ->
                    let files =
                        listings
                        |> List.collect (function
                            | Ok listing -> listing.Entries |> List.filter (fun entry -> not entry.IsFolder)
                            | Error _ -> [])

                    let unchecked =
                        files
                        |> List.filter (fun entry ->
                            match page.ReceiptChecks.TryFind(RelativePath.render entry.Path) with
                            | Some(revision, _) -> revision <> entry.Revision
                            | None -> true)

                    let! reads = unchecked |> List.map (fun entry -> reading.Read state.Folder entry.Path) |> InTurn.together page.Bridge

                    match reads |> List.tryPick (function Error failure -> Some failure | Ok _ -> None) with
                    | Some failure -> return Error failure
                    | None ->
                        // The candidates each unchecked receipt names, by the month it was received.
                        let named =
                            List.zip unchecked reads
                            |> List.choose (fun (entry, read) ->
                                match read with
                                | Ok(ReadOutcome.Found found) ->
                                    Record.decode Record.DefaultMaxBytes found.Content
                                    |> Result.toOption
                                    |> Option.bind (fun record -> ReceiptRecord.ofBody record.Body |> Result.toOption)
                                    |> Option.map (fun receipt -> entry, Some receipt)
                                | _ -> Some(entry, None))

                        let today = DateOnly.FromDateTime((page.Now ()).UtcDateTime)

                        let monthsFrom (at: DateTimeOffset) =
                            let first = DateOnly(at.UtcDateTime.Year, at.UtcDateTime.Month, 1)
                            List.unfold (fun (month: DateOnly) -> if month > today then None else Some(month, month.AddMonths 1)) first

                        let wanted =
                            named
                            |> List.collect (function
                                | _, Some { Outcome = Observations.CandidateRecorded id; At = at } when not (state.Stored.Candidates.ContainsKey id) -> monthsFrom at
                                | _ -> [])
                            |> List.distinct
                            |> List.choose (CandidateRecord.decidedFolder >> Result.toOption)

                        let! decided = wanted |> List.map (reading.List state.Folder) |> InTurn.together page.Bridge

                        match decided |> List.tryPick (function Error failure -> Some failure | Ok _ -> None) with
                        | Some failure -> return Error failure
                        | None ->
                            let held =
                                decided
                                |> List.collect (function
                                    | Ok listing -> listing.Entries |> List.map (fun entry -> lastSegment entry.Path)
                                    | Error _ -> [])
                                |> Set.ofList

                            let exists (id: string) = state.Stored.Candidates.ContainsKey id || held.Contains $"{id}.json"

                            for entry, receipt in named do
                                let orphan =
                                    match receipt with
                                    | Some { Outcome = Observations.CandidateRecorded id; SourceSystem = source; ObservationId = observation } when not (exists id) ->
                                        Some(source, observation, id)
                                    | _ -> None

                                page.ReceiptChecks <- page.ReceiptChecks.Add(RelativePath.render entry.Path, (entry.Revision, orphan))

                            // What every receipt still listed found, checked now or before.
                            let listed = files |> List.map (fun entry -> RelativePath.render entry.Path) |> Set.ofList

                            return
                                Ok(
                                    page.ReceiptChecks
                                    |> Map.toList
                                    |> List.filter (fun (path, _) -> listed.Contains path)
                                    |> List.choose (fun (_, (_, orphan)) -> orphan)
                                )
    }

/// The days an observation's time falls on, in the organization's zone.
let observedDates (zone: Time.Zone) (observation: Observations.Observation) =
    match observation.Timing with
    | Observations.ObservedInterval(start, finish) ->
        [ (Time.occurrence zone start).LocalDate; (Time.occurrence zone finish).LocalDate ] |> List.distinct
    | Observations.ObservedDuration _ -> [ (Time.occurrence zone observation.ObservedAt).LocalDate ]

/// One inbox file, received, and decided at once when its source is
/// trusted (expansion 19, WI-0074): the organization's policy for the
/// source accepts the observation as the named member's time, checked
/// against that person's months as stored, and the activity lands in the
/// same commit as its candidate and receipt. Time that cannot be recorded
/// as it is leaves the candidate needing a person.
let receive (page: Page) (state: Opened) (sourceSystem: string) (observationId: string) (path: RelativePath) =
    async {
        match! receiveFile page state sourceSystem observationId path with
        | Ok(Some(changed, TakenIn)) when not changed.Candidates.IsEmpty ->
            let candidate = changed.Candidates.Head
            let policy = Deployment.sourcePolicy state.Organization sourceSystem
            let members = state.Stored.Members |> Map.toList |> List.map (fun (_, found) -> found.Membership.Principal.PrincipalId) |> Set.ofList

            match policy, candidate.Observation.ActorId, Time.tryZone state.Organization.TimeZone with
            | { AutoAccept = true }, Some actor, Ok zone when members.Contains actor ->
                match! readFolders page.Bridge state.Provider state.Folder (Stored.folders actor (observedDates zone candidate.Observation)) with
                | Error reason -> return Error(StorageFailure.ProviderFailed("CHRONA.INBOX.READ", true, reasonOf reason))
                | Ok(folders, _) ->
                    let theirs = Stored.load (folders |> List.collect snd)

                    let periods =
                        match state.Stored.Periods with
                        | Some stored -> { stored.Periods.Config with ZoneId = state.Organization.TimeZone }
                        | None -> Periods.defaultConfig state.Organization.TimeZone

                    let context: Observations.DecisionContext =
                        { Ledger =
                            { Performer = "chrona"
                              At = page.Now ()
                              Source = $"policy:{sourceSystem}"
                              Zone = zone
                              References =
                                { OrganizationId = state.Organization.Id
                                  Items = state.Stored.References |> Map.toList |> List.map (fun (_, found) -> (found.Item.Kind, found.Item.Id), found.Item) |> Map.ofList }
                              CorrelationId = None }
                          Zone = zone }

                    match
                        Intake.police
                            policy
                            context
                            members
                            periods
                            (Stored.reviews theirs)
                            (page.NewKey "ACT")
                            candidate
                            (Persistence.ledgerOf theirs.Activities)
                    with
                    | Intake.ForReview -> return Ok(Some(changed, TakenIn))
                    | Intake.Attention decided -> return Ok(Some({ changed with Candidates = [ decided ] }, TakenIn))
                    | Intake.AutoAccepted(decided, activities, entries) ->
                        return
                            Ok(
                                Some(
                                    { changed with
                                        Candidates = [ decided ]
                                        Activities = activities
                                        Audit = AuditRecord.place activities entries },
                                    TakenIn
                                )
                            )
            | _ -> return Ok(Some(changed, TakenIn))
        | other -> return other
    }
