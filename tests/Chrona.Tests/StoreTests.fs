/// The store port on Arca (WI-0032): the engine's records opened and
/// committed through Arca's provider, against Arca's in-memory provider,
/// as the application drives it (requirements expansion 2.4 to 2.7, 21, 22,
/// 38, 39, 41). The GitHub backend's transport is checked through the
/// bridge with a fake GitHub.
module Chrona.Tests.StoreTests

open System
open Xunit
open Arca
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update
open Chrona.Application

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private configuration (environment: string) =
    """{"environment":"ENV","environmentName":"ENV","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":"deployments"},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://chrona.test/"},"organizations":[{"id":"org_acme","displayName":"Acme Consulting","slug":"acme","timeZone":"America/New_York","administrators":["583231"]}]}"""
        .Replace("ENV", environment)
    |> Deployment.parse
    |> ok

let private start = DateTimeOffset(2026, 10, 8, 14, 10, 0, TimeSpan.FromHours -4.0)

let private octocat =
    { ActorId = "github:583231"
      OrganizationId = "local"
      DisplayName = "octocat"
      Kind = SignedIn "github" }

let private snapshot (visibility: RepositoryVisibility) : CapabilitySnapshot =
    { Identity =
        { Provider = "github"
          Subject = "583231"
          Login = Some "octocat"
          Kind = IdentityKind.User }
      RepositoryId = "R_1"
      Repository = RepositoryRef.create "acme" "chrona-data" |> ok
      Visibility = visibility
      CanRead = true
      CanWrite = true
      Archived = false
      Branch = BranchAccess.Writable }

let private keys = ref 0

/// One browser's localStorage, IndexedDB (Limen's store pack, through its
/// fake) and Web Locks, shared by the pages opened in it.
type private Browser() =
    let origin = Limen.Store.FakeStore.origin None
    let mutable tabs = 0
    let mutable handles = 0

    member val Storage = Collections.Generic.Dictionary<string, string>()
    /// The browser refuses localStorage requests with this reason, when set.
    member val Refusing: string option = None with get, set
    /// The browser has Web Locks (every current browser does).
    member val WebLocks = true with get, set
    /// The page negotiated Limen's store pack (IndexedDB).
    member val IndexedDb = true with get, set
    /// Who holds each lock: its handle, the page, and how that page hears
    /// that another took it.
    member val Held = Collections.Generic.Dictionary<string, string * obj * (unit -> unit)>()
    /// Pages waiting for a lock, oldest first, and how each is told.
    member val Waiting = Collections.Generic.List<string * obj * (string -> unit)>()
    /// LockLost facts not delivered yet: the page, and how it hears it.
    member val Lost = Collections.Generic.List<obj * (unit -> unit)>()

    /// A new tab's IndexedDB, in this browser's origin.
    member _.Tab() =
        tabs <- tabs + 1
        origin $"tab-{tabs}" (Limen.Store.FakeTabConfig.create "chrona")

    /// Answers a coordination request from a page (Web Locks).
    member this.Coordinate (page: obj) (lost: unit -> unit) (answer: Limen.Contract.Coordination.Types.CoordinationResult -> unit) (request: Limen.Contract.Coordination.Types.CoordinationRequest) =
        let fresh () =
            handles <- handles + 1
            $"lock-{handles}"

        match request with
        | _ when not this.WebLocks -> answer Limen.Contract.Coordination.Types.CoordinationResult.Unsupported
        | Limen.Contract.Coordination.Types.CoordinationRequest.Acquire(name, _, wait, steal) ->
            match this.Held.TryGetValue name with
            | true, (_, holder, told) when steal ->
                // The holder hears LockLost; the lock is this page's.
                this.Lost.Add((holder, told))
                let handle = fresh ()
                this.Held[name] <- (handle, page, lost)
                answer (Limen.Contract.Coordination.Types.CoordinationResult.Acquired(Limen.Contract.Coordination.Types.LockHandle handle))
            | true, _ when wait ->
                this.Waiting.Add(
                    (name,
                     page,
                     fun handle ->
                         answer (Limen.Contract.Coordination.Types.CoordinationResult.Acquired(Limen.Contract.Coordination.Types.LockHandle handle)))
                )
            // Web Locks are not re-entrant: a page asking again for a lock
            // it holds is told it is busy.
            | true, _ -> answer Limen.Contract.Coordination.Types.CoordinationResult.Busy
            | _ ->
                let handle = fresh ()
                this.Held[name] <- (handle, page, lost)
                answer (Limen.Contract.Coordination.Types.CoordinationResult.Acquired(Limen.Contract.Coordination.Types.LockHandle handle))
        | Limen.Contract.Coordination.Types.CoordinationRequest.Release(Limen.Contract.Coordination.Types.LockHandle handle) ->
            match this.Held |> Seq.tryFind (fun pair -> let held, _, _ = pair.Value in held = handle) with
            | Some pair ->
                this.Pass pair.Key
                answer Limen.Contract.Coordination.Types.CoordinationResult.Released
            | None -> answer Limen.Contract.Coordination.Types.CoordinationResult.UnknownLock
        | _ -> answer Limen.Contract.Coordination.Types.CoordinationResult.Unsupported

    /// A lock was let go: it passes to the first page waiting for it.
    member this.Pass(name: string) =
        this.Held.Remove name |> ignore

        match this.Waiting |> Seq.tryFind (fun (wanted, _, _) -> wanted = name) with
        | Some((_, waiter, told) as next) ->
            this.Waiting.Remove next |> ignore
            handles <- handles + 1
            let handle = $"lock-{handles}"
            this.Held[name] <- (handle, waiter, ignore)
            told handle
        | None -> ()

    /// A page closed: its locks pass to the pages waiting for them.
    member this.Release(page: obj) =
        this.Waiting.RemoveAll(fun (_, waiter, _) -> obj.ReferenceEquals(waiter, page)) |> ignore
        this.Lost.RemoveAll(fun (lost, _) -> obj.ReferenceEquals(lost, page)) |> ignore

        let released =
            this.Held |> Seq.filter (fun pair -> let _, holder, _ = pair.Value in obj.ReferenceEquals(holder, page)) |> Seq.map _.Key |> List.ofSeq

        for name in released do
            this.Pass name

    /// The pages whose locks were taken hear LockLost.
    member this.DeliverLockLost() =
        let due = List.ofSeq this.Lost
        this.Lost.Clear()

        for _, told in due do
            told ()

/// One device: a page's bridge and store over the shared in-memory GitHub,
/// in a browser whose localStorage it shares with the browser's other pages.
type private Device(github: InMemoryStore, visibility: RepositoryVisibility, environment: string, person: Session, config: Deployment.DeploymentConfig, browser: Browser) =

    let bridge = Bridge.Bridge()
    /// This page's IndexedDB connection, through Limen's fake store pack.
    let indexedDb = browser.Tab()

    /// Commits that someone else's commit to the repository beats first.
    let mutable beaten = 0
    /// GitHub cannot be reached: nothing is read or written.
    let mutable offline = false
    /// GitHub refuses this account the repository, with this reason.
    let mutable refusing: string option = None
    /// The next commit lands, and the connection drops before its answer.
    let mutable dropAnswer = false
    /// After the dropped answer, GitHub cannot be reached either.
    let mutable disconnect = false
    /// Waits the store asked for (its back-off), not yet over.
    let sleeping = Collections.Generic.List<string>()
    let mutable signingOut = false

    let unreachable () =
        async.Return(Error(StorageFailure.ProviderFailed("AEGIS.NETWORK.UNAVAILABLE", true, "GitHub could not be reached")))

    let provider location =
        let real = github.Provider

        { real with
            ChangeToken = fun ns -> if offline then unreachable () else real.ChangeToken ns
            Read = fun ns path -> if offline then unreachable () else real.Read ns path
            List = fun ns path -> if offline then unreachable () else real.List ns path
            History = fun ns path -> if offline then unreachable () else real.History ns path
            Reconcile = fun ns pending -> if offline then unreachable () else real.Reconcile ns pending
            Commit =
                fun operation ->
                    async {
                        if offline then
                            return! unreachable ()
                        else
                            if beaten > 0 then
                                beaten <- beaten - 1
                                github.WriteExternally(location, $"other-application/{beaten}.txt", Some "another application's file")

                            let! result = real.Commit operation

                            match result with
                            | Ok receipt when dropAnswer ->
                                dropAnswer <- false
                                offline <- disconnect

                                return
                                    Error(
                                        StorageFailure.OutcomeUnknown
                                            { IdempotencyKey = operation.Metadata.IdempotencyKey
                                              Base = receipt.ChangeToken
                                              Candidate = None
                                              Revisions = Map.empty }
                                    )
                            | other -> return other
                    } }

    let backend: Store.Backend =
        { Provider = provider
          Resolve =
            fun _ ->
                async.Return(
                    match offline, refusing with
                    | true, _ -> Error(Store.Unopened.Unreachable "GitHub could not be reached.")
                    | _, Some reason -> Error(Store.Unopened.Refused reason)
                    | false, None -> Ok(snapshot visibility)
                ) }

    let store =
        Store.arca bridge backend (fun () -> start) (fun prefix -> $"{prefix}-{Threading.Interlocked.Increment keys:D8}")

    let ids = ref 0

    /// The device's clock.
    let mutable now = start

    let ctx () =
        { Now = now
          NewId = fun prefix -> $"{prefix}-{environment}-{Threading.Interlocked.Increment ids:D6}-{Guid.NewGuid():N}" }

    new(github, visibility, environment, person, config) = Device(github, visibility, environment, person, config, Browser())
    new(github, visibility, environment) = Device(github, visibility, environment, octocat, configuration environment, Browser())
    new(github, visibility, environment, browser: Browser) = Device(github, visibility, environment, octocat, configuration environment, browser)

    member val Model = Model.initial noOne InMemory start with get, set

    /// Sends a message, carries out the store effects, and feeds back every
    /// message the store produces, until nothing is left.
    member this.Send(msg: Msg) =
        let next, effects = update (ctx ()) msg this.Model
        this.Model <- next
        let heard = Collections.Generic.List<Msg>()

        for effect in effects do
            match effect with
            // The device's timer, in this browser's storage (WI-0055).
            | LoadTimer key ->
                heard.Add(TimerLoaded(key, (match browser.Storage.TryGetValue key with | true, value -> Some value | _ -> None)))
            | SaveTimer(key, Some value) -> browser.Storage[key] <- value
            | SaveTimer(key, None) -> browser.Storage.Remove key |> ignore
            | OpenStore(config, session, dates) -> store.Open config session dates
            | Store request -> store.Commit request
            | ConfirmAdministrator -> store.Confirm()
            | ReadMonths dates -> store.Read dates
            | RebuildIndex -> store.Rebuild()
            | SendUnsent -> store.SendNow()
            | TakeOverQueue -> store.TakeOver()
            | ClaimQueue -> store.Claim()
            | DiscardUnsent -> store.Discard()
            | LeaveDevice(choice, unsent) -> store.SignedOut choice unsent
            | ClearDevice -> store.ClearDevice()
            // Fides signs the person out; the page hears it.
            | SignOut -> signingOut <- true
            | _ -> ()

        for message in heard do
            this.Send message

        this.Settle()

        if signingOut then
            signingOut <- false
            this.Send(IdentityChanged(SignedOutWith(Some "signed_out")))

    /// Answers the browser calls the store made (localStorage, waits) and
    /// feeds back every message, until nothing is left.
    member this.Settle() =
        match bridge.Drain() with
        | Error error -> raise error
        | Ok([], messages) ->
            for message in messages do
                this.Send message
        | Ok(calls, messages) ->
            // Messages produced before these calls are answered come first.
            for message in messages do
                this.Send message

            for id, call in calls do
                let answer =
                    match call, browser.Refusing with
                    | (Bridge.DeviceGet _ | Bridge.DeviceSet _ | Bridge.DeviceRemove _), Some reason -> Some(Bridge.Refused reason)
                    | Bridge.DeviceGet key, None ->
                        Some(Bridge.Read(match browser.Storage.TryGetValue key with | true, value -> Some value | _ -> None))
                    | Bridge.DeviceSet(key, value), None ->
                        browser.Storage[key] <- value
                        Some(Bridge.Read None)
                    | Bridge.DeviceRemove key, None ->
                        browser.Storage.Remove key |> ignore
                        Some(Bridge.Read None)
                    | Bridge.Sleep _, _ ->
                        sleeping.Add id
                        None
                    | Bridge.Coordinate request, _ ->
                        let mutable now = true
                        let mutable immediate = None

                        let answer result =
                            let raw = Bridge.Raw(Limen.Contract.Coordination.Codec.serializeCoordinationResult result)

                            if now then
                                immediate <- Some raw
                            else
                                bridge.Answer id raw |> ignore
                                this.Settle()

                        browser.Coordinate (box this) (fun () -> this.LoseLock()) answer (Limen.Contract.Coordination.Codec.parseCoordinationRequest request |> ok)
                        now <- false
                        immediate
                    | Bridge.StoreOperation _, _ when not browser.IndexedDb -> Some Bridge.Missing
                    | Bridge.StoreOperation request, _ ->
                        let result =
                            indexedDb.Execute(Limen.Contract.Store.Codec.parseStoreRequest request |> ok) |> Async.RunSynchronously

                        Some(Bridge.Raw(Limen.Contract.Store.Codec.serializeStoreResult result))
                    | other, _ -> failwith $"unexpected browser call %A{other}"

                answer |> Option.iter (fun answer -> bridge.Answer id answer |> ignore)

            this.Settle()

    /// GitHub can or cannot be reached.
    member _.Offline
        with get () = offline
        and set value = offline <- value

    /// GitHub refuses this account the repository (access revoked).
    member _.Refusing
        with get () = refusing
        and set value = refusing <- value

    /// The next commit lands, but its answer is lost on the way back.
    member _.DropNextAnswer() = dropAnswer <- true

    /// The next commit lands, then the connection is lost before its answer.
    member _.DisconnectDuringNextCommit() =
        dropAnswer <- true
        disconnect <- true

    /// The page closes (or crashes, or navigates away): the browser lets go
    /// of its locks. Its unsent changes stay where it kept them.
    member this.Close() = browser.Release(box this)

    /// Another context took this page's lock from it.
    member this.LoseLock() =
        store.Lost()
        this.Settle()

    /// The browser clears this page's IndexedDB under it (cleared site data,
    /// eviction); the store pack reports ConnectionLost.
    member this.ClearIndexedDb() =
        indexedDb.Inject Limen.Store.FakeFault.StorageCleared
        store.Reconnect()
        this.Settle()

    /// The person returns to this page (visibility, back-forward cache).
    member this.Return() = this.Send PageReturned

    /// The back-off waits are over: the store tries again.
    member this.Wake() =
        let due = List.ofSeq sleeping
        sleeping.Clear()

        for id in due do
            bridge.Answer id Bridge.Done |> ignore

        this.Settle()

    member _.Sleeping = sleeping.Count

    /// The device's clock.
    member _.Now
        with get () = now
        and set value = now <- value

    member this.Ui(name: string, value: string) = this.Send(Ui(name, None, value, None))

    member this.Ui(name: string, key: string, value: string) = this.Send(Ui(name, Some key, value, None))

    /// The next `count` commits are each beaten by another application's commit.
    member _.Beaten(count: int) = beaten <- count

    /// Opens Chrona in this deployment and signs in as octocat.
    member this.Open() =
        this.Model <- { Model.initial noOne InMemory start with Deployment = Some config }
        this.Send(EnvironmentDescribed "America/New_York")
        this.Model <- { this.Model with Identity = { this.Model.Identity with Mode = SignInRequired false } }
        this.Send(IdentityChanged(SignedInAs person))

