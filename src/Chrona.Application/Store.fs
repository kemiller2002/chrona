/// The store port (DF-CHRONA-2026-0003): what the engine asks to be made
/// durable, made durable through Arca (WI-0032).
///
/// The engine asks to open the organization's records (`OpenStore`) and to
/// commit a command's records (`Store`); this module answers with
/// `StoreOpened`, `StoreUnavailable` and `StoreAnswered`. It runs Arca's
/// provider-neutral `StorageProvider`: in the browser, Arca's GitHub adapter,
/// whose every request is a Limen Http request through the `Bridge` and
/// whose token comes from Fides' token provider (never from Chrona); in
/// tests, Arca's in-memory provider. The rules are the domain's
/// (`Storage`, `Organization`, `Stored`, `Persistence`); this module only
/// sequences them.
///
/// - Opening resolves the repository with the signed-in credential (what it
///   is, whether it is public, whether it may be written), refuses to start
///   production data in a public repository (2.7), initializes Chrona's and
///   the organization's folders when they are new (2.4 to 2.6), opens them
///   only when their manifests match the configuration, reads the reference
///   folders and the month folders the period needs (38), validates
///   everything read (39) and holds records edited outside Chrona (41).
/// - Committing turns the request into one Arca operation, one commit, every
///   change conditioned on the revision last read. A conflict reloads the
///   records and is surfaced; an unknown outcome is reconciled before
///   anything is sent again (21, 31). Commits run one at a time.
module Chrona.Application.Store

open System
open System.Collections.Generic
open Arca
open Arca.GitHub
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.Model
open Chrona.Application.Bridge

/// What the store needs from where the data lives.
[<NoComparison; NoEquality>]
type Backend =
    { /// The provider serving a location.
      Provider: DataLocation -> StorageProvider
      /// The repository's facts for the signed-in credential: its visibility
      /// and whether it may be read and written (ARCA-AUTH-003, 2.7). A
      /// refusal says why, for the person.
      Resolve: DataLocation -> Async<Result<CapabilitySnapshot, string>> }

/// The store port the application drives.
[<NoComparison; NoEquality>]
type StorePort =
    { Kind: StoreKind
      Open: Deployment.DeploymentConfig -> Session -> DateOnly list -> unit
      Commit: StoreRequest -> unit }

/// A store that keeps nothing: every commit is acknowledged at once. For a
/// deployment that configures no location.
let inMemory (bridge: Bridge) : StorePort =
    { Kind = InMemory
      Open = fun _ _ _ -> bridge.Start(async { return [ Update.StoreUnavailable "This deployment stores nothing." ] })
      Commit = fun request -> bridge.Start(async { return [ Update.StoreAnswered(request.CommitId, Committed) ] }) }

// ---- GitHub, through the bridge ----------------------------------------------------

let private methodName =
    function
    | HttpMethod.Get -> "GET"
    | HttpMethod.Post -> "POST"
    | HttpMethod.Patch -> "PATCH"
    | HttpMethod.Put -> "PUT"
    | HttpMethod.Delete -> "DELETE"

let private describeResolve =
    function
    | ResolveError.CredentialUnavailable _ -> "You are not signed in to GitHub any more. Sign in again."
    | ResolveError.CredentialRejected -> "GitHub refused your sign-in. Sign in again."
    | ResolveError.RepositoryNotFound repository -> $"Your GitHub account cannot see {repository}."
    | ResolveError.Call _ -> "GitHub could not be reached."

