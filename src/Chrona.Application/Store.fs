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
///   change conditioned on the repository state last read. When that state
///   moved, the records are reloaded and Chrona's rules decide the change
///   again (`Reconcile`); what no longer fits is answered as a conflict
///   naming what diverged, for the person to resolve. An unknown outcome is
///   reconciled before anything is sent again (21, 31). Commits run one at
///   a time.
///
/// The port is here; the Arca store behind it is `ArcaStore.arca`, its
/// parts in Store/: the GitHub backend (`StoreGitHub`), the records as
/// opened (`StoreState`), the browser's queue of unsent changes
/// (`StoreUnsentQueue`, `StoreHolding`, `StoreSync`), the activity index
/// (`StoreIndex`), this page's store (`StorePage`), reading (`StoreReads`,
/// `StoreElsewhere`), opening (`StoreOpening`), the engine's requests
/// (`StoreJobs`), the producers' inboxes (`InboxReading`, `InboxPass`) and
/// this device (`StoreDevice`).
module Chrona.Application.Store

open System
open System.Collections.Generic
open Arca
open Arca.GitHub
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.Model
open Chrona.Application.Bridge

/// Why the records could not be read, for the person: GitHub could not be
/// reached (the read cache may stand in, WI-0057), or it refused (it never
/// may: a refusal is never answered with what was cached).
[<RequireQualifiedAccess>]
type Unopened =
    | Unreachable of reason: string
    | Refused of reason: string

/// The reason, for the person.
let reasonOf =
    function
    | Unopened.Unreachable reason
    | Unopened.Refused reason -> reason

/// A location's storage: its provider, and, where the provider offers it,
/// reads pinned to one commit (Arca 0.5.0, ARCA-API-006): a refresh reads
/// every folder at the state it observed, without resolving the branch for
/// each read (WI-0077).
[<NoComparison; NoEquality>]
type Connection =
    { Provider: StorageProvider
      PinAt: (ChangeToken -> PinnedReads) option }

/// Reads through the pin where there is one: the same provider, its List,
/// Read and NamespaceState answered at the pinned commit. Histories and
/// commits are never pinned.
let pinnedAt (connection: Connection) (token: ChangeToken) =
    match connection.PinAt with
    | Some pinAt ->
        let pin = pinAt token

        { connection.Provider with
            List = pin.List
            Read = pin.Read
            NamespaceState = pin.NamespaceState }
    | None -> connection.Provider

/// What the store needs from where the data lives.
[<NoComparison; NoEquality>]
type Backend =
    { /// The storage serving a location.
      Connect: DataLocation -> Connection
      /// The repository's facts for the signed-in credential: its visibility
      /// and whether it may be read and written (ARCA-AUTH-003, 2.7). A
      /// refusal says why, for the person.
      Resolve: DataLocation -> Async<Result<CapabilitySnapshot, Unopened>> }

/// The store port the application drives.
[<NoComparison; NoEquality>]
type StorePort =
    { Kind: StoreKind
      Open: Deployment.DeploymentConfig -> Session -> DateOnly list -> unit
      /// A listed administrator confirms an organization that has none.
      Confirm: unit -> unit
      Commit: StoreRequest -> unit
      /// Read the month folders these dates need.
      Read: DateOnly list -> unit
      /// Read other people's months, for a report across people (WI-0039).
      ReadOthers: (string * DateOnly) list -> unit
      /// Read the deployment's other organizations, for a report across
      /// organizations (WI-0075): each organization, whether everyone's time
      /// is wanted, and the months.
      ReadElsewhere: (string * bool * DateOnly list) list -> unit
      /// Rebuild the activity index from the stored records.
      Rebuild: unit -> unit
      /// Read the producers' inboxes now (WI-0038).
      ReadInboxes: unit -> unit
      /// Send this account's unsent changes now.
      SendNow: unit -> unit
      /// Discard this account's unsent changes from this device.
      Discard: unit -> unit
      /// Take this browser's unsent changes over from the tab holding them
      /// ("use this tab instead"; WI-0059).
      TakeOver: unit -> unit
      /// Send the unsent changes from an earlier version, naming no one, as
      /// this person's (the person said they are theirs).
      SendEarlier: unit -> unit
      /// Discard the unsent changes from an earlier version that cannot have
      /// landed (the person confirmed).
      DiscardEarlier: unit -> unit
      /// Ask again to hold them: the tab that held them may have closed.
      Claim: unit -> unit
      /// The browser closed the queue's database under the page: open it again.
      Reconnect: unit -> unit
      /// This tab no longer holds this browser's unsent changes.
      Lost: unit -> unit
      /// The account leaves this device: its read cache goes unless its
      /// unsent work was kept here (WI-0057, Arca's SignOut.plan).
      SignedOut: Update.UnsentChoice -> int -> unit
      /// Clear Chrona's data from this browser, for every account.
      ClearDevice: unit -> unit }

/// A store that keeps nothing: every commit is acknowledged at once. For a
/// deployment that configures no location.
let inMemory (bridge: Bridge) : StorePort =
    { Kind = InMemory
      Open = fun _ _ _ -> bridge.Start(async { return [ Update.StoreUnavailable "This deployment stores nothing." ] })
      Confirm = fun () -> ()
      Commit = fun request -> bridge.Start(async { return [ Update.StoreAnswered(request.CommitId, Committed) ] })
      Read = fun _ -> ()
      ReadOthers = fun _ -> ()
      ReadElsewhere = fun _ -> ()
      Rebuild = fun () -> ()
      ReadInboxes = fun () -> ()
      SendNow = fun () -> ()
      Discard = fun () -> bridge.Start(async { return [ Update.UnsentDiscarded 0 ] })
      TakeOver = fun () -> ()
      SendEarlier = fun () -> ()
      DiscardEarlier = fun () -> ()
      Claim = fun () -> ()
      Reconnect = fun () -> ()
      Lost = fun () -> ()
      SignedOut = fun _ _ -> ()
      ClearDevice = fun () -> bridge.Start(async { return [ Update.DeviceCleared(Ok()) ] }) }