let private project (model: Model) = (Reference.selectable Reference.Project model.References).Head.Id
let private activityType (model: Model) = (Reference.selectable Reference.ActivityType model.References).Head.Id

/// Adds reference data and records a manual entry from 09:00 to 10:00 yesterday.
let private record (device: Device) (startTime: string) (endTime: string) (description: string) =
    let model = device.Model

    if (Reference.selectable Reference.Project model.References).IsEmpty then
        device.Ui("newProjectName", "HelixNote")
        device.Ui("addProject", "")
        device.Ui("newActivityTypeName", "Research")
        device.Ui("addActivityType", "")

    device.Ui("manualActivityType", activityType device.Model)
    device.Ui("manualProject", project device.Model)
    device.Ui("manualStartDate", "2026-10-07")
    device.Ui("manualStartTime", startTime)
    device.Ui("manualEndTime", endTime)
    device.Ui("manualDescription", description)
    device.Ui("manualPurpose", "Delivery")
    device.Ui("manualReason", "From notes")
    device.Ui("saveManual", "")

/// A text of the first row of a projected list.
let private rowText (list: string) (key: string) (model: Model) =
    match Project.project model |> List.tryFind (fst >> (=) list) with
    | Some(_, Chrona.Engine.View.Items(row :: _)) ->
        match row |> List.tryFind (fst >> (=) key) with
        | Some(_, Chrona.Engine.View.Text value) -> value
        | other -> failwith $"%A{other}"
    | other -> failwith $"%A{other}"

/// Every object's path in the repository (acme/chrona-data, main).
let private storedPaths (github: InMemoryStore) =
    github.State.Objects |> Map.toList |> List.map (fun (key, _) -> key.Substring(key.IndexOf ':' + 1))

// ---- Opening (2.4 to 2.7) ---------------------------------------------------------

[<Fact>]
let ``a new repository is set up on first sign-in, and the records open empty`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()

    Assert.True(canWork device.Model)
    Assert.Equal(Durable "acme/chrona-data", device.Model.Store.Kind)
    Assert.True(device.Model.Ledger.Activities.IsEmpty)

    Assert.Equal<string list>(
        [ "deployments/chrona/arca-manifest.json"
          "deployments/chrona/datasets/org_acme/arca-manifest.json"
          // The person who set it up founded it: they administer it.
          "deployments/chrona/datasets/org_acme/records/chrona.member/github_3a583231.json"
          "deployments/chrona/datasets/org_acme/records/chrona.organization/org_acme.json" ],
        storedPaths github |> List.sort
    )

    // The person works in the organization the deployment serves.
    Assert.Equal("org_acme", device.Model.Session.OrganizationId)

[<Fact>]
let ``production records are never started in a public repository`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Public, "production")
    device.Open()

    Assert.False(canWork device.Model)
    Assert.Contains("never started in a public repository", device.Model.Store.Failure |> Option.defaultValue "")
    Assert.Empty(storedPaths github)

    // Staging may use a public repository.
    let staging = Device(InMemoryStore(), RepositoryVisibility.Public, "staging")
    staging.Open()
    Assert.True(canWork staging.Model)

// ---- Committing and reading back (21, 22) ------------------------------------------------

[<Fact>]
let ``what is recorded is stored, one commit per command, and reads back on another device`` () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "09:00" "10:00" "Pairing"

    Assert.Empty(first.Model.Store.Pending)
    Assert.Equal(None, first.Model.Store.Problem)
    let paths = storedPaths github
    Assert.Contains(paths, fun path -> path.StartsWith "deployments/chrona/datasets/org_acme/records/chrona.activity/github_3a583231/2026/10/")
    Assert.Equal(2, paths |> List.filter (fun path -> path.Contains "records/chrona.reference/") |> List.length)

    // Another device signs in: the same records open there.
    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()
    let stored = second.Model.Ledger.Activities |> Map.toList |> List.map snd
    Assert.Equal<string list>([ "Pairing" ], stored |> List.map _.Classification.Description)
    Assert.Equal(2, second.Model.References.Items.Count)
    Assert.Empty(second.Model.Store.Integrity)

[<Fact>]
let ``independent changes from two devices both land, and each device is shown the other's`` () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "08:00" "08:30" "Setup"

    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()

    // Both record, independently, at different times: both are kept, and the
    // second device, whose commit was decided again on the moved repository,
    // now shows the first device's entry too.
    record first "09:00" "10:00" "Pairing"
    record second "13:00" "14:00" "Review"
    Assert.Equal(None, second.Model.Store.Problem)
    let descriptions (device: Device) = device.Model.Ledger.Activities |> Map.toList |> List.map (snd >> _.Classification.Description) |> List.sort
    Assert.Equal<string list>([ "Pairing"; "Review"; "Setup" ], descriptions second)

    let third = Device(github, RepositoryVisibility.Private, "production")
    third.Open()
    Assert.Equal<string list>([ "Pairing"; "Review"; "Setup" ], descriptions third)

[<Fact>]
let ``time that overlaps what another device stored first is a conflict decided on what is stored`` () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "08:00" "08:30" "Setup"
    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()

    // The first device records 09:00-10:00; the second, not knowing, 09:30-10:30.
    record first "09:00" "10:00" "Pairing"
    record second "09:30" "10:30" "Overlap"

    // Kept for the person, with what diverged and a stable code; not dropped.
    match second.Model.Store.Conflicts with
    | [ { Divergences = [ Reconcile.OverlapsStored(mine, stored) ] } ] ->
        Assert.Equal("Overlap", mine.Classification.Description)
        Assert.Equal("Pairing", stored.Classification.Description)
    | other -> failwith $"%A{other}"

    Assert.Equal("CHRONA.OVERLAP.OVERLAPS_ACTIVITY", rowText "conflicts" "code" second.Model)

    // It reloaded what is stored; the overlapping entry was never stored.
    let descriptions (device: Device) = device.Model.Ledger.Activities |> Map.toList |> List.map (snd >> _.Classification.Description) |> List.sort
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions second)
    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions reader)

[<Fact>]
let ``an edit made elsewhere first makes this device's stale edit a conflict, never an overwrite`` () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "09:00" "10:00" "Pairing"
    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()
    let id = (first.Model.Ledger.Activities |> Map.toList |> List.head |> fst)

    let amend (device: Device) (text: string) =
        device.Send(LocationMoved $"#/activity/{id}")
        device.Ui("amendDescription", text)
        device.Ui("amendReason", "fix")
        device.Ui("saveAmend", "")

    amend first "From the first device"
    amend second "From the second device"

    match second.Model.Store.Conflicts with
    | [ { Divergences = [ Reconcile.ActivityChanged(mine, Some stored) ] } ] ->
        Assert.Equal("From the second device", mine.Classification.Description)
        Assert.Equal("From the first device", stored.Classification.Description)
    | other -> failwith $"%A{other}"

    Assert.Equal("CHRONA.CONCURRENCY.SEMANTIC_CONFLICT", rowText "conflicts" "code" second.Model)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal("From the first device", reader.Model.Ledger.Activities[id].Classification.Description)
    Assert.Equal("From the first device", second.Model.Ledger.Activities[id].Classification.Description)

