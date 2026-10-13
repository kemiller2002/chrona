/// One device in the store's tests: a page's bridge and store over the
/// shared in-memory GitHub, driven as the application drives it, and how a
/// test records time and reads what is shown.
module Chrona.Tests.StoreDeviceFixture

open System
open Xunit
open Arca
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update
open Chrona.Engine.App.UpdateSession
open Chrona.Engine.App.UpdateDispatch
open Chrona.Application
open Chrona.Tests.StoreBrowserFixture

/// One device: a page's bridge and store over the shared in-memory GitHub,
/// in a browser whose localStorage it shares with the browser's other pages.
type Device(github: InMemoryStore, visibility: RepositoryVisibility, environment: string, person: Session, config: Deployment.DeploymentConfig, browser: Browser) =

    let bridge = Bridge.Bridge()
    /// This page's IndexedDB connection, through Limen's fake store pack.
    let indexedDb = browser.Tab()

    /// Commits that someone else's commit to the repository beats first.
    let mutable beaten = 0
    /// Where those commits write: another application's folder, or inside
    /// Chrona's organization folder (another device, a tool).
    let mutable beatenInside = false
    /// Commit attempts made, and the one that finds GitHub out of reach.
    let mutable attempts = 0
    let mutable unreachableAttempt: int option = None
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

    /// The records whose history was asked for, in order (WI-0077).
    let mutable historyAsked: string list = []
    /// Whether the backend offers pinned reads, the tokens pinned and the
    /// reads made through a pin (WI-0077).
    let mutable pins = false
    let mutable pinnedAt: ChangeToken list = []
    let mutable pinnedReads = 0
    /// The paths read or listed without a pin while pins are offered.
    let mutable unpinned: string list = []

    let provider location =
        let real = github.Provider

        { real with
            ChangeToken = fun ns -> if offline then unreachable () else real.ChangeToken ns
            NamespaceState = fun ns -> if offline then unreachable () else real.NamespaceState ns
            Read =
                fun ns path ->
                    if pins then unpinned <- unpinned @ [ RelativePath.render path ]
                    if offline then unreachable () else real.Read ns path
            List =
                fun ns path ->
                    if pins then unpinned <- unpinned @ [ RelativePath.render path ]
                    if offline then unreachable () else real.List ns path
            History =
                fun ns path ->
                    historyAsked <- historyAsked @ [ RelativePath.render path ]
                    if offline then unreachable () else real.History ns path
            Reconcile = fun ns pending -> if offline then unreachable () else real.Reconcile ns pending
            Commit =
                fun operation ->
                    async {
                        attempts <- attempts + 1

                        if offline || unreachableAttempt = Some attempts then
                            return! unreachable ()
                        else
                            if beaten > 0 then
                                beaten <- beaten - 1
                                let path =
                                    if beatenInside then
                                        $"deployments/chrona/datasets/org_acme/elsewhere/{beaten}.txt"
                                    else
                                        $"other-application/{beaten}.txt"

                                github.WriteExternally(location, path, Some "a file written elsewhere")

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
        { Connect =
            fun location ->
                let unpinned = provider location

                { Provider = unpinned
                  PinAt =
                    if pins then
                        Some(fun token ->
                            pinnedAt <- pinnedAt @ [ token ]

                            ({ At = token
                               NamespaceState = unpinned.NamespaceState
                               // Straight to the store: a pinned read is not an unpinned one.
                               List =
                                 fun ns path ->
                                     pinnedReads <- pinnedReads + 1
                                     github.Provider.List ns path
                               Read =
                                 fun ns path ->
                                     pinnedReads <- pinnedReads + 1
                                     github.Provider.Read ns path }: PinnedReads))
                    else
                        None }
          Resolve =
            fun _ ->
                async.Return(
                    match offline, refusing with
                    | true, _ -> Error(Store.Unopened.Unreachable "GitHub could not be reached.")
                    | _, Some reason -> Error(Store.Unopened.Refused reason)
                    | false, None -> Ok(snapshot visibility)
                ) }

    let store =
        ArcaStore.arca bridge backend (fun () -> start) (fun prefix -> $"{prefix}-{Threading.Interlocked.Increment keys:D8}")

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
            | ReadOthers wanted -> store.ReadOthers wanted
            | ReadElsewhere wanted -> store.ReadElsewhere wanted
            | RebuildIndex -> store.Rebuild()
            | ReadInboxes -> store.ReadInboxes()
            | SendUnsent -> store.SendNow()
            | TakeOverQueue -> store.TakeOver()
            | SendEarlier -> store.SendEarlier()
            | DiscardEarlier -> store.DiscardEarlier()
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
    member _.Beaten(count: int) =
        beaten <- count
        beatenInside <- false

    /// The commit attempt `after` attempts from now finds GitHub out of reach.
    /// The records whose history GitHub was asked for, in order.
    member _.HistoryAsked = historyAsked

    /// Offers pinned reads, as Arca 0.5.0's GitHub connection does.
    member _.UsePins() = pins <- true
    member _.PinnedAt = pinnedAt
    member _.PinnedReads = pinnedReads
    member _.Unpinned = unpinned

    member _.UnreachableAfter(after: int) = unreachableAttempt <- Some(attempts + after)

    /// The next `count` commits are each beaten by a commit inside Chrona's
    /// organization folder.
    member _.BeatenInside(count: int) =
        beaten <- count
        beatenInside <- true

    /// Opens Chrona in this deployment and signs in as octocat.
    member this.Open() =
        this.Model <- { Model.initial noOne InMemory start with Deployment = Some config }
        this.Send(EnvironmentDescribed "America/New_York")
        this.Model <- { this.Model with Identity = { this.Model.Identity with Mode = SignInRequired false } }
        this.Send(IdentityChanged(SignedInAs person))

let project (model: Model) = (Reference.selectable Reference.Project model.References).Head.Id

let activityType (model: Model) = (Reference.selectable Reference.ActivityType model.References).Head.Id

/// Adds reference data and records a manual entry from 09:00 to 10:00 yesterday.
let record (device: Device) (startTime: string) (endTime: string) (description: string) =
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
let rowText (list: string) (key: string) (model: Model) =
    match Project.project model |> List.tryFind (fst >> (=) list) with
    | Some(_, Chrona.Engine.View.Items(row :: _)) ->
        match row |> List.tryFind (fst >> (=) key) with
        | Some(_, Chrona.Engine.View.Text value) -> value
        | other -> failwith $"%A{other}"
    | other -> failwith $"%A{other}"

/// Every object's path in the repository (acme/chrona-data, main).
let storedPaths (github: InMemoryStore) =
    github.State.Objects |> Map.toList |> List.map (fun (key, _) -> key.Substring(key.IndexOf ':' + 1))
