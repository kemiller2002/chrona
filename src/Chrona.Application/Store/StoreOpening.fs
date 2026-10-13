/// Opening the organization's records (2.4 to 2.7, Governance): from GitHub,
/// initializing Chrona's and the organization's folders when they are new
/// and appointing its first administrator; or, while GitHub cannot be
/// reached, from the read cache, as of when it was read (WI-0057).
module internal Chrona.Application.StoreOpening

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
open Chrona.Application.StoreReads
open Chrona.Application.StoreHolding

/// Opens a folder, initializing it first when it is new.
let ensure (provider: StorageProvider) (folder: Namespace) (initialize: unit -> Result<Operation, Diagnostic list>) =
    async {
        let readManifest () =
            async {
                match Layout.manifestPath with
                | Error error -> return Error(Unopened.Refused(LocationError.describe error))
                | Ok path ->
                    match! provider.Read folder path with
                    | Error failure -> return Error(unopened failure)
                    | Ok stored -> return Ok(Storage.openNamespace folder stored)
            }

        match! readManifest () with
        | Error reason -> return Error reason
        | Ok(Ok _) -> return Ok()
        | Ok(Error [ NamespaceNotInitialized _ ]) ->
            match initialize () with
            | Error [ PublicProductionRepository ] ->
                return Error(Unopened.Refused "Production records are never started in a public repository. Use a private repository.")
            | Error diagnostics -> return Error(Unopened.Refused $"The records could not be set up ({describeAll diagnostics}).")
            | Ok operation ->
                match! provider.Commit operation with
                | Ok _ -> return Ok()
                | Error failure -> return Error(unopened failure)
        | Ok(Error diagnostics) -> return Error(Unopened.Refused $"The configured folder cannot be used ({describeAll diagnostics}).")
    }

/// The repository, if the signed-in account can keep records in it.
let resolve (page: Page) (location: DataLocation) =
    async {
        match! page.Backend.Resolve location with
        | Error reason -> return Error reason
        | Ok snapshot when not snapshot.CanRead -> return Error(Unopened.Refused $"Your GitHub account cannot read {snapshot.Repository}.")
        | Ok snapshot when not snapshot.CanWrite -> return Error(Unopened.Refused $"Your GitHub account cannot write to {snapshot.Repository}.")
        | Ok snapshot when snapshot.Archived -> return Error(Unopened.Refused $"{snapshot.Repository} is archived.")
        | Ok snapshot ->
            match snapshot.Branch with
            | BranchAccess.Writable -> return Ok snapshot
            | BranchAccess.Missing ->
                return Error(Unopened.Refused $"The data branch {BranchName.value location.Branch} does not exist in {snapshot.Repository}.")
            | BranchAccess.NotWritable _ ->
                return Error(Unopened.Refused $"The data branch {BranchName.value location.Branch} does not accept direct changes.")
    }

/// Makes the session's person the organization's administrator: the
/// founder of a new organization, or a listed account confirming one
/// that has no listed administrator (Governance).
let appoint (page: Page) (state: Opened) =
    async {
        let roster = Stored.roster state.Session.OrganizationId state.Stored
        let membership = Governance.administrator (principalOf state.Session) roster
        let changed = { StoredChanges.nothing with Members = [ membership ] }

        match state.Basis, StoredChanges.changes state.Stored changed, operationContext state.Session (page.NewKey "appoint") (page.Now ()) with
        // Appointing is a write: never from the read cache (LCP-085).
        | CachedAt _, _, _ -> return Error "An administrator can be appointed only while GitHub can be reached."
        | ReadAt token, Ok changes, Ok context ->
            match Storage.operation state.Folder context "appoint an administrator" changes with
            | Error diagnostics -> return Error($"The administrator could not be recorded ({describeAll diagnostics}).")
            | Ok operation ->
                match! state.Provider.Commit(require token operation) with
                | Ok receipt ->
                    let! condition = conditionAfter state receipt

                    let next =
                        { state with
                            Basis = ReadAt condition
                            Stored = StoredOverlay.committed changed receipt state.Stored }

                    let! next = afterCommit page next operation receipt
                    return Ok next
                | Error failure -> return Error(describeFailure failure)
        | _, Error diagnostics, _
        | _, _, Error diagnostics -> return Error($"The administrator could not be recorded ({describeAll diagnostics}).")
    }