[<Fact>]
let ``a commit whose outcome GitHub did not report is reconciled, not sent again`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    record device "09:00" "10:00" "Setup"
    let commits = github.State.History.Length
    github.Arrange InMemoryFault.OutcomeUnknownLanded
    record device "11:00" "12:00" "Landed anyway"

    Assert.Equal(None, device.Model.Store.Problem)
    Assert.Equal(commits + 1, github.State.History.Length)

// ---- What is read is checked (39, 41) ------------------------------------------------------

[<Fact>]
let ``records edited outside Chrona are held and reported when the records open`` () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "09:00" "10:00" "Pairing"

    let id, activity = first.Model.Ledger.Activities |> Map.toList |> List.head
    let path = ActivityRecord.path activity |> ok |> RelativePath.render
    let folder = (configuration "production") |> fun config -> Storage.organizationNamespace config (Storage.binding config |> ok) "org_acme" |> ok

    let edited =
        { activity with
            Classification = { activity.Classification with Description = "Edited on github.com" }
            Revision = 2 }

    github.WriteExternally(folder.Location, $"deployments/chrona/datasets/org_acme/{path}", Some(ActivityRecord.encode edited |> ok))

    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()
    Assert.False(second.Model.Ledger.Activities.ContainsKey id)
    Assert.Equal<string list>([ "CHRONA.INTEGRITY.EXTERNAL_EDIT" ], second.Model.Store.Integrity |> List.map code)

// ---- The GitHub backend's transport ------------------------------------------------------

[<Fact>]
let ``the GitHub backend sends its requests through the bridge with Fides' token, and reads the repository's visibility`` () =
    let bridge = Bridge.Bridge()
    let token = AccessToken.create "gho_STORETESTTOKEN0123456789" |> ok
    let mutable unauthorized = 0

    let backend =
        Store.gitHub
            bridge
            (fun () -> Some(fun () -> async.Return(Ok token)))
            (fun () ->
                async {
                    unauthorized <- unauthorized + 1
                })

    let location = DataLocation.create "acme" "chrona-data" "main" "deployments" |> ok
    let mutable result = None
    bridge.Start(async {
        let! resolved = backend.Resolve location
        result <- Some resolved
        return []
    })

    let answers =
        dict
            [ "https://api.github.com/user", """{"id":583231,"login":"octocat","type":"User"}"""
              "https://api.github.com/repos/acme/chrona-data",
              """{"id":1,"node_id":"R_1","name":"chrona-data","full_name":"acme/chrona-data","owner":{"login":"acme"},"private":false,"visibility":"public","archived":false,"permissions":{"pull":true,"push":true,"admin":false}}"""
              "https://api.github.com/repos/acme/chrona-data/branches/main", """{"name":"main"}"""
              "https://api.github.com/repos/acme/chrona-data/rules/branches/main", "[]" ]

    let mutable sent = []

    let rec pump () =
        match bridge.Drain() with
        | Error error -> raise error
        | Ok([], _) -> ()
        | Ok(calls, _) ->
            for id, call in calls do
                match call with
                | Bridge.Http(method, url, headers, _, _, responseHeaders) ->
                    sent <- sent @ [ method, url, headers ]
                    Assert.Contains("etag", responseHeaders)
                    let body = answers[url]
                    bridge.Answer id (Bridge.Answered(AppProtocol.HttpSucceeded(200, [ "x-ratelimit-remaining", "4999" ], body))) |> ignore
                | other -> failwith $"%A{other}"

            pump ()

    pump ()

    match result with
    | Some(Ok found) ->
        Assert.Equal(RepositoryVisibility.Public, found.Visibility)
        Assert.True(found.CanWrite)
    | other -> failwith $"%A{other}"

    Assert.All(
        sent,
        fun (method, _, headers) ->
            Assert.Equal("GET", method)
            Assert.Contains(("Authorization", "Bearer gho_STORETESTTOKEN0123456789"), headers)
            Assert.Contains(("X-GitHub-Api-Version", "2022-11-28"), headers)
    )

    Assert.Equal(0, unauthorized)

// ---- Rosters, people and organizations (WI-0031) -------------------------------------

let private hubot =
    { ActorId = "github:1001"
      OrganizationId = "local"
      DisplayName = "hubot"
      Kind = SignedIn "github" }

[<Fact>]
let ``only members work in an organization; an administrator adds people, and the roster is shared across devices`` () =
    let github = InMemoryStore()
    let founder = Device(github, RepositoryVisibility.Private, "production")
    founder.Open()

    // Someone else signs in: they are not a member, so nothing opens for them.
    let config = configuration "production"
    let stranger = Device(github, RepositoryVisibility.Private, "production", hubot, config)
    stranger.Open()
    Assert.False(canWork stranger.Model)
    let view = Project.project stranger.Model |> Map.ofList
    Assert.Equal(Chrona.Engine.View.Value(Chrona.Engine.View.Flag true), view["screenNotMember"])
    Assert.True(stranger.Model.Ledger.Activities.IsEmpty)

    // The founder adds them, to keep their own time.
    founder.Ui("memberId", "1001")
    founder.Ui("memberName", "hubot")
    founder.Ui("memberAccess", "ownTime")
    founder.Ui("admitMember", "")
    Assert.Equal(None, founder.Model.Store.Problem)

    let colleague = Device(github, RepositoryVisibility.Private, "production", hubot, config)
    colleague.Open()
    Assert.True(canWork colleague.Model)
    Assert.Equal<Access.Capability Set>(Access.Grants.ownTime, Access.capabilitiesOf colleague.Model.Roster "github:1001")

    // A member who may not manage the organization cannot change the roster.
    colleague.Send(Ui("changeMemberAccess", Some "github:1001", "administrator", None))
    Assert.Equal<Access.Capability Set>(Access.Grants.ownTime, Access.capabilitiesOf colleague.Model.Roster "github:1001")

    // The founder makes them a reviewer; the change is stored and read elsewhere.
    founder.Send(Ui("changeMemberAccess", Some "github:1001", "reviewer", None))
    let again = Device(github, RepositoryVisibility.Private, "production", hubot, config)
    again.Open()
    Assert.Equal<Access.Capability Set>(Access.Grants.reviewer, Access.capabilitiesOf again.Model.Roster "github:1001")

    // The only administrator cannot remove themselves.
    founder.Send(Ui("removeMember", Some "github:583231", "", None))
    Assert.True(founder.Model.Roster.Members.ContainsKey "github:583231")

    // Removing the colleague takes effect on their next opening.
    founder.Send(Ui("removeMember", Some "github:1001", "", None))
    let removed = Device(github, RepositoryVisibility.Private, "production", hubot, config)
    removed.Open()
    Assert.False(canWork removed.Model)

[<Fact>]
let ``reference data changed on one device is what another device opens`` () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "09:00" "10:00" "Pairing"
    let project = (Reference.selectable Reference.Project first.Model.References).Head
    first.Send(Ui("referenceActive", Some $"project:{project.Id}", "", Some false))

    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()
    Assert.Equal(Reference.Archived, second.Model.References.Items[(Reference.Project, project.Id)].Status)
    // Archived, so no longer offered, but still valid on the record that carries it.
    Assert.Empty(Reference.selectable Reference.Project second.Model.References)
    Assert.Equal(project.Id, (second.Model.Ledger.Activities |> Map.toList |> List.head |> snd).Classification.ProjectId)

[<Fact>]
let ``a person works in one of the deployment's organizations at a time, each in its own folder`` () =
    let github = InMemoryStore()

    let config =
        """{"environment":"production","environmentName":"production","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":"deployments"},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://chrona.test/"},"organizations":[{"id":"org_acme","displayName":"Acme Consulting","slug":"acme","timeZone":"America/New_York","administrators":["583231"]},{"id":"org_eu","displayName":"Acme Europe","slug":"acme-eu","timeZone":"Europe/Berlin","administrators":["583231"]}]}"""
        |> Deployment.parse
        |> ok

    let device = Device(github, RepositoryVisibility.Private, "production", octocat, config)
    device.Open()
    Assert.Equal("org_acme", device.Model.Session.OrganizationId)
    record device "09:00" "10:00" "Acme work"

    device.Ui("chooseOrganization", "org_eu")
    Assert.Equal("org_eu", device.Model.Session.OrganizationId)
    Assert.True(canWork device.Model)
    Assert.True(device.Model.Ledger.Activities.IsEmpty)
    record device "11:00" "12:00" "Europe work"

    let paths = storedPaths github
    Assert.Contains(paths, fun path -> path.StartsWith "deployments/chrona/datasets/org_eu/records/chrona.activity/")
    Assert.Contains(paths, fun path -> path.StartsWith "deployments/chrona/datasets/org_acme/records/chrona.activity/")

    device.Ui("chooseOrganization", "org_acme")
    Assert.Equal<string list>([ "Acme work" ], device.Model.Ledger.Activities |> Map.toList |> List.map (snd >> _.Classification.Description))

// ---- Bootstrap administrators (WI-0053) ----------------------------------------------

let private configured (environment: string) (administrators: string) =
    """{"environment":"ENV","environmentName":"ENV","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":"deployments"},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://chrona.test/"},"organizations":[{"id":"org_acme","displayName":"Acme Consulting","slug":"acme","timeZone":"America/New_York","administrators":ADMINS}]}"""
        .Replace("ENV", environment)
        .Replace("ADMINS", administrators)
    |> Deployment.parse
    |> ok

let private viewFlag (key: string) (model: Model) =
    match (Project.project model |> Map.ofList)[key] with
    | Chrona.Engine.View.Value(Chrona.Engine.View.Flag f) -> f
    | other -> failwith $"%A{other}"

let private viewText (key: string) (model: Model) =
    match (Project.project model |> Map.ofList)[key] with
    | Chrona.Engine.View.Value(Chrona.Engine.View.Text t) -> t
    | other -> failwith $"%A{other}"

[<Fact>]
let ``an account the configuration does not list cannot set an organization up, and nothing is written`` () =
    let github = InMemoryStore()
    let stranger = Device(github, RepositoryVisibility.Private, "production", hubot, configured "production" """["583231"]""")
    stranger.Open()
    Assert.False(canWork stranger.Model)
    Assert.Contains("listed administrators can set it up", stranger.Model.Store.Failure |> Option.defaultValue "")
    Assert.DoesNotContain(storedPaths github, fun path -> path.Contains "datasets/org_acme")

[<Fact>]
let ``with no administrators listed, production refuses to set up; a local environment keeps first-opener founding`` () =
    let github = InMemoryStore()
    let production = Device(github, RepositoryVisibility.Private, "production", octocat, configured "production" "[]")
    production.Open()
    Assert.Contains("has no administrators in this deployment's configuration", production.Model.Store.Failure |> Option.defaultValue "")
    Assert.DoesNotContain(storedPaths github, fun path -> path.Contains "datasets/org_acme")

    let local = Device(InMemoryStore(), RepositoryVisibility.Private, "local", octocat, configured "local" "[]")
    local.Open()
    Assert.True(canWork local.Model)
    Assert.True(Access.permits local.Model.Roster "github:583231" Access.ManageOrganizationSettings)

[<Fact>]
let ``an organization from before the rule, administered by an unlisted account, waits for a listed account to confirm`` () =
    let github = InMemoryStore()

    // Set up earlier, locally, by hubot, who is not listed now.
    let earlier = Device(github, RepositoryVisibility.Private, "local", hubot, configured "local" "[]")
    earlier.Open()
    record earlier "09:00" "10:00" "Earlier work"

    let rules = configured "production" """["583231"]"""

    // hubot, the stored administrator, is not granted anything and cannot confirm.
    let unlisted = Device(github, RepositoryVisibility.Private, "production", hubot, rules)
    unlisted.Open()
    Assert.False(canWork unlisted.Model)
    Assert.True(viewFlag "screenConfirmAdministrator" unlisted.Model)
    Assert.False(viewFlag "canConfirmAdministrator" unlisted.Model)
    Assert.True(unlisted.Model.Ledger.Activities.IsEmpty)
    unlisted.Ui("confirmAdministrator", "")
    Assert.False(canWork unlisted.Model)

    // octocat, listed, is not granted silently either: they confirm.
    let listed = Device(github, RepositoryVisibility.Private, "production", octocat, rules)
    listed.Open()
    Assert.False(canWork listed.Model)
    Assert.True(viewFlag "canConfirmAdministrator" listed.Model)
    Assert.DoesNotContain(storedPaths github, fun path -> path.EndsWith "chrona.member/github_3a583231.json")
    listed.Ui("confirmAdministrator", "")
    Assert.True(canWork listed.Model)
    Assert.True(Access.permits listed.Model.Roster "github:583231" Access.ManageOrganizationSettings)

    // Now that a listed administrator exists, hubot works as the roster says.
    let again = Device(github, RepositoryVisibility.Private, "production", hubot, rules)
    again.Open()
    Assert.True(canWork again.Model)
    // Nothing stored was lost while it waited.
    Assert.Equal<string list>([ "Earlier work" ], again.Model.Ledger.Activities |> Map.toList |> List.map (snd >> _.Classification.Description))

[<Fact>]
let ``an organization with no members at all is not founded by whoever opens it`` () =
    let github = InMemoryStore()
    let earlier = Device(github, RepositoryVisibility.Private, "local", hubot, configured "local" "[]")
    earlier.Open()

    // Its roster is removed outside Chrona.
    let folder =
        let config = configured "production" """["583231"]"""
        Storage.organizationNamespace config (Storage.binding config |> ok) "org_acme" |> ok

    github.WriteExternally(folder.Location, "deployments/chrona/datasets/org_acme/records/chrona.member/github_3a1001.json", None)

    let opener = Device(github, RepositoryVisibility.Private, "production", hubot, configured "production" """["583231"]""")
    opener.Open()
    Assert.False(canWork opener.Model)
    Assert.True(viewFlag "screenConfirmAdministrator" opener.Model)
    Assert.DoesNotContain(storedPaths github, fun path -> path.Contains "chrona.member/")

// ---- Resolving what was not stored (WI-0035: 21, 23, 26, 34) ------------------------------

/// Two devices on the same records; the first records "Pairing" 09:00-10:00.
let private twoDevices () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "08:00" "08:30" "Setup"
    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()
    github, first, second

let private descriptions (device: Device) =
    device.Model.Ledger.Activities |> Map.toList |> List.map (snd >> _.Classification.Description) |> List.sort

let private amendFrom (device: Device) (id: string) (text: string) =
    device.Send(LocationMoved $"#/activity/{id}")
    device.Ui("amendDescription", text)
    device.Ui("amendReason", "fix")
    device.Ui("saveAmend", "")

[<Fact>]
let ``a stale edit is redone on the current version only when the person saves it again`` () =
    let github, first, second = twoDevices ()
    let id = first.Model.Ledger.Activities |> Map.toList |> List.head |> fst
    amendFrom first id "From the first device"
    amendFrom second id "From the second device"
    let case = second.Model.Store.Conflicts.Head
    Assert.Equal("Redo yours on the current version", rowText "conflicts" "redoLabel" second.Model)

    // Redoing puts the person's version into the correction form of the
    // current record; nothing is written until they save it.
    let commits = github.State.History.Length
    second.Ui("redoChange", case.Id, id)
    second.Send(LocationMoved $"#/activity/{id}")
    Assert.Empty(second.Model.Store.Conflicts)
    Assert.Equal(commits, github.State.History.Length)
    let detail = second.Model.Detail.Value
    Assert.Equal("From the second device", detail.Amend.Description)
    Assert.Equal(second.Model.Ledger.Activities[id].Revision, detail.Revision)

    second.Ui("amendReason", "Redone after a conflict")
    second.Ui("saveAmend", "")
    Assert.Empty(second.Model.Store.Conflicts)
    Assert.Equal(None, second.Model.Store.Problem)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal("From the second device", reader.Model.Ledger.Activities[id].Classification.Description)

[<Fact>]
let ``keeping what is stored sets the person's change aside, knowingly, and writes nothing`` () =
    let github, first, second = twoDevices ()
    let id = first.Model.Ledger.Activities |> List.ofSeq |> List.head |> _.Key
    amendFrom first id "From the first device"
    amendFrom second id "From the second device"
    let commits = github.State.History.Length

    // Until the person decides, it is an obligation, and the day is not saved.
    Assert.Contains("was not saved", rowText "obligations" "title" second.Model)
    Assert.Equal("Not saved: changed elsewhere", (Project.project second.Model |> Map.ofList)["storeHeadline"] |> function Chrona.Engine.View.Value(Chrona.Engine.View.Text t) -> t | other -> failwith $"%A{other}")

    second.Ui("keepStored", second.Model.Store.Conflicts.Head.Id, "")
    Assert.Empty(second.Model.Store.Conflicts)
    Assert.Equal("Your change was set aside. What is stored stays.", second.Model.Announcement)
    Assert.Equal(commits, github.State.History.Length)
    Assert.Equal("From the first device", second.Model.Ledger.Activities[id].Classification.Description)

[<Fact>]
let ``new time that overlaps what was stored since goes back into the form, to be changed and saved`` () =
    let github, first, second = twoDevices ()
    record first "09:00" "10:00" "Pairing"
    record second "09:30" "10:30" "Overlap"
    let case = second.Model.Store.Conflicts.Head
    let mine = match case.Divergences with [ Reconcile.OverlapsStored(mine, _) ] -> mine | other -> failwith $"%A{other}"

    second.Ui("redoChange", case.Id, mine.ActivityId)
    let draft = second.Model.Manual
    Assert.Equal<string list>([ "2026-10-07"; "09:30"; "2026-10-07"; "10:30"; "Overlap" ], [ draft.StartDate; draft.StartTime; draft.EndDate; draft.EndTime; draft.Classification.Description ])

    second.Ui("manualStartTime", "10:00")
    second.Ui("manualEndTime", "11:00")
    second.Ui("saveManual", "")
    Assert.Empty(second.Model.Store.Conflicts)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Overlap"; "Pairing"; "Setup" ], descriptions reader)

[<Fact>]
let ``a change refused only because the repository kept moving is tried again when the person asks`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    record device "08:00" "08:30" "Setup"

    // Another application commits to the shared repository before each of
    // the three attempts.
    device.Beaten 3
    record device "09:00" "10:00" "Pairing"
    let case = device.Model.Store.Conflicts.Head
    Assert.Equal<Reconcile.Divergence list>([ Reconcile.KeptChanging ], case.Divergences)
    Assert.Equal("CHRONA.CONCURRENCY.KEPT_CHANGING", rowText "conflicts" "code" device.Model)
    Assert.Equal<string list>([ "Setup" ], descriptions device)

    device.Ui("retryChange", case.Id, "")
    Assert.Empty(device.Model.Store.Conflicts)
    Assert.Equal(None, device.Model.Store.Problem)
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions device)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions reader)

