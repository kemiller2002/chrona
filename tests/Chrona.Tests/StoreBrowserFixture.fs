/// The store's tests' browser (WI-0032, WI-0059): one browser's localStorage,
/// IndexedDB and Web Locks shared by its pages, the deployment they open and
/// the person who signs in.
module Chrona.Tests.StoreBrowserFixture

open System
open Xunit
open Arca
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update
open Chrona.Application

let ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let configuration (environment: string) =
    """{"environment":"ENV","environmentName":"ENV","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":"deployments"},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://chrona.test/"},"organizations":[{"id":"org_acme","displayName":"Acme Consulting","slug":"acme","timeZone":"America/New_York","administrators":["583231"]}]}"""
        .Replace("ENV", environment)
    |> Deployment.parse
    |> ok

let start = DateTimeOffset(2026, 10, 8, 14, 10, 0, TimeSpan.FromHours -4.0)

let octocat =
    { ActorId = "github:583231"
      OrganizationId = "local"
      DisplayName = "octocat"
      Kind = SignedIn "github" }

let snapshot (visibility: RepositoryVisibility) : CapabilitySnapshot =
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

let keys = ref 0

/// One browser's localStorage, IndexedDB (Limen's store pack, through its
/// fake) and Web Locks, shared by the pages opened in it.
type Browser() =
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