/// Why an organization waits for a listed administrator, for this person.
let confirmationReason (organization: Deployment.OrganizationConfig) (canConfirm: bool) =
    let accounts = organization.Administrators |> String.concat ", "

    match canConfirm, organization.Administrators with
    | true, _ ->
        $"{organization.DisplayName} has no administrator from this deployment's configuration. You are one of its listed administrators: confirm to administer it."
    | false, [] ->
        $"{organization.DisplayName} has no administrator from this deployment's configuration, and the configuration lists none. Nothing can be done in it until the deployment lists one."
    | false, _ ->
        $"{organization.DisplayName} has no administrator from this deployment's configuration. One of its listed administrators (GitHub accounts {accounts}) must sign in and confirm first."

/// Where the deployment keeps this session's organization.
let prepare (config: Deployment.DeploymentConfig) (session: Session) =
    match Storage.binding config, Deployment.organization config session.OrganizationId with
    | Ok binding, Some organization ->
        match Storage.applicationNamespace binding, Storage.organizationNamespace config binding organization.Id with
        | Ok application, Ok folder -> Ok(binding, application, folder, organization)
        | Error diagnostic, _
        | _, Error diagnostic -> Error(code diagnostic)
    | Error diagnostic, _ -> Error(code diagnostic)
    | _, None -> Error $"the deployment does not serve {session.OrganizationId}"