// ---- Reviewing records edited outside Chrona (WI-0035: 41) ----------------------------------

/// A record edited on github.com, then a device that opens the records.
let private editedOutside (change: Activity.Activity -> Activity.Activity) =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "09:00" "10:00" "Pairing"
    let id, activity = first.Model.Ledger.Activities |> Map.toList |> List.head
    let path = ActivityRecord.path activity |> ok |> RelativePath.render
    let config = configuration "production"
    let folder = Storage.organizationNamespace config (Storage.binding config |> ok) "org_acme" |> ok

    let write (edited: Activity.Activity) =
        github.WriteExternally(folder.Location, $"deployments/chrona/datasets/org_acme/{path}", Some(ActivityRecord.encode edited |> ok))

    write (change activity)
    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()
    github, second, id, activity, write

let private described (text: string) (activity: Activity.Activity) =
    { activity with
        Classification = { activity.Classification with Description = text }
        Revision = 2 }

[<Fact>]
let ``a record edited outside Chrona is accepted by its person after the usual rules, and is trusted again`` () =
    let github, second, id, _, _ = editedOutside (described "Edited on github.com")
    Assert.Equal<string list>([ id ], second.Model.Store.Held |> List.map _.ActivityId)
    Assert.Contains("Edited on github.com", rowText "outsideEdits" "title" second.Model)
    Assert.Contains("changed outside Chrona", rowText "obligations" "title" second.Model)

    second.Ui("acceptOutsideEdit", id, "")
    Assert.Empty(second.Model.Store.Held)
    Assert.Empty(second.Model.Store.Integrity)
    Assert.Equal(None, second.Model.Store.Problem)
    Assert.Equal(3, second.Model.Ledger.Activities[id].Revision)

    // Its newest commit is Chrona's now: another device trusts it.
    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Empty(reader.Model.Store.Held)
    Assert.Empty(reader.Model.Store.Integrity)
    Assert.Equal("Edited on github.com", reader.Model.Ledger.Activities[id].Classification.Description)
    Assert.Equal(3, reader.Model.Ledger.Activities[id].Revision)

[<Fact>]
let ``an outside edit that claims a state only Chrona gives is never accepted, and stays held`` () =
    let github, second, id, _, _ =
        editedOutside (fun activity -> { described "Approved on github.com" activity with Review = Activity.Approved })

    let commits = github.State.History.Length
    second.Ui("acceptOutsideEdit", id, "")
    Assert.Equal<string list>([ "CHRONA.INTEGRITY.EXTERNAL_STATE_CLAIM" ], second.Model.Problems[OutsideEditForm] |> List.map code)
    Assert.Equal<string list>([ id ], second.Model.Store.Held |> List.map _.ActivityId)
    Assert.Equal(commits, github.State.History.Length)

[<Fact>]
let ``an outside edit changed again after it was reviewed is not accepted on the strength of the earlier review`` () =
    let github, second, id, original, write = editedOutside (described "First outside edit")
    write (described "Second outside edit" original)

    second.Ui("acceptOutsideEdit", id, "")

    match second.Model.Store.Conflicts with
    | [ { Divergences = [ Reconcile.ActivityChanged(mine, None) ] } ] -> Assert.Equal("First outside edit", mine.Classification.Description)
    | other -> failwith $"%A{other}"

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ id ], reader.Model.Store.Held |> List.map _.ActivityId)
    Assert.Equal("Second outside edit", reader.Model.Store.Held.Head.Classification.Description)

// ---- Offline: the queue of unsent changes (WI-0033: 21, 23, 31, 33, 34) ---------------------

let private headline (device: Device) =
    match (Project.project device.Model |> Map.ofList)["storeHeadline"] with
    | Chrona.Engine.View.Value(Chrona.Engine.View.Text text) -> text
    | other -> failwith $"%A{other}"

let private queueKey = "arca.queue.chrona.org_acme"

let private outstanding (text: string) =
    (OfflineQueue.decode text |> ok).Entries
    |> List.filter (fun entry -> match entry.State with EntryState.Synchronized _ | EntryState.Abandoned _ -> false | _ -> true)

/// The queue folder's IndexedDB record, as another tab of the browser reads it.
let private queueRecord (browser: Browser) =
    let tab = browser.Tab()
    let config = configuration "production"
    let folder = Storage.organizationNamespace config (Storage.binding config |> ok) "org_acme" |> ok

    async {
        match! Arca.Limen.IndexedDbQueue.openDatabase tab.Execute with
        | Error _ -> return None
        | Ok opened ->
            match! Arca.Limen.IndexedDbQueue.read tab.Execute (Limen.Store.Connection.Open opened) (Arca.Limen.IndexedDbQueue.recordKey folder) with
            | Ok found -> return found
            | Error failure -> return failwith $"%A{failure}"
    }
    |> Async.RunSynchronously

/// The unsent changes this browser keeps in IndexedDB (WI-0059).
let private inIndexedDb (browser: Browser) =
    match queueRecord browser with
    | Some { Queue = Some text } -> outstanding text
    | _ -> []

/// The unsent changes this browser keeps in localStorage: where IndexedDB
/// cannot be used, or where an older Chrona kept them.
let private inLocalStorage (browser: Browser) =
    match browser.Storage.TryGetValue queueKey with
    | true, text -> outstanding text
    | _ -> []

/// The unsent changes this browser keeps, wherever it keeps them.
let private queued (browser: Browser) = inIndexedDb browser @ inLocalStorage browser

[<Fact>]
let ``time recorded offline waits in this browser, is shown as unsent, and is sent in order on reconnect`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    let commits = github.State.History.Length
    Assert.Empty(queued browser)

    device.Offline <- true
    record device "09:00" "10:00" "Pairing"
    record device "11:00" "12:00" "Review"

    // Nothing reached GitHub; both wait in this browser, and the page says so.
    Assert.Equal(commits, github.State.History.Length)
    Assert.Equal(2, (queued browser).Length)
    Assert.Equal(2, device.Model.Store.Pending.Length)
    Assert.True(device.Model.Store.Sync.Offline)
    Assert.Equal("Offline: 2 changes wait to be sent", headline device)
    Assert.Contains("not reached GitHub", rowText "obligations" "title" device.Model)
    Assert.Equal<string list>([ "Pairing"; "Review"; "Setup" ], descriptions device)

    // Back online, the back-off ends: both are sent, in order, one commit each.
    device.Offline <- false
    device.Wake()
    Assert.Equal(commits + 2, github.State.History.Length)
    Assert.Empty(queued browser)
    Assert.Empty(device.Model.Store.Pending)
    Assert.False(device.Model.Store.Sync.Offline)
    Assert.Equal("All changes saved", headline device)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Pairing"; "Review"; "Setup" ], descriptions reader)

[<Fact>]
let ``unsent changes survive closing the page, and are sent when the records next open`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    device.Offline <- true
    record device "09:00" "10:00" "Pairing"
    let commits = github.State.History.Length

    // The page closes while offline; later it opens again, online.
    device.Close()
    let reopened = Device(github, RepositoryVisibility.Private, "production", browser)
    reopened.Open()
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(queued browser)
    Assert.Empty(reopened.Model.Store.Pending)
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions reopened)

[<Fact>]
let ``a change whose answer was lost with the connection is reconciled before anything is sent again`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    let commits = github.State.History.Length

    // It lands, but the answer is lost and GitHub is then out of reach.
    device.DisconnectDuringNextCommit()
    record device "09:00" "10:00" "Pairing"
    Assert.Equal(commits + 1, github.State.History.Length)

    match device.Model.Store.Problem with
    | Some(OutcomeUnknown _) -> ()
    | other -> failwith $"%A{other}"

    // Reopened online, it is found to have landed: it is not sent again.
    device.Close()
    let reopened = Device(github, RepositoryVisibility.Private, "production", browser)
    reopened.Open()
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(queued browser)
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions reopened)

[<Fact>]
let ``an edit queued offline that another device changed first becomes a conflict on reconnect, neither side dropped`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let laptop = Device(github, RepositoryVisibility.Private, "production", browser)
    laptop.Open()
    record laptop "09:00" "10:00" "Pairing"
    let id = laptop.Model.Ledger.Activities |> Map.toList |> List.head |> fst
    let phone = Device(github, RepositoryVisibility.Private, "production")
    phone.Open()

    laptop.Offline <- true
    amendFrom laptop id "From the laptop, offline"
    amendFrom phone id "From the phone"
    laptop.Offline <- false
    laptop.Wake()

    match laptop.Model.Store.Conflicts with
    | [ { Divergences = [ Reconcile.ActivityChanged(mine, Some stored) ] } ] ->
        Assert.Equal("From the laptop, offline", mine.Classification.Description)
        Assert.Equal("From the phone", stored.Classification.Description)
    | other -> failwith $"%A{other}"

    Assert.Empty(queued browser)
    Assert.Equal("From the phone", laptop.Model.Ledger.Activities[id].Classification.Description)

[<Fact>]
let ``another account's unsent changes in this browser are never sent with this account's credential`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let config = configuration "production"
    let owner = Device(github, RepositoryVisibility.Private, "production", browser)
    owner.Open()
    owner.Ui("memberId", "1001")
    owner.Ui("memberName", "hubot")
    owner.Ui("memberAccess", "ownTime")
    owner.Ui("admitMember", "")
    owner.Offline <- true
    record owner "09:00" "10:00" "Pairing"
    let commits = github.State.History.Length

    // Someone else signs in on this browser: octocat's change stays unsent.
    owner.Close()
    let other = Device(github, RepositoryVisibility.Private, "production", hubot, config, browser)
    other.Open()
    Assert.True(canWork other.Model)
    Assert.Equal(commits, github.State.History.Length)
    Assert.Equal(3, (queued browser).Length)
    Assert.False(other.Model.Store.Sync.KeptInBrowser)
    Assert.Contains("Another account", other.Model.Store.Sync.Note.Value)
    Assert.True(other.Model.Ledger.Activities.IsEmpty)

    // When octocat opens the records again, it is sent.
    other.Close()
    let back = Device(github, RepositoryVisibility.Private, "production", browser)
    back.Open()
    Assert.Equal(commits + 3, github.State.History.Length)
    Assert.Empty(queued browser)

[<Fact>]
let ``without browser storage, changes are still sent, and the page says they would not survive closing it`` () =
    let github = InMemoryStore()
    let browser = Browser(Refusing = Some "unavailable", IndexedDb = false)
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    Assert.False(device.Model.Store.Sync.KeptInBrowser)
    record device "08:00" "08:30" "Setup"
    Assert.Equal(None, device.Model.Store.Problem)
    Assert.Empty(device.Model.Store.Pending)

    device.Offline <- true
    record device "09:00" "10:00" "Pairing"
    Assert.Contains("Keep this page open", (Project.project device.Model |> Map.ofList)["storeDetail"] |> function Chrona.Engine.View.Value(Chrona.Engine.View.Text t) -> t | other -> failwith $"%A{other}")

    device.Offline <- false
    device.Wake()
    Assert.Empty(device.Model.Store.Pending)

[<Fact>]
let ``time for a month not read yet cannot be checked offline, so it is refused, not queued unchecked`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    device.Offline <- true

    device.Ui("manualActivityType", activityType device.Model)
    device.Ui("manualProject", project device.Model)
    device.Ui("manualStartDate", "2026-08-03")
    device.Ui("manualEndDate", "2026-08-03")
    device.Ui("manualStartTime", "09:00")
    device.Ui("manualEndTime", "10:00")
    device.Ui("manualDescription", "August")
    device.Ui("manualPurpose", "Delivery")
    device.Ui("manualReason", "From notes")
    device.Ui("saveManual", "")

    match device.Model.Store.Problem with
    | Some(Failed reason) -> Assert.Contains("have not been read yet", reason)
    | other -> failwith $"%A{other}"

    Assert.Empty(queued browser)

// ---- Derived state (WI-0034: 22, 38, 40) -------------------------------------------------------

let private folderOf (config: Deployment.DeploymentConfig) =
    Storage.organizationNamespace config (Storage.binding config |> ok) "org_acme" |> ok

let private storedIndex (github: InMemoryStore) =
    let config = configuration "production"

    match Derived.read github.Provider (folderOf config) ActivityIndex.definition |> Async.RunSynchronously with
    | Ok(Some(index, _)) -> Some index
    | Ok None -> None
    | Error error -> failwith $"%A{error}"

/// Arca's own rebuild from every stored record: None when the stored index
/// is already exactly what the records make.
let private rebuildWrites (github: InMemoryStore) =
    let config = configuration "production"

    let metadata: OperationMetadata =
        { Summary = "check"
          Actor = { Kind = ActorKind.Human; Id = ActorId.create "github:583231" |> ok }
          ProviderIdentity = None
          ExecutionId = None
          CorrelationId = CorrelationId.create "check-1" |> ok
          IdempotencyKey = IdempotencyKey.create "index-check-1" |> ok }

    match Derived.rebuild github.Provider (folderOf config) metadata ActivityIndex.definition |> Async.RunSynchronously with
    | Ok(index, receipt) -> index, receipt.IsSome
    | Error error -> failwith $"%A{error}"