/// Arca's GitHub adapter, its requests sent as Limen Http requests, its waits
/// as `limen.schedule` timeouts, its tokens from Fides. A 401 is reported to
/// Fides, which then asks the person to sign in again.
let gitHub (bridge: Bridge) (tokens: unit -> TokenProvider option) (unauthorized: unit -> Async<unit>) : Backend =
    let host: Host =
        { Send =
            fun authorized ->
                async {
                    let request = authorized.Request
                    let credential = authorized.Credential |> Option.map AccessToken.authorization |> Option.toList

                    match!
                        bridge.Call(
                            Http(methodName request.Method, request.Url, request.Headers @ credential, request.Body, request.TimeoutMs, request.ResponseHeaders)
                        )
                    with
                    | Answered(AppProtocol.HttpSucceeded(status, headers, body)) ->
                        if status = 401 && authorized.Credential.IsSome then
                            do! unauthorized ()

                        return HttpOutcome.Response(status, Http.normalizeHeaders headers, body)
                    | Answered(AppProtocol.HttpFailed reason) ->
                        return
                            HttpOutcome.Failed(
                                match reason with
                                | "aborted" -> HttpFailure.Aborted
                                | "invalid-response" -> HttpFailure.InvalidResponse
                                | "too-large" -> HttpFailure.TooLarge
                                | _ -> HttpFailure.Network
                            )
                    | Answered AppProtocol.HttpCancelled -> return HttpOutcome.Cancelled
                    | Answered(AppProtocol.HttpUnknown reason) ->
                        return
                            HttpOutcome.OutcomeUnknown(
                                if reason = "connection-lost" then UnknownReason.ConnectionLost else UnknownReason.TimeoutAfterDispatch
                            )
                    | Read _
                    | Done -> return HttpOutcome.Failed HttpFailure.InvalidResponse
                }
          Wait = fun delay -> bridge.Call(Sleep(int delay.TotalMilliseconds)) |> Async.Ignore
          Tokens =
            fun () ->
                match tokens () with
                | Some provider -> provider ()
                | None -> async.Return(Error TokenUnavailable.NoToken) }

    { Provider = fun location -> GitHubStorage.provider host (GitHubConfig.create location)
      Resolve =
        fun location ->
            async {
                match! Conversation.run host (Arca.GitHub.Identity.resolve (GitHubConfig.create location)) with
                | Ok snapshot -> return Ok snapshot
                | Error error -> return Error(describeResolve error)
            } }

// ---- The Arca store -----------------------------------------------------------------

/// The organization's records as opened.
[<NoComparison; NoEquality>]
type private Opened =
    { Session: Session
      Folder: Namespace
      Provider: StorageProvider
      Name: string
      /// The repository state the records were read at (21).
      Token: ChangeToken
      Stored: Stored.Stored
      /// The months read so far.
      Months: Set<int * int> }

let private describeFailure =
    function
    | StorageFailure.Refused(WriteRefusal.CredentialUnavailable _) -> "You are not signed in to GitHub any more. Sign in again; nothing was saved."
    | StorageFailure.Refused WriteRefusal.ReadOnlyAccess -> "Your GitHub account can read this repository but not write to it."
    | StorageFailure.Refused WriteRefusal.BranchProtected -> "The data branch does not accept direct changes."
    | StorageFailure.Refused WriteRefusal.RepositoryArchived -> "The data repository is archived."
    | StorageFailure.Refused(WriteRefusal.RepositoryIdentityChanged _) -> "The configured repository is now a different repository."
    | StorageFailure.Refused(WriteRefusal.CapabilityUnavailable _) -> "The store cannot make this change."
    | StorageFailure.Conflicted _ -> "Something changed elsewhere."
    | StorageFailure.OutcomeUnknown _ -> "GitHub did not say whether the change was saved."
    | StorageFailure.ObjectTooLarge(path, _, _) -> $"{path} is too large to store."
    | StorageFailure.StaleChangeToken _ -> "The repository changed while saving."
    | StorageFailure.RateLimited _ -> "GitHub's rate limit is used up for now. Try again shortly."
    | StorageFailure.WrongLocation _ -> "The store is not serving the configured repository."
    | StorageFailure.IntegrityRefused(path, _) -> $"{path} could not be changed safely: it is not a valid record."
    | StorageFailure.ProviderFailed(_, _, detail) -> $"GitHub could not be reached ({detail})."