/// Opens the records from GitHub; what was read is kept in the read
/// cache. This page's own unsent changes (`carried`) follow what this
/// browser keeps.
let openFresh (page: Page) (config: Deployment.DeploymentConfig) (session: Session) (dates: DateOnly list) (carried: QueueEntry list) =
    async {
        let at = page.Now ()
        let context () = operationContext session (page.NewKey "open") at

        match prepare config session with
        | Error reason -> return Error(Unopened.Refused $"This deployment's storage is not configured correctly ({reason}).")
        | Ok(binding, application, folder, organization) ->
            match! resolve page binding.Location with
            | Error reason -> return Error reason
            | Ok home ->
                let applicationConnection = page.Backend.Connect binding.Location
                let applicationProvider = applicationConnection.Provider

                match!
                    ensure applicationProvider application (fun () ->
                        context () |> Result.bind (Storage.initializeApplication binding home.Visibility None))
                with
                | Error reason -> return Error reason
                | Ok() ->
                    let separate = folder.Location <> binding.Location
                    let! own = if separate then resolve page folder.Location else async.Return(Ok home)

                    match own with
                    | Error reason -> return Error reason
                    | Ok repository ->
                        let connection = if separate then page.Backend.Connect folder.Location else applicationConnection
                        let provider = connection.Provider
                        let manifest = Organization.create organization.Id organization.DisplayName organization.Slug organization.TimeZone at

                        // The organization's manifest is read twice while opening
                        // (whether it exists, then to open it): both at one state
                        // observed once, never resolving the branch for each
                        // (WI-0081). Setting it up still commits through the
                        // provider, conditioned as always.
                        let! observedFolder = provider.NamespaceState folder

                        let opening =
                            match observedFolder with
                            | Ok observed -> pinnedAt connection observed.RepositoryToken
                            | Error _ -> provider

                        let! existing =
                            async {
                                match Layout.manifestPath with
                                | Error error -> return Error(Unopened.Refused(LocationError.describe error))
                                | Ok path ->
                                    match! opening.Read folder path with
                                    | Ok ReadOutcome.Absent -> return Ok false
                                    | Ok(ReadOutcome.Found _)
                                    | Ok(ReadOutcome.Erased _) -> return Ok true
                                    | Error failure -> return Error(unopened failure)
                            }

                        let decideFor isNew roster =
                            Governance.decide config.Environment organization isNew roster session.ActorId

                        let nobody: Access.Roster = { OrganizationId = organization.Id; Members = Map.empty }

                        match existing, decideFor true nobody with
                        | Error reason, _ -> return Error reason
                        // Nothing is written for someone who may not set it up.
                        | Ok false, Governance.Refused reason -> return Error(Unopened.Refused reason)
                        | Ok isExisting, _ ->
                            match!
                                ensure opening folder (fun () ->
                                    context ()
                                    |> Result.bind (fun context -> Storage.initializeOrganization config binding repository.Visibility None context manifest))
                            with
                            | Error reason -> return Error reason
                            | Ok() ->
                                let months = dates |> List.map monthOf |> Set.ofList

                                match! load page connection folder session.ActorId months Set.empty Map.empty with
                                | Error reason -> return Error reason
                                | Ok(token, stored, read, histories) ->
                                    let state =
                                        { Session = session
                                          Folder = folder
                                          Provider = provider
                                          PinAt = connection.PinAt
                                          Name = string folder.Location.Repository
                                          Basis = ReadAt token
                                          Stored = stored
                                          Read = read
                                          Months = months
                                          Others = Set.empty
                                          Histories = histories
                                          Organization = organization
                                          Queue = { OfflineQueue.create OfflinePolicy.QueueWrites with Entries = carried }
                                          Owned = None
                                          Holder = HeldHere
                                          Note = None
                                          Notice = None
                                          Index = { Index = None; Revision = None; Note = None } }

                                    // A new organization's activity index starts empty.
                                    // The index at the state load read (WI-0081).
                                    let! indexed =
                                        readIndex
                                            (pinnedFor connection token)
                                            folder
                                            session.ActorId
                                            months
                                            Set.empty
                                            stored
                                            { state.Index with Index = (if isExisting then None else Some ActivityIndex.empty) }

                                    let state = { state with Index = indexed }
                                    do! keepIndex page state token

                                    let roster = Stored.roster organization.Id stored

                                    match decideFor (not isExisting) roster with
                                    | Governance.Proceed ->
                                        let! messages = ready page state
                                        return Ok messages
                                    | Governance.Found ->
                                        match! appoint page state with
                                        | Error reason -> return Error(Unopened.Refused reason)
                                        | Ok state ->
                                            let! messages = ready page state
                                            return Ok messages
                                    | Governance.NeedsConfirmation canConfirm ->
                                        // Nothing is granted: the records stay closed
                                        // until a listed account confirms.
                                        page.Opened <- None
                                        page.Pending <- Some state
                                        return Ok [ Update.StoreNeedsConfirmation(confirmationReason organization canConfirm, canConfirm) ]
                                    | Governance.Refused reason -> return Error(Unopened.Refused reason)
    }