[<Fact>]
let ``the activity index is kept with every change, exactly as a rebuild from the records makes it`` () =
    let github, first, second = twoDevices ()
    record first "09:00" "10:00" "Pairing"
    let id = first.Model.Ledger.Activities |> Map.toList |> List.find (fun (_, a) -> a.Classification.Description = "Pairing") |> fst
    amendFrom second id "Pairing, corrected"
    second.Send(LocationMoved $"#/activity/{id}")
    second.Ui("voidReason", "Duplicate")
    second.Ui("voidActivity", "")

    let kept = storedIndex github |> Option.get
    Assert.Equal(2, kept.Source.Count)

    // Arca's rebuild from every record finds nothing to change.
    let rebuilt, wrote = rebuildWrites github
    Assert.False(wrote)
    Assert.Equal(kept, rebuilt)

    // What the page shows comes from it: one month, the voided entry not counted.
    match second.Model.Store.History with
    | [ october ] ->
        Assert.Equal((2026, 10), (october.Year, october.Month))
        Assert.Equal(1, october.Activities)
        Assert.Equal(30, october.Minutes)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a month not read yet is listed from the index, and read only when the person goes to it`` () =
    let github = InMemoryStore()
    let first = Device(github, RepositoryVisibility.Private, "production")
    first.Open()
    record first "08:00" "08:30" "Setup"

    // Time recorded in August, months before the current period.
    first.Ui("manualActivityType", activityType first.Model)
    first.Ui("manualProject", project first.Model)
    first.Ui("manualStartDate", "2026-08-03")
    first.Ui("manualEndDate", "2026-08-03")
    first.Ui("manualStartTime", "09:00")
    first.Ui("manualEndTime", "10:00")
    first.Ui("manualDescription", "August work")
    first.Ui("manualPurpose", "Delivery")
    first.Ui("manualReason", "From notes")
    first.Ui("saveManual", "")
    Assert.Equal(None, first.Model.Store.Problem)

    let second = Device(github, RepositoryVisibility.Private, "production")
    second.Open()
    Assert.DoesNotContain((2026, 8), second.Model.Store.Months)
    Assert.DoesNotContain("August work", descriptions second)

    let august = second.Model.Store.History |> List.find (fun total -> total.Month = 8)
    Assert.Equal(60, august.Minutes)
    second.Send(LocationMoved "#/month/2026-10")
    let historyRows =
        match (Project.project second.Model |> Map.ofList)["monthHistory"] with
        | Chrona.Engine.View.Items rows ->
            rows
            |> List.map (fun row ->
                row
                |> List.choose (function
                    | key, Chrona.Engine.View.Text value when key = "id" || key = "label" || key = "total" -> Some value
                    | _ -> None))
        | other -> failwith $"%A{other}"

    Assert.Contains([ "2026-08"; "August 2026"; "1h" ], historyRows)

    // Going to August reads its folder, and only then shows its entries.
    second.Ui("showMonth", "2026-08")
    second.Send(LocationMoved "#/month/2026-08")
    Assert.Contains((2026, 8), second.Model.Store.Months)
    Assert.Contains("August work", descriptions second)
    Assert.Empty(second.Model.Store.Reading)

[<Fact>]
let ``an organization without an index is told so, and an administrator builds it from the records`` () =
    let github, first, _ = twoDevices ()
    let config = configuration "production"
    github.WriteExternally((folderOf config).Location, $"deployments/chrona/datasets/org_acme/{RelativePath.render ActivityIndex.path}", None)

    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    Assert.Contains("has not been built yet", device.Model.Store.Index)
    Assert.Empty(device.Model.Store.History)

    // Changes go on without it; nothing derived is written.
    record device "09:00" "10:00" "Pairing"
    Assert.Equal(None, storedIndex github)

    device.Ui("rebuildIndex", "")
    Assert.Equal("The activity index was built from 2 records.", device.Model.Store.Index)
    Assert.Equal(2, (storedIndex github |> Option.get).Source.Count)
    Assert.Equal(90, device.Model.Store.History |> List.sumBy _.Minutes)
    Assert.False(snd (rebuildWrites github))

[<Fact>]
let ``a record changed outside Chrona shows where the index is out of date, and a rebuild repairs it`` () =
    let github, second, _, _, _ = editedOutside (described "Edited on github.com")
    Assert.Contains("differs from the records in 1 place ", second.Model.Store.Index)

    second.Ui("rebuildIndex", "")
    Assert.Equal("The activity index was rebuilt from 1 record; 2 entries had differed.", second.Model.Store.Index)
    Assert.False(snd (rebuildWrites github))

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.StartsWith("Kept with every change", reader.Model.Store.Index)