let private describeAll (diagnostics: Diagnostic list) =
    diagnostics |> List.map code |> List.distinct |> String.concat ", "

let private operationContext (session: Session) (key: string) (at: DateTimeOffset) : Result<Storage.OperationContext, Diagnostic list> =
    match ActorId.create session.ActorId, CorrelationId.create key, IdempotencyKey.create key with
    | Ok actor, Ok correlation, Ok idempotency ->
        Ok
            { Actor = { Kind = ActorKind.Human; Id = actor }
              ProviderIdentity = Some session.DisplayName
              CorrelationId = correlation
              IdempotencyKey = idempotency
              At = at }
    | _ -> Error [ StorageOperationRefused "the session's actor or the change's id is not a valid identifier" ]

let private monthOf (date: DateOnly) = date.Year, date.Month
let private firstOf (year: int, month: int) = DateOnly(year, month, 1)

/// The request's records that still need writing on top of what is stored,
/// or why the request no longer fits what is stored (21: Chrona's rules
/// decide again on the current state; a clean merge is not proof). A record
/// already stored exactly as requested (an earlier attempt landed) needs
/// nothing; one the request changed must be the next revision of the stored
/// one; new time must not overlap stored time.
let private revalidate (stored: Stored.Stored) (request: StoreRequest) =
    let activities = stored.Activities.Activities

    let pending =
        request.Activities
        |> List.filter (fun activity ->
            match activities.TryFind activity.ActivityId with
            | Some found -> found.Activity <> activity
            | None -> true)

    let references =
        request.References
        |> List.filter (fun item ->
            match stored.References.TryFind(Stored.referenceKey item) with
            | Some found -> found.Item <> item
            | None -> true)

    let staleActivity =
        pending
        |> List.tryFind (fun activity ->
            match activities.TryFind activity.ActivityId with
            | Some found -> found.Activity.Revision <> activity.Revision - 1
            | None -> activity.Revision <> 1)

    let staleReference =
        references
        |> List.tryFind (fun item ->
            match stored.References.TryFind(Stored.referenceKey item) with
            | Some found -> found.Item.Revision <> item.Revision - 1
            | None -> item.Revision <> 1)

    let others =
        activities
        |> Map.toList
        |> List.map (fun (_, found) -> found.Activity)
        |> List.filter (fun other -> not (request.Activities |> List.exists (fun a -> a.ActivityId = other.ActivityId)))

    let overlapping =
        pending
        |> List.filter Activity.consumesTime
        |> List.exists (fun activity -> not (Overlap.overlapping others activity).IsEmpty)

    match staleActivity, staleReference with
    | Some activity, _ -> Error $"Activity \"{activity.Classification.Description}\" was changed elsewhere first."
    | _, Some item -> Error $"\"{item.Name}\" was changed elsewhere first."
    | None, None when overlapping -> Error "It overlaps time recorded elsewhere."
    | None, None -> Ok(pending, references)