/// Opens the records from the read cache while GitHub cannot be reached
/// (WI-0057, LCP-082..084): what GitHub last gave this account, shown as
/// of when it was read, never as current. The organization's people and
/// reference data must be cached; a month counts as read only when all
/// of its folders are. None when the cache cannot stand in.
let openCached (page: Page) (config: Deployment.DeploymentConfig) (session: Session) (dates: DateOnly list) (carried: QueueEntry list) =
    async {
        match prepare config session, Stored.folders session.ActorId [] with
        | Error _, _
        | _, Error _ -> return None
        | Ok(_, _, folder, organization), Ok common ->
            match! readCache page () with
            | None -> return None
            | Some cached ->
                // A partition as GitHub last gave it: still shown, as of
                // its token, while GitHub cannot be reached (LCP-084).
                let show (partition: string) =
                    async {
                        match ReadCache.key session.ActorId folder partition with
                        | Error _ -> return None
                        | Ok key ->
                            match! cached.Show key with
                            | Ok(Some entry) ->
                                match ReadCache.revalidate ProviderObservation.Unreachable entry with
                                | Revalidation.Unverified shown -> return Some shown
                                | _ -> return None
                            | _ -> return None
                    }

                // One after another, on this page's own turn: never
                // Async.Parallel or Sequential, which leave the bridge's
                // thread.
                let showAll (partitions: string list) =
                    async {
                        let mutable shown = []

                        for partition in partitions do
                            let! one = show partition
                            shown <- shown @ [ one ]

                        return shown
                    }

                let isHeld (entry: Cached<Arca.CacheEntry>) =
                    (Cached.key entry).Partition.StartsWith(HeldPrefix, StringComparison.Ordinal)

                let objectsOf (entry: Cached<Arca.CacheEntry>) =
                    (Cached.value entry).Records
                    |> List.choose (fun (record: CachedRecord) ->
                        RelativePath.parse record.Path
                        |> Result.toOption
                        // A cached record carries no revision: it is never
                        // the basis of a write (LCP-085).
                        |> Option.map (fun path ->
                            ({ Path = path
                               Content = record.Content
                               Revision = Revision("cached:" + record.ContentHash) }: StoredObject)))

                let! shared = common |> List.map RelativePath.render |> showAll

                // Candidates kept by a Chrona before WI-0038 are not
                // cached yet: shown once GitHub is read again.
                let optional = CandidateRecord.openFolder () |> Result.toOption

                let missing =
                    List.zip common shared |> List.exists (fun (path, shown) -> shown.IsNone && Some path <> optional)

                if missing then
                    return None
                else
                    let mutable entries = shared |> List.choose id
                    let mutable months = Set.empty

                    for month in dates |> List.map monthOf |> List.distinct do
                        match Stored.folders session.ActorId [ firstOf month ] with
                        | Ok all ->
                            let own = all |> List.filter (fun path -> not (List.contains path common)) |> List.map RelativePath.render
                            let! found = showAll own
                            let! held = own |> List.map (fun path -> HeldPrefix + path) |> showAll

                            if found |> List.forall Option.isSome then
                                months <- months.Add month
                                entries <- entries @ (found |> List.choose id) @ (held |> List.choose id)
                        | Error _ -> ()

                    let heldPaths =
                        entries |> List.filter isHeld |> List.collect objectsOf |> List.map (fun found -> RelativePath.render found.Path) |> Set.ofList

                    let stored = Stored.load (entries |> List.filter (isHeld >> not) |> List.collect objectsOf)
                    let stored = { stored with Activities = Persistence.holdPaths heldPaths stored.Activities }
                    let! index = show IndexPartition
                    let unread = "The activity index is read again when GitHub can be reached."

                    let indexed =
                        match index |> Option.map objectsOf with
                        | Some [ found ] ->
                            match Derived.decode found.Content with
                            | Ok index -> { Index = Some index; Revision = None; Note = None }
                            | Error _ -> { Index = None; Revision = None; Note = Some unread }
                        | _ -> { Index = None; Revision = None; Note = Some unread }

                    let state =
                        { Session = session
                          Folder = folder
                          Provider = (page.Backend.Connect folder.Location).Provider
                          PinAt = None
                          Name = string folder.Location.Repository
                          // Shown as of the oldest partition it holds.
                          Basis = CachedAt(entries |> List.map Cached.readAt |> List.min)
                          Stored = stored
                          Read = Map.empty
                          Months = months
                          Others = Set.empty
                          Histories = Map.empty
                          Organization = organization
                          Queue = { OfflineQueue.create OfflinePolicy.QueueWrites with Entries = carried }
                          Owned = None
                          Holder = HeldHere
                          Note = None
                          Notice = None
                          Index = indexed }

                    // Only an organization this person already works in
                    // opens from the cache: setting one up, or confirming
                    // an administrator, needs GitHub.
                    match Governance.decide config.Environment organization false (Stored.roster organization.Id stored) session.ActorId with
                    | Governance.Proceed ->
                        let! messages = ready page state
                        return Some messages
                    | _ -> return None
    }