[<Fact>]
let ``only an administrator rebuilds the index`` () =
    let github = InMemoryStore()
    let owner = Device(github, RepositoryVisibility.Private, "production")
    owner.Open()
    owner.Ui("memberId", "1001")
    owner.Ui("memberName", "hubot")
    owner.Ui("memberAccess", "ownTime")
    owner.Ui("admitMember", "")
    let member' = Device(github, RepositoryVisibility.Private, "production", hubot, configuration "production")
    member'.Open()
    let commits = github.State.History.Length
    member'.Ui("rebuildIndex", "")
    Assert.Equal<string list>([ "CHRONA.AUTH.UNAUTHORIZED_CAPABILITY" ], member'.Model.Problems[IndexForm] |> List.map code)
    Assert.Equal(commits, github.State.History.Length)

[<Fact>]
let ``every projection is rebuilt from the stored records alone`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    record device "08:00" "09:00" "Setup"
    record device "09:00" "10:00" "Pairing"
    record device "10:00" "11:00" "Review"
    let idOf text = device.Model.Ledger.Activities |> Map.toList |> List.find (fun (_, a) -> a.Classification.Description = text) |> fst
    let pairing, review, setup = idOf "Pairing", idOf "Review", idOf "Setup"
    amendFrom device pairing "Pairing, corrected"
    device.Send(LocationMoved $"#/activity/{review}")
    device.Ui("voidReason", "Duplicate")
    device.Ui("voidActivity", "")
    device.Send(LocationMoved $"#/activity/{setup}")
    device.Ui("splitFirst", "20")
    device.Ui("splitSecond", "40")
    device.Ui("saveSplit", "")
    device.Send(LocationMoved "#/review/2026-10-07")
    device.Ui("attestStatement", "Complete and accurate.")
    device.Ui("attestDay", "")
    Assert.Equal(None, device.Model.Store.Problem)

    // A device that only read what is stored.
    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()

    // Announcements are transitions, not state. The activity screen's
    // history is read from the stored audit trail (WI-0056).
    let ephemeral (key: string) = key = "announcement" || key = "copyStatus"

    for route in
        [ "#/today/2026-10-07"
          "#/review/2026-10-07"
          "#/month/2026-10"
          "#/reports"
          "#/more"
          $"#/activity/{pairing}"
          $"#/activity/{review}"
          $"#/activity/{setup}" ] do
        device.Send(LocationMoved route)
        reader.Send(LocationMoved route)
        let view (d: Device) = Project.project d.Model |> List.filter (fst >> ephemeral >> not)
        let differing = List.zip (view device) (view reader) |> List.filter (fun (a, b) -> a <> b)
        Assert.True(differing.IsEmpty, $"{route}: %A{differing}")

// ---- Signing out with unsent changes (WI-0058) -------------------------------------------------

let private signedOut (device: Device) =
    match device.Model.Identity.Mode with
    | SignInRequired _ -> true
    | _ -> false

/// A device in a shared browser whose next change cannot reach GitHub.
let private unsentOn (browser: Browser) (config: Deployment.DeploymentConfig) =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production", octocat, config, browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    device.Offline <- true
    record device "09:00" "10:00" "Pairing"
    Assert.Equal(1, device.Model.Store.Pending.Length)
    github, device

[<Fact>]
let ``signing out with nothing unsent signs out at once`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    record device "08:00" "08:30" "Setup"
    device.Ui("signOut", "")
    Assert.True(signedOut device)

[<Fact>]
let ``signing out with unsent changes asks first, and keeping them leaves them for this account only`` () =
    let browser = Browser()
    let github, device = unsentOn browser (configuration "production")
    let commits = github.State.History.Length

    device.Ui("signOut", "")
    Assert.False(signedOut device)
    Assert.Equal(Some ChoosingUnsent, device.Model.Identity.SignOut)
    Assert.StartsWith("1 change has not reached GitHub", (Project.project device.Model |> Map.ofList)["signOutSummary"] |> function Chrona.Engine.View.Value(Chrona.Engine.View.Text t) -> t | other -> failwith $"%A{other}")

    device.Ui("signOutKeep", "")
    Assert.True(signedOut device)
    Assert.Equal(1, (queued browser).Length)
    Assert.Equal(commits, github.State.History.Length)

    // The same account, back online, sends them.
    device.Close()
    let back = Device(github, RepositoryVisibility.Private, "production", browser)
    back.Open()
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(queued browser)

[<Fact>]
let ``discarding unsent changes at sign-out needs a confirmation that names how many`` () =
    let browser = Browser()
    let github, device = unsentOn browser (configuration "production")
    let commits = github.State.History.Length

    device.Ui("signOut", "")
    device.Ui("signOutDiscard", "")
    Assert.Equal(Some ConfirmingDiscard, device.Model.Identity.SignOut)
    Assert.Equal(Chrona.Engine.View.Value(Chrona.Engine.View.Text "Discard 1 change? They will not be saved anywhere, and this cannot be undone."), (Project.project device.Model |> Map.ofList)["signOutDiscardText"])

    // Changing one's mind keeps them, signed in.
    device.Ui("signOutCancel", "")
    Assert.Equal(None, device.Model.Identity.SignOut)
    Assert.Equal(1, (queued browser).Length)

    device.Ui("signOut", "")
    device.Ui("signOutDiscard", "")
    device.Ui("signOutDiscardConfirmed", "")
    Assert.True(signedOut device)
    Assert.Empty(queued browser)

    device.Offline <- false
    let back = Device(github, RepositoryVisibility.Private, "production", browser)
    back.Open()
    Assert.Equal(commits, github.State.History.Length)

[<Fact>]
let ``sending unsent changes at sign-out signs out once they are stored, or comes back when they cannot be sent`` () =
    let browser = Browser()
    let github, device = unsentOn browser (configuration "production")
    let commits = github.State.History.Length

    // Still offline: the choice comes back, saying why.
    device.Ui("signOut", "")
    device.Ui("signOutSend", "")
    Assert.False(signedOut device)
    Assert.Equal(Some ChoosingUnsent, device.Model.Identity.SignOut)
    Assert.Equal(Some "GitHub cannot be reached, so they could not be sent.", device.Model.Identity.SignOutNote)

    // Online again: they are sent, and then the person is signed out.
    device.Offline <- false
    device.Ui("signOutSend", "")
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.True(signedOut device)
    Assert.Empty(queued browser)

[<Fact>]
let ``a deployment that keeps nothing on shared devices offers only sending or discarding`` () =
    let config =
        """{"environment":"production","environmentName":"production","sharedDevicePolicy":"discardOnSignOut","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":"deployments"},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://chrona.test/"},"organizations":[{"id":"org_acme","displayName":"Acme Consulting","slug":"acme","timeZone":"America/New_York","administrators":["583231"]}]}"""
        |> Deployment.parse
        |> ok

    Assert.Equal(Deployment.DiscardOnSignOut, config.SharedDevice)
    let browser = Browser()
    let _, device = unsentOn browser config
    device.Ui("signOut", "")
    Assert.Equal(Chrona.Engine.View.Value(Chrona.Engine.View.Flag true), (Project.project device.Model |> Map.ofList)["signOutCannotKeep"])

    // Keeping is not offered, and asking for it does nothing.
    device.Ui("signOutKeep", "")
    Assert.False(signedOut device)
    Assert.Equal(1, (queued browser).Length)

[<Fact>]
let ``the shared-device policy is ask unless the deployment says otherwise, and nothing else is accepted`` () =
    Assert.Equal(Deployment.Ask, (configuration "production").SharedDevice)

    let refused =
        """{"environment":"local","environmentName":"local","sharedDevicePolicy":"forget"}""" |> Deployment.parse

    Assert.True(Result.isError refused)

// ---- The stored audit trail (WI-0056: 25, 40) ---------------------------------------------------

let private auditPaths (github: InMemoryStore) =
    storedPaths github |> List.filter (fun path -> path.Contains "/records/chrona.audit/")

[<Fact>]
let ``who changed what is stored with the records, once, beside the activity it concerns`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    record device "09:00" "10:00" "Pairing"
    let id = device.Model.Ledger.Activities |> Map.toList |> List.head |> fst
    amendFrom device id "Pairing, corrected"

    // One immutable record per command, in the person's month folder.
    let paths = auditPaths github
    Assert.Equal(2, paths.Length)
    Assert.All(paths, fun path -> Assert.Contains("/records/chrona.audit/github_3a583231/2026/10/", path))

    // A device that only read the records shows the same history.
    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    reader.Send(LocationMoved $"#/activity/{id}")

    let actions =
        match (Project.project reader.Model |> Map.ofList)["detailHistory"] with
        | Chrona.Engine.View.Items rows ->
            rows |> List.map (List.pick (function "action", Chrona.Engine.View.Text action -> Some action | _ -> None))
        | other -> failwith $"%A{other}"

    Assert.Equal<string list>([ "Amended"; "Recorded" ], actions)
    Assert.Equal(Chrona.Engine.View.Value(Chrona.Engine.View.Text ""), (Project.project reader.Model |> Map.ofList)["detailHistoryNote"])
    Assert.Equal("github:583231", reader.Model.Ledger.Audit.Head.Performer)

[<Fact>]
let ``a change refused as a conflict stores no audit entry`` () =
    let github, first, second = twoDevices ()
    record first "09:00" "10:00" "Pairing"
    let before = auditPaths github
    record second "09:30" "10:30" "Overlap"
    Assert.Single(second.Model.Store.Conflicts) |> ignore
    Assert.Equal<string list>(before, auditPaths github)

[<Fact>]
let ``accepting an outside edit is audited, and revisions made before the trail was stored say so`` () =
    let github, second, id, _, _ = editedOutside (described "Edited on github.com")
    second.Ui("acceptOutsideEdit", id, "")

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    reader.Send(LocationMoved $"#/activity/{id}")
    Assert.Contains(reader.Model.Ledger.Audit, fun entry -> entry.Command = "accept-outside-edit" && entry.ResultingRevisions = [ id, 3 ])

    // Revision 2 was written on github.com: no entry tells of it.
    Assert.Contains("before Chrona kept its history", (Project.project reader.Model |> Map.ofList)["detailHistoryNote"] |> function Chrona.Engine.View.Value(Chrona.Engine.View.Text t) -> t | other -> failwith $"%A{other}")

// ---- The device's timer (WI-0055: 10.1, 10.2, 10.5, 23) -------------------------------------------

let private startTimer (device: Device) =
    device.Ui("timerActivityType", activityType device.Model)
    device.Ui("timerProject", project device.Model)
    device.Ui("timerDescription", "Deep work")
    device.Ui("startTimer", "")
    Assert.True(match device.Model.Timer with Timer.Running _ -> true | _ -> false)

let private timerKeyOf = TimerRecord.key "org_acme" "github:583231"

[<Fact>]
let ``a running timer survives a refresh, and is recovered with its elapsed time from its timestamps`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    startTimer device
    Assert.True(browser.Storage.ContainsKey timerKeyOf)

    // The page is reloaded 25 minutes later.
    let reloaded = Device(github, RepositoryVisibility.Private, "production", browser)
    reloaded.Now <- start.AddMinutes 25.0
    reloaded.Open()

    match reloaded.Model.Timer with
    | Timer.Running timer -> Assert.Equal(25, Timer.elapsedMinutes reloaded.Now timer)
    | other -> failwith $"%A{other}"

    Assert.Equal("Your records are open. Your timer was recovered and is still running: 25m so far.", reloaded.Model.Announcement)

[<Fact>]
let ``a stopped timer's held time survives a refresh until it is completed, then the device lets it go`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    startTimer device
    device.Now <- start.AddMinutes 40.0
    device.Ui("stopTimer", "")

    let reloaded = Device(github, RepositoryVisibility.Private, "production", browser)
    reloaded.Now <- start.AddMinutes 45.0
    reloaded.Open()
    Assert.Equal(40, reloaded.Model.Stopped.Value.TotalMinutes)

    reloaded.Ui("completePurpose", "Delivery")
    reloaded.Ui("saveCompletion", "")
    Assert.True(reloaded.Model.Stopped.IsNone)
    Assert.False(browser.Storage.ContainsKey timerKeyOf)
    Assert.Contains("Deep work", descriptions reloaded)

[<Fact>]
let ``a kept timer that cannot be read is left as it is, and another account never sees this one's`` () =
    let github = InMemoryStore()
    let browser = Browser()
    browser.Storage[timerKeyOf] <- "{not a timer"
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    Assert.Equal("Your records are open. A timer kept in this browser could not be read. It was left as it is.", device.Model.Announcement)
    Assert.Equal("{not a timer", browser.Storage[timerKeyOf])

    // octocat's timer, then hubot on the same browser.
    browser.Storage.Remove timerKeyOf |> ignore
    record device "08:00" "08:30" "Setup"
    device.Ui("memberId", "1001")
    device.Ui("memberName", "hubot")
    device.Ui("memberAccess", "ownTime")
    device.Ui("admitMember", "")
    startTimer device
    let other = Device(github, RepositoryVisibility.Private, "production", hubot, configuration "production", browser)
    other.Open()
    Assert.Equal(Timer.Idle, other.Model.Timer)
    Assert.True(browser.Storage.ContainsKey timerKeyOf)

[<Fact>]
let ``starting offline with nothing to open from, the timer keeps working and the unsent changes are counted`` () =
    let github = InMemoryStore()
    // No IndexedDB: no read cache to open the records from (WI-0057); the
    // queue is kept in localStorage.
    let browser = Browser(IndexedDb = false)
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    startTimer device
    device.Offline <- true
    record device "09:00" "10:00" "Pairing"

    // The page starts again while GitHub cannot be reached.
    device.Close()
    let offline = Device(github, RepositoryVisibility.Private, "production", browser)
    offline.Offline <- true
    offline.Now <- start.AddMinutes 30.0
    offline.Open()
    Assert.True(offline.Model.Store.Failure.IsSome)
    let view () = Project.project offline.Model |> Map.ofList
    Assert.Equal(Chrona.Engine.View.Value(Chrona.Engine.View.Flag true), (view ())["offlineTimer"])
    Assert.Equal(Chrona.Engine.View.Value(Chrona.Engine.View.Text "1 change waits in this browser and is sent when your records open."), (view ())["offlineWaiting"])

    // Pausing and stopping work without the records; the time is held, and kept.
    offline.Ui("pauseTimer", "")
    Assert.True(match offline.Model.Timer with Timer.Paused _ -> true | _ -> false)
    offline.Ui("stopTimer", "")
    Assert.Equal(30, offline.Model.Stopped.Value.TotalMinutes)
    Assert.Contains("\"stopped\"", browser.Storage[timerKeyOf])

[<Fact>]
let ``a running timer that overlaps time recorded on another device is an obligation with a stable code`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let laptop = Device(github, RepositoryVisibility.Private, "production", browser)
    laptop.Open()
    record laptop "08:00" "08:30" "Setup"
    startTimer laptop

    // Meanwhile, the phone records 14:15 to 14:35 today.
    let phone = Device(github, RepositoryVisibility.Private, "production")
    phone.Now <- start.AddMinutes 40.0
    phone.Open()
    phone.Ui("manualActivityType", activityType phone.Model)
    phone.Ui("manualProject", project phone.Model)
    phone.Ui("manualStartDate", "2026-10-08")
    phone.Ui("manualEndDate", "2026-10-08")
    phone.Ui("manualStartTime", "14:15")
    phone.Ui("manualEndTime", "14:35")
    phone.Ui("manualDescription", "Call")
    phone.Ui("manualPurpose", "Delivery")
    phone.Ui("saveManual", "")
    Assert.Equal(None, phone.Model.Store.Problem)

    let reopened = Device(github, RepositoryVisibility.Private, "production", browser)
    reopened.Now <- start.AddMinutes 45.0
    reopened.Open()
    let title = rowText "obligations" "title" reopened.Model
    Assert.Equal("Your timer overlaps \"Call\", recorded since it started", title)
    Assert.Contains("CHRONA.TIMER.CONCURRENT_CONFLICT", rowText "obligations" "detail" reopened.Model)

[<Fact>]
let ``signing out with a timer on the device asks first, like unsent changes`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    startTimer device

    device.Ui("signOut", "")
    Assert.Equal(Some ChoosingUnsent, device.Model.Identity.SignOut)
    device.Ui("signOutDiscard", "")
    Assert.Equal(Chrona.Engine.View.Value(Chrona.Engine.View.Text "Discard your timer? They will not be saved anywhere, and this cannot be undone."), (Project.project device.Model |> Map.ofList)["signOutDiscardText"])
    device.Ui("signOutDiscardConfirmed", "")
    Assert.True(signedOut device)
    Assert.False(browser.Storage.ContainsKey timerKeyOf)

/// Without Web Locks no tab can be told apart from another, so no durable
/// store is ever written by several tabs at once (LCP-059): each tab keeps
/// its own changes in its page, says so, and sends them itself.
[<Fact>]
let ``without Web Locks, two tabs offline in one browser each keep their own changes in the page, and nothing is overwritten or lost`` () =
    let github = InMemoryStore()
    let browser = Browser(WebLocks = false)
    let first = Device(github, RepositoryVisibility.Private, "production", browser)
    let second = Device(github, RepositoryVisibility.Private, "production", browser)
    first.Open()
    record first "08:00" "08:30" "Setup"
    second.Open()
    let commits = github.State.History.Length

    first.Offline <- true
    second.Offline <- true
    record first "09:00" "10:00" "Pairing"
    record second "11:00" "12:00" "Review"

    Assert.Empty(queued browser)

    for tab in [ first; second ] do
        Assert.False(tab.Model.Store.Sync.KeptInBrowser)
        Assert.Equal(HeldHere, tab.Model.Store.Sync.Holder)
        Assert.Equal("In this page only", viewText "queueMode" tab.Model)
        Assert.True(viewFlag "offlineInPage" tab.Model)
        Assert.Equal(1, tab.Model.Store.Pending.Length)

    first.Offline <- false
    second.Offline <- false
    first.Wake()
    second.Wake()
    Assert.Equal(commits + 2, github.State.History.Length)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Pairing"; "Review"; "Setup" ], descriptions reader)

// ---- One tab holds this browser's unsent changes (WI-0067, WI-0059) ---------

[<Fact>]
let ``two tabs in one browser: only the tab holding the unsent changes keeps them in IndexedDB; the other says so and sends its own from the page`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let first = Device(github, RepositoryVisibility.Private, "production", browser)
    let second = Device(github, RepositoryVisibility.Private, "production", browser)
    first.Open()
    record first "08:00" "08:30" "Setup"
    second.Open()
    let commits = github.State.History.Length

    Assert.Equal(HeldHere, first.Model.Store.Sync.Holder)
    Assert.Equal(InIndexedDb, first.Model.Store.Sync.Durability)
    Assert.Equal(HeldElsewhere false, second.Model.Store.Sync.Holder)
    Assert.False(viewFlag "queueElsewhere" first.Model)
    Assert.True(viewFlag "queueElsewhere" second.Model)
    Assert.Equal("Held by another Chrona tab", viewText "queueMode" second.Model)
    Assert.Equal("Kept in this browser (IndexedDB) by this tab", viewText "queueMode" first.Model)

    // Online, the second tab still writes directly (OQ-LIMEN-IDB-001).
    record second "07:00" "07:30" "Standup"
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(queued browser)

    // Offline, only the holder keeps its change in the browser; the other
    // tab keeps its own in the page, and says where they are.
    first.Offline <- true
    second.Offline <- true
    record first "09:00" "10:00" "Pairing"
    record second "11:00" "12:00" "Review"
    Assert.Equal(1, (inIndexedDb browser).Length)
    Assert.Empty(inLocalStorage browser)
    Assert.Equal(1, second.Model.Store.Pending.Length)
    Assert.StartsWith("Another tab is holding your unsent changes", viewText "storeDetail" second.Model)

    // Back online, each change is sent once, by the tab that holds it.
    first.Offline <- false
    second.Offline <- false
    first.Wake()
    second.Wake()
    Assert.Equal(commits + 3, github.State.History.Length)
    Assert.Empty(queued browser)
    Assert.Empty(first.Model.Store.Pending)
    Assert.Empty(second.Model.Store.Pending)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Pairing"; "Review"; "Setup"; "Standup" ], descriptions reader)

[<Fact>]
let ``"use this tab instead" takes the unsent changes over at once; the other tab's next save is fenced, and each change is sent once`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let first = Device(github, RepositoryVisibility.Private, "production", browser)
    let second = Device(github, RepositoryVisibility.Private, "production", browser)
    first.Open()
    record first "08:00" "08:30" "Setup"
    second.Open()
    let commits = github.State.History.Length

    first.Offline <- true
    second.Offline <- true
    record first "09:00" "10:00" "Pairing"
    record second "11:00" "12:00" "Review"

    // The second tab takes them over: it reads the first tab's change back,
    // keeps its own after it, and shows both as unsent.
    second.Ui("takeOverQueue", "")
    Assert.Equal(HeldHere, second.Model.Store.Sync.Holder)
    Assert.True(second.Model.Store.Sync.KeptInBrowser)
    Assert.Equal(2, (inIndexedDb browser).Length)
    Assert.Equal(2, second.Model.Store.Pending.Length)
    Assert.Equal("This tab now holds your unsent changes and sends them.", second.Model.Announcement)
    Assert.Equal<string list>([ "Pairing"; "Review"; "Setup" ], descriptions second)

    // Before the first tab hears that it lost them, it saves once more: the
    // save is fenced and writes nothing. What it kept is the second tab's
    // now; the change it just made stays in its page.
    record first "13:00" "14:00" "Planning"
    Assert.Equal(HeldElsewhere false, first.Model.Store.Sync.Holder)
    Assert.Equal(2, (inIndexedDb browser).Length)
    Assert.Equal<string list>([ "Planning" ], first.Model.Store.Pending |> List.collect (fun request -> request.Activities |> List.map _.Classification.Description))
    Assert.Equal("Your unsent changes moved to the other Chrona tab, which now sends them.", first.Model.Announcement)
    browser.DeliverLockLost()
    Assert.Equal(HeldElsewhere false, first.Model.Store.Sync.Holder)

    // Back online, every change is sent once.
    first.Offline <- false
    second.Offline <- false
    second.Wake()
    first.Wake()
    Assert.Equal(commits + 3, github.State.History.Length)
    Assert.Empty(queued browser)
    Assert.Empty(first.Model.Store.Pending)
    Assert.Empty(second.Model.Store.Pending)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Pairing"; "Planning"; "Review"; "Setup" ], descriptions reader)

[<Fact>]
let ``a tab that hears its unsent changes were taken over hands them to the other tab, and sends none of them itself`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let first = Device(github, RepositoryVisibility.Private, "production", browser)
    let second = Device(github, RepositoryVisibility.Private, "production", browser)
    first.Open()
    record first "08:00" "08:30" "Setup"
    second.Open()
    let commits = github.State.History.Length

    first.Offline <- true
    second.Offline <- true
    record first "09:00" "10:00" "Pairing"
    second.Ui("takeOverQueue", "")
    browser.DeliverLockLost()
    Assert.Equal(HeldElsewhere false, first.Model.Store.Sync.Holder)
    Assert.Empty(first.Model.Store.Pending)
    Assert.Equal(1, second.Model.Store.Pending.Length)

    first.Offline <- false
    second.Offline <- false
    first.Wake()
    Assert.Equal(commits, github.State.History.Length)
    second.Wake()
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(queued browser)

[<Fact>]
let ``when the holding tab closes, the other tab holds the unsent changes once the person returns to it, and sends each once`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let first = Device(github, RepositoryVisibility.Private, "production", browser)
    let second = Device(github, RepositoryVisibility.Private, "production", browser)
    first.Open()
    record first "08:00" "08:30" "Setup"
    second.Open()
    let commits = github.State.History.Length

    first.Offline <- true
    second.Offline <- true
    record first "09:00" "10:00" "Pairing"
    record second "11:00" "12:00" "Review"

    first.Close()
    second.Return()
    Assert.Equal(HeldHere, second.Model.Store.Sync.Holder)
    Assert.True(second.Model.Store.Sync.KeptInBrowser)
    Assert.Equal(2, (inIndexedDb browser).Length)
    Assert.Equal(2, second.Model.Store.Pending.Length)

    second.Offline <- false
    second.Wake()
    Assert.Equal(commits + 2, github.State.History.Length)
    Assert.Empty(queued browser)
    Assert.Empty(second.Model.Store.Pending)

[<Fact>]
let ``a change the holding tab was sending when the connection dropped is reconciled by the tab that takes over, not sent again`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let first = Device(github, RepositoryVisibility.Private, "production", browser)
    let second = Device(github, RepositoryVisibility.Private, "production", browser)
    first.Open()
    record first "08:00" "08:30" "Setup"
    second.Open()
    let commits = github.State.History.Length

    // It lands, but its answer is lost and GitHub is then out of reach.
    first.DisconnectDuringNextCommit()
    record first "09:00" "10:00" "Pairing"
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Equal(1, (inIndexedDb browser).Length)

    second.Ui("takeOverQueue", "")
    browser.DeliverLockLost()
    second.Wake()
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(queued browser)
    Assert.Empty(second.Model.Store.Pending)
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions second)

[<Fact>]
let ``a tab whose lock is taken stops keeping unsent changes in the browser, and says another tab holds them`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    device.LoseLock()
    Assert.Equal(HeldElsewhere false, device.Model.Store.Sync.Holder)
    Assert.False(device.Model.Store.Sync.KeptInBrowser)

    device.Offline <- true
    record device "09:00" "10:00" "Pairing"
    Assert.Empty(queued browser)
    Assert.Equal(1, device.Model.Store.Pending.Length)

[<Fact>]
let ``a tab that opens the records again goes on holding the unsent changes it holds`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    // Signed in again (another tab's sign-in, or a new session): the records
    // open again in the same page, which already holds the lock.
    device.Open()
    Assert.Equal(HeldHere, device.Model.Store.Sync.Holder)
    Assert.True(device.Model.Store.Sync.KeptInBrowser)
    device.Offline <- true
    record device "09:00" "10:00" "Pairing"
    Assert.Equal(1, (queued browser).Length)


// ---- Moving the queue into IndexedDB (WI-0059, Limen LCP-065) ---------------

/// The organization's project and activity type, made online, so that a
/// change recorded offline is one entry.
let private prepare (device: Device) =
    device.Ui("newProjectName", "HelixNote")
    device.Ui("addProject", "")
    device.Ui("newActivityTypeName", "Research")
    device.Ui("addActivityType", "")

/// A browser where an older Chrona kept unsent changes in localStorage: one
/// offline change, made by a page without the store pack, then closed.
let private olderChrona (github: InMemoryStore) (browser: Browser) (description: string) =
    browser.IndexedDb <- false
    let older = Device(github, RepositoryVisibility.Private, "production", browser)
    older.Open()
    if (Reference.selectable Reference.Project older.Model.References).IsEmpty then prepare older
    Assert.Equal(InLocalStorage, older.Model.Store.Sync.Durability)
    Assert.Equal("Kept in this browser (localStorage) by this tab", viewText "queueMode" older.Model)
    older.Offline <- true
    record older "09:00" "10:00" description
    older.Close()
    browser.IndexedDb <- true
    Assert.Equal(1, (inLocalStorage browser).Length)
    older

[<Fact>]
let ``unsent changes an older Chrona kept in localStorage move to IndexedDB on first load, unchanged, and are sent once`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let older = olderChrona github browser "Pairing"
    let commits = github.State.History.Length
    // The localStorage queue as it was, byte for byte.
    let before = browser.Storage[queueKey]

    // Opened while GitHub cannot be reached: the records do not open, but
    // the queue is read, moved, counted and the move is said.
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Offline <- true
    device.Open()
    Assert.Equal(1, device.Model.Store.Waiting)
    Assert.Equal(Some "1 unsent change kept in this browser's older storage moved to IndexedDB, unchanged.", device.Model.Store.Sync.Notice)
    Assert.True(viewFlag "hasQueueNotice" device.Model)
    Assert.Empty(inLocalStorage browser)
    Assert.Equal<string list>((outstanding before) |> List.map _.Operation.IdempotencyKey, (inIndexedDb browser) |> List.map _.Operation.IdempotencyKey)
    ignore older

    // Online, the records open and the moved change is sent once.
    device.Offline <- false
    device.Open()
    Assert.True(device.Model.Store.Sync.KeptInBrowser)
    Assert.Equal(InIndexedDb, device.Model.Store.Sync.Durability)
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(queued browser)
    Assert.Empty(device.Model.Store.Pending)

[<Fact>]
let ``a move to IndexedDB interrupted after the copy, before localStorage was cleared, finishes on the next load without sending anything twice`` () =
    let github = InMemoryStore()
    let browser = Browser()
    olderChrona github browser "Pairing" |> ignore
    let commits = github.State.History.Length
    let legacy = browser.Storage[queueKey]

    // The copy is made and verified; the page then closes before the old
    // queue is removed.
    let interrupted = Device(github, RepositoryVisibility.Private, "production", browser)
    interrupted.Offline <- true
    interrupted.Open()
    interrupted.Close()
    browser.Storage[queueKey] <- legacy
    Assert.Equal(1, (inIndexedDb browser).Length)
    Assert.Equal(1, (inLocalStorage browser).Length)

    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    Assert.Empty(inLocalStorage browser)
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(queued browser)
    Assert.Empty(device.Model.Store.Pending)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Pairing" ], descriptions reader |> List.filter ((=) "Pairing"))

[<Fact>]
let ``an older localStorage queue waits, said so, while IndexedDB holds unsent changes, and moves once they are sent; none is merged or dropped`` () =
    let github = InMemoryStore()
    let browser = Browser()
    // IndexedDB already holds an unsent change.
    let current = Device(github, RepositoryVisibility.Private, "production", browser)
    current.Open()
    prepare current
    current.Offline <- true
    record current "08:00" "08:30" "Setup"
    current.Close()
    Assert.Equal(1, (inIndexedDb browser).Length)
    // An older Chrona then left one in localStorage.
    olderChrona github browser "Pairing" |> ignore
    let commits = github.State.History.Length

    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Offline <- true
    device.Open()
    Assert.Equal(Some "1 earlier unsent change waits in this browser's older storage. They move here once the changes now kept are sent; none is merged or dropped.", device.Model.Store.Sync.Notice)
    Assert.Equal(1, (inLocalStorage browser).Length)
    Assert.Equal(1, (inIndexedDb browser).Length)

    // Sent; the older queue moves on the next load, and is sent too.
    device.Offline <- false
    device.Open()
    Assert.Equal(commits + 1, github.State.History.Length)
    device.Close()

    let next = Device(github, RepositoryVisibility.Private, "production", browser)
    next.Open()
    Assert.Equal(commits + 2, github.State.History.Length)
    Assert.Empty(queued browser)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions reader)