/// The Arca store over a backend. `now` is the clock; `newKey` mints the
/// idempotency keys of the operations the store starts itself. It keeps
/// commits in memory until the engine opens storage, which it does only for
/// a deployment that configures a location.
let arca (bridge: Bridge) (backend: Backend) (now: unit -> DateTimeOffset) (newKey: string -> string) : StorePort =
    let mutable opened: Opened option = None
    let queue = Queue<unit -> Async<Update.Msg list>>()
    let mutable busy = false

    /// Runs the queued jobs one at a time; each job's messages go to the
    /// engine as it finishes.
    let rec run () =
        async {
            if queue.Count = 0 then
                busy <- false
            else
                let job = queue.Dequeue()

                let! messages =
                    async {
                        try
                            return! job ()
                        with error ->
                            queue.Clear()
                            busy <- false
                            return raise error
                    }

                bridge.Emit messages
                return! run ()
        }

    let serial (job: unit -> Async<Update.Msg list>) =
        queue.Enqueue job

        if not busy then
            busy <- true

            bridge.Start(
                async {
                    do! run ()
                    return []
                }
            )

    /// Reads the folders these dates need.
    let read (provider: StorageProvider) (folder: Namespace) (actorId: string) (dates: DateOnly list) : Async<Result<StoredObject list * Diagnostic list, string>> =
        async {
            match Stored.folders actorId dates with
            | Error diagnostic -> return Error(code diagnostic)
            | Ok folders ->
                let mutable objects = []
                let mutable problems = []
                let mutable failure = None

                for path in folders do
                    if failure.IsNone then
                        match! provider.List folder path with
                        | Error error -> failure <- Some(describeFailure error)
                        | Ok listing ->
                            let files, incomplete = Persistence.recordFiles path listing
                            problems <- problems @ incomplete

                            for file in files do
                                if failure.IsNone then
                                    match! provider.Read folder file with
                                    | Ok(ReadOutcome.Found found) -> objects <- objects @ [ found ]
                                    | Ok ReadOutcome.Absent -> ()
                                    | Error error -> failure <- Some(describeFailure error)

                match failure with
                | Some reason -> return Error reason
                | None -> return Ok(objects, problems)
        }

    /// The repository state, then what the folders hold at it: validated,
    /// with records edited outside Chrona held for review.
    let load (provider: StorageProvider) (folder: Namespace) (actorId: string) (months: Set<int * int>) =
        async {
            match! provider.ChangeToken folder with
            | Error error -> return Error(describeFailure error)
            | Ok token ->
                match! read provider folder actorId (months |> Set.toList |> List.map firstOf) with
                | Error reason -> return Error reason
                | Ok(objects, incomplete) ->
                    let stored = Stored.load objects
                    let mutable histories = Map.empty
                    let mutable failure = None

                    for KeyValue(_, found) in stored.Activities.Activities do
                        if failure.IsNone then
                            match! provider.History folder found.Path with
                            | Ok history -> histories <- histories.Add(RelativePath.render found.Path, history)
                            | Error error -> failure <- Some(describeFailure error)

                    match failure with
                    | Some reason -> return Error reason
                    | None ->
                        let activities = Persistence.holdExternalEdits histories stored.Activities

                        return
                            Ok(
                                token,
                                { stored with
                                    Activities = { activities with Problems = incomplete @ activities.Problems } }
                            )
        }

    let contents (state: Opened) : StoreContents =
        { Name = state.Name
          Activities = state.Stored.Activities.Activities |> Map.toList |> List.map (fun (_, found) -> found.Activity)
          References = state.Stored.References |> Map.toList |> List.map (fun (_, found) -> found.Item)
          Attestations = Stored.attestations state.Stored
          Problems = Stored.problems state.Stored }

    /// Reads the state again: the same months, plus any these dates add.
    let refresh (state: Opened) (dates: DateOnly list) =
        async {
            let months = state.Months + (dates |> List.map monthOf |> Set.ofList)

            match! load state.Provider state.Folder state.Session.ActorId months with
            | Error reason -> return Error reason
            | Ok(token, stored) ->
                let next =
                    { state with
                        Token = token
                        Stored = stored
                        Months = months }

                opened <- Some next
                return Ok next
        }

    /// Opens a folder, initializing it first when it is new.
    let ensure (provider: StorageProvider) (folder: Namespace) (initialize: unit -> Result<Operation, Diagnostic list>) =
        async {
            let readManifest () =
                async {
                    match Layout.manifestPath with
                    | Error error -> return Error(LocationError.describe error)
                    | Ok path ->
                        match! provider.Read folder path with
                        | Error failure -> return Error(describeFailure failure)
                        | Ok stored -> return Ok(Storage.openNamespace folder stored)
                }

            match! readManifest () with
            | Error reason -> return Error reason
            | Ok(Ok _) -> return Ok()
            | Ok(Error [ NamespaceNotInitialized _ ]) ->
                match initialize () with
                | Error [ PublicProductionRepository ] ->
                    return Error "Production records are never started in a public repository. Use a private repository."
                | Error diagnostics -> return Error($"The records could not be set up ({describeAll diagnostics}).")
                | Ok operation ->
                    match! provider.Commit operation with
                    | Ok _ -> return Ok()
                    | Error failure -> return Error(describeFailure failure)
            | Ok(Error diagnostics) -> return Error($"The configured folder cannot be used ({describeAll diagnostics}).")
        }

    /// The repository, if the signed-in account can keep records in it.
    let resolve (location: DataLocation) =
        async {
            match! backend.Resolve location with
            | Error reason -> return Error reason
            | Ok snapshot when not snapshot.CanRead -> return Error $"Your GitHub account cannot read {snapshot.Repository}."
            | Ok snapshot when not snapshot.CanWrite -> return Error $"Your GitHub account cannot write to {snapshot.Repository}."
            | Ok snapshot when snapshot.Archived -> return Error $"{snapshot.Repository} is archived."
            | Ok snapshot ->
                match snapshot.Branch with
                | BranchAccess.Writable -> return Ok snapshot
                | BranchAccess.Missing -> return Error $"The data branch {BranchName.value location.Branch} does not exist in {snapshot.Repository}."
                | BranchAccess.NotWritable _ -> return Error $"The data branch {BranchName.value location.Branch} does not accept direct changes."
        }

    let openJob (config: Deployment.DeploymentConfig) (session: Session) (dates: DateOnly list) () =
        async {
            let at = now ()

            let prepared =
                match Storage.binding config, config.Organization with
                | Ok binding, Some organization ->
                    match Storage.applicationNamespace binding, Storage.organizationNamespace config binding organization.Id with
                    | Ok application, Ok folder -> Ok(binding, application, folder, organization)
                    | Error diagnostic, _
                    | _, Error diagnostic -> Error(code diagnostic)
                | Error diagnostic, _ -> Error(code diagnostic)
                | _, None -> Error "the deployment names no organization"

            let context () = operationContext session (newKey "open") at

            match prepared with
            | Error reason -> return [ Update.StoreUnavailable $"This deployment's storage is not configured correctly ({reason})." ]
            | Ok(binding, application, folder, organization) ->
                match! resolve binding.Location with
                | Error reason -> return [ Update.StoreUnavailable reason ]
                | Ok home ->
                    let applicationProvider = backend.Provider binding.Location

                    match!
                        ensure applicationProvider application (fun () ->
                            context () |> Result.bind (Storage.initializeApplication binding home.Visibility None))
                    with
                    | Error reason -> return [ Update.StoreUnavailable reason ]
                    | Ok() ->
                        let separate = folder.Location <> binding.Location
                        let! own = if separate then resolve folder.Location else async.Return(Ok home)

                        match own with
                        | Error reason -> return [ Update.StoreUnavailable reason ]
                        | Ok repository ->
                            let provider = if separate then backend.Provider folder.Location else applicationProvider
                            let manifest = Organization.create organization.Id organization.DisplayName organization.Slug organization.TimeZone at

                            match!
                                ensure provider folder (fun () ->
                                    context ()
                                    |> Result.bind (fun context -> Storage.initializeOrganization config binding repository.Visibility None context manifest))
                            with
                            | Error reason -> return [ Update.StoreUnavailable reason ]
                            | Ok() ->
                                let months = dates |> List.map monthOf |> Set.ofList

                                match! load provider folder session.ActorId months with
                                | Error reason -> return [ Update.StoreUnavailable reason ]
                                | Ok(token, stored) ->
                                    let state =
                                        { Session = session
                                          Folder = folder
                                          Provider = provider
                                          Name = string folder.Location.Repository
                                          Token = token
                                          Stored = stored
                                          Months = months }

                                    opened <- Some state
                                    return [ Update.StoreOpened(contents state) ]
        }

    /// The most a commit is decided again after the repository moved.
    let attempts = 3

    let commitJob (request: StoreRequest) () =
        async {
            let answer outcome = [ Update.StoreAnswered(request.CommitId, outcome) ]

            let refreshed (state: Opened) =
                // Others' independent changes are shown once nothing of this
                // page's own is waiting to be stored.
                if queue.Count = 0 then [ Update.StoreOpened(contents state) ] else []

            let rec attempt (state: Opened) (tries: int) =
                async {
                    match revalidate state.Stored request with
                    | Error reason -> return refreshed state @ answer (Conflict $"{reason} The stored records were reloaded.")
                    | Ok([], []) when request.Attestations.IsEmpty -> return answer Committed
                    | Ok(activities, references) ->
                        match
                            Stored.changes state.Stored activities references request.Attestations,
                            operationContext state.Session request.CommitId (now ())
                        with
                        | Error diagnostics, _
                        | _, Error diagnostics -> return answer (Failed $"This change cannot be stored ({describeAll diagnostics}).")
                        | Ok [], _ -> return answer Committed
                        | Ok changes, Ok context ->
                            match Storage.operation state.Folder context "save changes" changes with
                            | Error diagnostics -> return answer (Failed $"This change cannot be stored ({describeAll diagnostics}).")
                            | Ok operation ->
                                // Conditioned on the repository state the records were read at.
                                let operation = Operation.requireChangeToken state.Token operation

                                let landed (receipt: CommitReceipt) =
                                    let next =
                                        { state with
                                            Token = receipt.ChangeToken
                                            Stored = Stored.committed activities references request.Attestations receipt state.Stored }

                                    opened <- Some next
                                    (if tries > 1 then refreshed next else []) @ answer Committed

                                let decideAgain () =
                                    async {
                                        match! refresh state [] with
                                        | Error reason -> return answer (Failed reason)
                                        | Ok fresh when tries < attempts -> return! attempt fresh (tries + 1)
                                        | Ok fresh -> return refreshed fresh @ answer (Conflict "The records kept changing elsewhere. They were reloaded.")
                                    }

                                match! state.Provider.Commit operation with
                                | Ok receipt -> return landed receipt
                                | Error(StorageFailure.StaleChangeToken _)
                                | Error(StorageFailure.Conflicted _) -> return! decideAgain ()
                                | Error(StorageFailure.OutcomeUnknown pending) ->
                                    match! state.Provider.Reconcile state.Folder pending with
                                    | Ok(ReconcileOutcome.Landed receipt) -> return landed receipt
                                    // It did not land: safe to decide again on the current state.
                                    | Ok ReconcileOutcome.NotLanded -> return! decideAgain ()
                                    | Ok(ReconcileOutcome.StillUnknown _)
                                    | Error _ ->
                                        return
                                            answer (
                                                OutcomeUnknown "GitHub did not say whether the change was saved. It will be checked before anything is sent again."
                                            )
                                | Error failure -> return answer (Failed(describeFailure failure))
                }

            match opened with
            | None -> return answer (Failed "The records are not open.")
            | Some state ->
                // A change is never checked against less than what is stored:
                // months it touches that were not read yet are read first.
                let unread =
                    request.Activities
                    |> List.map (fun activity -> activity.Occurrence.LocalDate)
                    |> List.filter (fun date -> not (state.Months.Contains(monthOf date)))

                if unread.IsEmpty then
                    return! attempt state 1
                else
                    match! refresh state unread with
                    | Error reason -> return answer (Failed reason)
                    | Ok wider -> return! attempt wider 1
        }

    // Until the engine opens storage (a deployment that configures a
    // location), commits are kept in memory and acknowledged at once.
    let memory = inMemory bridge
    let mutable requested = false

    { Kind = InMemory
      Open =
        fun config session dates ->
            requested <- true
            serial (openJob config session dates)
      Commit =
        fun request ->
            if requested then
                serial (commitJob request)
            else
                memory.Commit request }