[<Fact>]
let ``an older localStorage queue that cannot be read is left exactly as it is, and the person is told`` () =
    let github = InMemoryStore()
    let browser = Browser()
    browser.Storage[queueKey] <- "{not a queue"

    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    Assert.True(canWork device.Model)
    Assert.Equal(Some "Unsent changes kept in this browser's older storage cannot be read. They were left exactly as they are.", device.Model.Store.Sync.Notice)
    Assert.Equal("{not a queue", browser.Storage[queueKey])

[<Fact>]
let ``when the browser clears IndexedDB under the page, the queue is opened again and what this tab holds is kept there again`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    prepare device
    device.Offline <- true
    record device "09:00" "10:00" "Pairing"
    Assert.Equal(1, (inIndexedDb browser).Length)

    device.ClearIndexedDb()
    Assert.Equal(HeldHere, device.Model.Store.Sync.Holder)
    Assert.True(device.Model.Store.Sync.KeptInBrowser)
    Assert.Equal(1, (inIndexedDb browser).Length)
    Assert.Equal(1, device.Model.Store.Pending.Length)

// ---- Opening offline from the read cache (WI-0057, Limen LCP-082..087) -----

[<Fact>]
let ``starting while GitHub cannot be reached, the records open from this browser's read cache, as of when they were read`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    device.Close()

    let offline = Device(github, RepositoryVisibility.Private, "production", browser)
    offline.Offline <- true
    offline.Open()
    Assert.Equal(None, offline.Model.Store.Failure)
    Assert.Equal(Some start, offline.Model.Store.Cached)
    Assert.Equal<string list>([ "Setup" ], descriptions offline)
    Assert.StartsWith("Offline: your records as of", headline offline)

/// A browser that read octocat's records from GitHub once (one activity),
/// then closed.
let private readOnce (github: InMemoryStore) (browser: Browser) =
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    let commits = github.State.History.Length
    device.Close()
    commits

[<Fact>]
let ``a change made from the read cache is decided again on what GitHub holds before it is sent, and sent once`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let commits = readOnce github browser

    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Offline <- true
    device.Open()
    Assert.True(device.Model.Store.Cached.IsSome)
    record device "09:00" "10:00" "Pairing"
    Assert.Equal(1, device.Model.Store.Pending.Length)
    Assert.Equal(commits, github.State.History.Length)

    // Kept in this browser, conditioned on nothing a cached value gave.
    match inIndexedDb browser with
    | [ entry ] -> Assert.Equal(None, entry.Operation.ExpectedChangeToken)
    | other -> failwith $"%A{other}"

    // GitHub again: the records are read from it, the change is decided on
    // them, and sent once.
    device.Offline <- false
    device.Wake()
    Assert.Equal(None, device.Model.Store.Cached)
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(device.Model.Store.Pending)
    Assert.Empty(queued browser)
    Assert.Equal("All changes saved", headline device)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions reader)

[<Fact>]
let ``a change made from the read cache to a record someone changed since is a conflict on reconnect, neither side dropped`` () =
    let github = InMemoryStore()
    let browser = Browser()
    readOnce github browser |> ignore

    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Offline <- true
    device.Open()
    let id = device.Model.Ledger.Activities |> Map.toList |> List.head |> fst

    // Meanwhile, another device changes it on GitHub.
    let phone = Device(github, RepositoryVisibility.Private, "production")
    phone.Open()
    amendFrom phone id "From the phone"

    amendFrom device id "From the cache, offline"
    device.Offline <- false
    device.Wake()

    match device.Model.Store.Conflicts with
    | [ { Divergences = [ Reconcile.ActivityChanged(mine, Some stored) ] } ] ->
        Assert.Equal("From the cache, offline", mine.Classification.Description)
        Assert.Equal("From the phone", stored.Classification.Description)
    | other -> failwith $"%A{other}"

    Assert.Empty(queued browser)
    Assert.Equal("From the phone", device.Model.Ledger.Activities[id].Classification.Description)

[<Fact>]
let ``from the read cache, a month it does not hold is refused, not queued unchecked`` () =
    let github = InMemoryStore()
    let browser = Browser()
    readOnce github browser |> ignore

    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Offline <- true
    device.Open()
    device.Ui("manualActivityType", activityType device.Model)
    device.Ui("manualProject", project device.Model)
    device.Ui("manualStartDate", "2026-08-03")
    device.Ui("manualEndDate", "2026-08-03")
    device.Ui("manualStartTime", "09:00")
    device.Ui("manualEndTime", "10:00")
    device.Ui("manualDescription", "August")
    device.Ui("manualPurpose", "Delivery")
    device.Ui("manualReason", "From notes")
    device.Ui("saveManual", "")

    match device.Model.Store.Problem with
    | Some(Failed reason) -> Assert.Contains("have not been read yet", reason)
    | other -> failwith $"%A{other}"

    Assert.Empty(queued browser)

[<Fact>]
let ``records held for review stay held when they open from the read cache`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let first = Device(github, RepositoryVisibility.Private, "production", browser)
    first.Open()
    record first "09:00" "10:00" "Pairing"
    let id, activity = first.Model.Ledger.Activities |> Map.toList |> List.head
    let path = ActivityRecord.path activity |> ok |> RelativePath.render
    let config = configuration "production"
    let folder = Storage.organizationNamespace config (Storage.binding config |> ok) "org_acme" |> ok
    let edited = { activity with Classification = { activity.Classification with Description = "Edited on github.com" }; Revision = 2 }
    github.WriteExternally(folder.Location, $"deployments/chrona/datasets/org_acme/{path}", Some(ActivityRecord.encode edited |> ok))
    first.Close()

    // Read once more from GitHub, which holds it for review, then offline.
    let second = Device(github, RepositoryVisibility.Private, "production", browser)
    second.Open()
    Assert.False(second.Model.Ledger.Activities.ContainsKey id)
    second.Close()

    let offline = Device(github, RepositoryVisibility.Private, "production", browser)
    offline.Offline <- true
    offline.Open()
    Assert.True(offline.Model.Store.Cached.IsSome)
    Assert.False(offline.Model.Ledger.Activities.ContainsKey id)
    Assert.Equal<string list>([ "CHRONA.INTEGRITY.EXTERNAL_EDIT" ], offline.Model.Store.Integrity |> List.map code)

[<Fact>]
let ``the read cache is this account's: another account on the same browser never opens from it`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let config = configuration "production"
    let owner = Device(github, RepositoryVisibility.Private, "production", browser)
    owner.Open()
    owner.Ui("memberId", "1001")
    owner.Ui("memberName", "hubot")
    owner.Ui("memberAccess", "ownTime")
    owner.Ui("admitMember", "")
    owner.Close()

    let other = Device(github, RepositoryVisibility.Private, "production", hubot, config, browser)
    other.Offline <- true
    other.Open()
    Assert.True(other.Model.Store.Failure.IsSome)
    Assert.Equal(None, other.Model.Store.Cached)

[<Fact>]
let ``a refusal from GitHub is never answered with what was cached`` () =
    let github = InMemoryStore()
    let browser = Browser()
    readOnce github browser |> ignore

    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Refusing <- Some "Your GitHub account cannot see acme/chrona-data."
    device.Open()
    Assert.Equal(Some "Your GitHub account cannot see acme/chrona-data.", device.Model.Store.Failure)
    Assert.Equal(None, device.Model.Store.Cached)

[<Fact>]
let ``signing out clears the account's read cache, unless its unsent work is kept here under 'ask'`` () =
    // Nothing unsent: the cache goes.
    let github = InMemoryStore()
    let browser = Browser()
    readOnce github browser |> ignore
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    device.Ui("signOut", "")
    Assert.True(signedOut device)
    device.Close()
    let after = Device(github, RepositoryVisibility.Private, "production", browser)
    after.Offline <- true
    after.Open()
    Assert.True(after.Model.Store.Failure.IsSome)

    // Kept under 'ask': the cache stays with it, so the account opens offline
    // and sees its unsent work in context.
    let github = InMemoryStore()
    let browser = Browser()
    readOnce github browser |> ignore
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    device.Offline <- true
    record device "09:00" "10:00" "Pairing"
    device.Ui("signOut", "")
    device.Ui("signOutKeep", "")
    Assert.True(signedOut device)
    device.Close()
    let after = Device(github, RepositoryVisibility.Private, "production", browser)
    after.Offline <- true
    after.Open()
    Assert.True(after.Model.Store.Cached.IsSome)
    Assert.Equal<string list>([ "Pairing"; "Setup" ], descriptions after)

[<Fact>]
let ``under discardOnSignOut, signing out always clears the account's read cache`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let config = { configuration "production" with SharedDevice = Deployment.DiscardOnSignOut }
    let first = Device(github, RepositoryVisibility.Private, "production", octocat, config, browser)
    first.Open()
    record first "08:00" "08:30" "Setup"
    first.Offline <- true
    record first "09:00" "10:00" "Pairing"
    first.Ui("signOut", "")
    first.Ui("signOutDiscard", "")
    first.Ui("signOutDiscardConfirmed", "")
    Assert.True(signedOut first)
    first.Close()

    let after = Device(github, RepositoryVisibility.Private, "production", octocat, config, browser)
    after.Offline <- true
    after.Open()
    Assert.True(after.Model.Store.Failure.IsSome)
    Assert.Equal(None, after.Model.Store.Cached)

[<Fact>]
let ``clearing this device removes the read cache and the queue for every account, then signs out`` () =
    let github = InMemoryStore()
    let browser = Browser()
    readOnce github browser |> ignore
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    Assert.True(viewFlag "canClearDevice" device.Model)
    device.Ui("clearDevice", "")
    Assert.True(viewFlag "clearDeviceConfirming" device.Model)
    device.Ui("clearDeviceConfirmed", "")
    Assert.True(signedOut device)
    device.Close()

    let after = Device(github, RepositoryVisibility.Private, "production", browser)
    after.Offline <- true
    after.Open()
    Assert.True(after.Model.Store.Failure.IsSome)
    Assert.Equal(None, after.Model.Store.Cached)

[<Fact>]
let ``this device cannot be cleared while this account has unsent work`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    device.Offline <- true
    record device "09:00" "10:00" "Pairing"
    Assert.False(viewFlag "canClearDevice" device.Model)
    device.Ui("clearDevice", "")
    Assert.Equal(None, device.Model.Identity.DeviceClear)

[<Fact>]
let ``a tab that opened from the read cache while another tab holds the queue keeps its own changes in the page and sends them once GitHub is back`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let commits = readOnce github browser
    let holder = Device(github, RepositoryVisibility.Private, "production", browser)
    holder.Open()

    let second = Device(github, RepositoryVisibility.Private, "production", browser)
    second.Offline <- true
    second.Open()
    Assert.True(second.Model.Store.Cached.IsSome)
    Assert.Equal(HeldElsewhere false, second.Model.Store.Sync.Holder)
    record second "11:00" "12:00" "Review"
    Assert.Equal(1, second.Model.Store.Pending.Length)
    Assert.Empty(queued browser)

    second.Offline <- false
    second.Wake()
    Assert.Equal(None, second.Model.Store.Cached)
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(second.Model.Store.Pending)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal<string list>([ "Review"; "Setup" ], descriptions reader)

// ---- Periods under review on Arca (WI-0036) ---------------------------------

/// Saves the organization's period settings, as its administrator.
let private periodSettings (device: Device) (approval: bool) =
    device.Send(Ui("periodSubmissionExpected", None, "", Some true))
    device.Send(Ui("periodApprovalRequired", None, "", Some approval))
    device.Ui("savePeriodSettings", "")

[<Fact>]
let ``the organization's period settings are stored and every device reads them`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    device.Ui("periodCadence", "monthly")
    device.Ui("periodWeekStart", "Sunday")
    periodSettings device true
    Assert.Empty(device.Model.Store.Pending)

    let other = Device(github, RepositoryVisibility.Private, "production")
    other.Open()
    Assert.Equal(Chrona.Domain.Periods.Monthly, other.Model.PeriodConfig.Cadence)
    Assert.Equal(DayOfWeek.Sunday, other.Model.PeriodConfig.WeekStart)
    Assert.True(other.Model.PeriodConfig.SubmissionExpected)
    Assert.True(other.Model.PeriodConfig.ApprovalRequired)
    Assert.Equal(Some 1, other.Model.PeriodsStored |> Option.map _.Revision)

    // A second save is the next revision; one made from a stale read is a
    // conflict, never a silent overwrite.
    other.Ui("periodCadence", "weekly")
    other.Ui("savePeriodSettings", "")
    Assert.Empty(other.Model.Store.Conflicts)
    device.Ui("periodCadence", "daily")
    device.Ui("savePeriodSettings", "")

    match device.Model.Store.Conflicts with
    | [ { Divergences = [ Reconcile.PeriodsChanged _ ] } ] -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a submitted period holds its time until it is reopened, and that is stored`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    periodSettings device false
    record device "08:00" "08:30" "Setup"
    let id = device.Model.Ledger.Activities |> Map.toList |> List.head |> fst

    device.Ui("submitPeriod", "")
    Assert.Empty(device.Model.Store.Pending)
    Assert.Equal(Chrona.Domain.Activity.Submitted, device.Model.Ledger.Activities[id].Review)

    // Read back elsewhere: the period is closed (no approval required).
    let other = Device(github, RepositoryVisibility.Private, "production")
    other.Open()
    Assert.Equal(Chrona.Domain.Activity.Submitted, other.Model.Ledger.Activities[id].Review)
    Assert.Single(other.Model.Reviews) |> ignore

    // Its time is not added to, changed or attested.
    record other "09:00" "09:30" "Late"
    Assert.Equal<string list>([ "CHRONA.REVIEW.SUBMITTED_PERIOD" ], other.Model.Problems[ManualForm] |> List.map code)
    other.Ui("attestStatement", "All of it")
    other.Ui("attestDay", "")
    Assert.Equal<string list>([ "CHRONA.REVIEW.SUBMITTED_PERIOD" ], other.Model.Problems[AttestForm] |> List.map code)

    // Reopened with a reason (the administrator may reopen time), it can be
    // changed again, and the reopening is stored.
    other.Ui("reopenReason", "A missing entry")
    other.Ui("reopenPeriod", "")
    Assert.Empty(other.Model.Store.Pending)
    record other "09:00" "09:30" "Late"
    Assert.Empty(other.Model.Store.Pending)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal(Chrona.Domain.Activity.Reopened, reader.Model.Ledger.Activities[id].Review)
    Assert.Equal(2, reader.Model.Reviews.Length)
    Assert.Equal<string list>([ "Late"; "Setup" ], descriptions reader)

[<Fact>]
let ``someone who approves sees another member's submission, approves it, and the member's period is closed`` () =
    let github = InMemoryStore()
    let config = configuration "production"
    let owner = Device(github, RepositoryVisibility.Private, "production")
    owner.Open()
    owner.Ui("memberId", "1001")
    owner.Ui("memberName", "hubot")
    owner.Ui("memberAccess", "ownTime")
    owner.Ui("admitMember", "")
    periodSettings owner true
    // Reference data hubot records against.
    record owner "07:00" "07:30" "Owner's own"

    let teammate = Device(github, RepositoryVisibility.Private, "production", hubot, config)
    teammate.Open()
    record teammate "08:00" "08:30" "Hubot's work"
    teammate.Ui("submitPeriod", "")
    Assert.Empty(teammate.Model.Store.Pending)

    // The approver opens the records again: the submission waits for them.
    let approver = Device(github, RepositoryVisibility.Private, "production")
    approver.Open()
    Assert.True(viewFlag "hasObligations" approver.Model)
    Assert.Single(approver.Model.Reviewing) |> ignore
    Assert.Equal<string list>([ "Owner's own" ], descriptions approver)

    let submission = Chrona.Domain.PeriodReview.awaitingApproval true approver.Model.Reviews |> List.exactlyOne
    approver.Send(Ui("reviewNote", Some submission.SubmissionId, "Looks right", None))
    approver.Send(Ui("approveSubmission", Some submission.SubmissionId, "", None))
    Assert.Empty(approver.Model.Store.Pending)
    Assert.Empty(approver.Model.Store.Conflicts)

    let back = Device(github, RepositoryVisibility.Private, "production", hubot, config)
    back.Open()
    Assert.Equal<Chrona.Domain.Activity.ReviewState list>([ Chrona.Domain.Activity.Approved ], back.Model.Ledger.Activities |> Map.toList |> List.map (snd >> _.Review))

    match Chrona.Domain.PeriodReview.stateOf true back.Model.Reviews hubot.ActorId submission.Period with
    | Chrona.Domain.PeriodReview.Closed approval ->
        Assert.Equal(octocat.ActorId, approval.By)
        Assert.Equal(Some "Looks right", approval.Note)
    | other -> failwith $"%A{other}"

    // The member cannot reopen approved time on their own.
    back.Ui("reopenReason", "Changed my mind")
    back.Ui("reopenPeriod", "")
    Assert.Equal<string list>([ "CHRONA.AUTH.UNAUTHORIZED_CAPABILITY" ], back.Model.Problems[ReviewForm] |> List.map code)

[<Fact>]
let ``a returned submission is open for correction, with the reason, and can be submitted again`` () =
    let github = InMemoryStore()
    let config = configuration "production"
    let owner = Device(github, RepositoryVisibility.Private, "production")
    owner.Open()
    owner.Ui("memberId", "1001")
    owner.Ui("memberName", "hubot")
    owner.Ui("memberAccess", "ownTime")
    owner.Ui("admitMember", "")
    periodSettings owner true
    record owner "07:00" "07:30" "Owner's own"

    let teammate = Device(github, RepositoryVisibility.Private, "production", hubot, config)
    teammate.Open()
    record teammate "08:00" "08:30" "Hubot's work"
    teammate.Ui("submitPeriod", "")

    let approver = Device(github, RepositoryVisibility.Private, "production")
    approver.Open()
    let submission = Chrona.Domain.PeriodReview.awaitingApproval true approver.Model.Reviews |> List.exactlyOne
    approver.Send(Ui("reviewNote", Some submission.SubmissionId, "Add the standup", None))
    approver.Send(Ui("rejectSubmission", Some submission.SubmissionId, "", None))
    Assert.Empty(approver.Model.Store.Pending)

    let back = Device(github, RepositoryVisibility.Private, "production", hubot, config)
    back.Open()

    match Chrona.Domain.PeriodReview.stateOf true back.Model.Reviews hubot.ActorId submission.Period with
    | Chrona.Domain.PeriodReview.Returned rejection -> Assert.Equal(Some "Add the standup", rejection.Note)
    | other -> failwith $"%A{other}"

    record back "09:00" "09:15" "Standup"
    Assert.Empty(back.Model.Store.Pending)
    // Submitted again, later.
    back.Now <- start.AddMinutes 30.0
    back.Ui("submitPeriod", "")
    Assert.Empty(back.Model.Store.Pending)
    Assert.Empty(back.Model.Store.Conflicts)

    match Chrona.Domain.PeriodReview.stateOf true back.Model.Reviews hubot.ActorId submission.Period with
    | Chrona.Domain.PeriodReview.AwaitingApproval again -> Assert.Equal(2, again.Covered.Length)
    | other -> failwith $"%A{other}"
