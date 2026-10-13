/// This page's store: what it holds while it runs, its jobs run one at a
/// time, and the read cache (WI-0057).
module internal Chrona.Application.StorePage

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

/// The version of Chrona's records in the read cache (WI-0057): raised when
/// a record format changes, so an entry of the old one is never shown.
[<Literal>]
let CacheSchema = 1

/// The cache partition that lists a folder's records held for review.
[<Literal>]
let HeldPrefix = "held-for-review:"

/// The cache partition that holds the activity index.
[<Literal>]
let IndexPartition = "derived:activity-index"

/// The longest wait between attempts to reach GitHub.
[<Literal>]
let MaxRetryMs = 60000

/// The first wait after GitHub could not be reached.
[<Literal>]
let FirstRetryMs = 5000

/// What this page's store holds while it runs: its connection to the page
/// and to where the data lives, the records as opened, its jobs, the queues
/// it holds, and what it knows of the inboxes. One per page, made by
/// `ArcaStore.arca`; every part of the store reads and changes it in the
/// store's own turn.
[<NoComparison; NoEquality>]
type Page =
    { Bridge: Bridge
      Backend: Backend
      /// The clock.
      Now: unit -> DateTimeOffset
      /// Mints the idempotency keys of the operations the store starts itself.
      NewKey: string -> string
      /// What this page asks of Limen: the store and coordination packs.
      Host: Arca.Limen.LimenHost
      mutable Opened: Opened option
      /// Records read but held until a listed administrator confirms.
      mutable Pending: Opened option
      Jobs: Queue<unit -> Async<Update.Msg list>>
      mutable Busy: bool
      /// Changes this tab kept that another tab took over, not told yet.
      Handed: ResizeArray<string>
      /// The read cache in IndexedDB (WI-0057), once opened: None where the
      /// browser has none, and the records then simply need GitHub to open.
      mutable Cache: Arca.Limen.IndexedDbCache option option
      /// Unsent changes from an earlier version, naming no one, waiting for
      /// the person to send, keep or discard them.
      mutable EarlierWaiting: int
      /// The queues this page holds, by lock: held until the page goes, the
      /// person signs out, or another tab takes one over. A Web Lock is not
      /// re-entrant, so a queue this page holds is never asked for again.
      Held: Dictionary<string, Arca.Limen.OwnedQueue>
      mutable RetryMs: int
      mutable Retrying: bool
      /// How many times each entry was decided again, by sequence.
      Decided: Dictionary<int64, int>
      /// Opens the records from GitHub again, in place of what the read cache
      /// showed: set once the opening jobs exist (`ArcaStore.arca`).
      mutable Reopen: unit -> Async<Update.Msg list>
      /// Reads the producers' inboxes after the records open (WI-0038): set
      /// once the inbox job exists (`ArcaStore.arca`).
      mutable ReadInboxes: unit -> unit
      /// The inboxes wait until this page's own unsent changes are sent.
      mutable InboxDeferred: bool
      /// What reading the inboxes last found on this page.
      mutable InboxStatus: InboxStatus
      /// Each receipt this page has checked, by path: the revision checked,
      /// and the candidate it names that no folder holds, if any (WI-0074).
      mutable ReceiptChecks: Map<string, Revision * (string * string * string) option>
      /// The deployment, session and dates the records were last opened for.
      mutable LastOpen: (Deployment.DeploymentConfig * Session * DateOnly list) option }

/// A page's store before anything is opened.
let create (bridge: Bridge) (backend: Backend) (now: unit -> DateTimeOffset) (newKey: string -> string) : Page =
    { Bridge = bridge
      Backend = backend
      Now = now
      NewKey = newKey
      Host = limenHost bridge now
      Opened = None
      Pending = None
      Jobs = Queue<unit -> Async<Update.Msg list>>()
      Busy = false
      Handed = ResizeArray<string>()
      Cache = None
      EarlierWaiting = 0
      Held = Dictionary<string, Arca.Limen.OwnedQueue>()
      RetryMs = FirstRetryMs
      Retrying = false
      Decided = Dictionary<int64, int>()
      Reopen = fun () -> async.Return []
      ReadInboxes = ignore
      InboxDeferred = false
      InboxStatus = idleInbox
      ReceiptChecks = Map.empty
      LastOpen = None }

/// Runs the queued jobs one at a time; each job's messages go to the
/// engine as it finishes.
let rec run (page: Page) () =
    async {
        if page.Jobs.Count = 0 then
            page.Busy <- false
        else
            let job = page.Jobs.Dequeue()

            let! messages =
                async {
                    try
                        return! job ()
                    with error ->
                        page.Jobs.Clear()
                        page.Busy <- false
                        return raise error
                }

            let messages =
                if page.Handed.Count = 0 then
                    messages
                else
                    let ids = List.ofSeq page.Handed
                    page.Handed.Clear()
                    messages @ [ Update.UnsentHandedOver ids ]

            page.Bridge.Emit messages
            return! run page ()
    }

let serial (page: Page) (job: unit -> Async<Update.Msg list>) =
    page.Jobs.Enqueue job

    if not page.Busy then
        page.Busy <- true

        page.Bridge.Start(
            async {
                do! run page ()
                return []
            }
        )

let readCache (page: Page) () =
    async {
        match page.Cache with
        | Some opened -> return opened
        | None ->
            let! result = Arca.Limen.IndexedDbReadCache.openCache page.Host Arca.Limen.IndexedDbReadCache.DefaultBudget
            let opened = Result.toOption result
            page.Cache <- Some opened
            return opened
    }

/// The queue's stores in order, freeing the read cache's space when a
/// save meets the browser's quota (LCP-087).
let queueOptions (page: Page) () =
    async {
        let! cached = readCache page ()
        return { StoreUnsentQueue.queueOptions with FreeSpace = cached |> Option.map _.FreeSpace }
    }

/// Keeps what was just read from GitHub in the read cache, one entry per
/// partition, best-effort: a failure never fails the read (LCP-083).
let keepRead (page: Page) (account: string) (folder: Namespace) (token: Condition) (partitions: (string * StoredObject list) list) =
    async {
        match! readCache page () with
        | None -> ()
        | Some cached ->
            // One IndexedDB transaction for them all (Arca 0.5.0's
            // `KeepMany`, arca#38): best-effort, as `Keep` is (WI-0077).
            do!
                partitions
                |> List.choose (fun (partition, objects) ->
                    ReadCache.key account folder partition
                    |> Result.toOption
                    |> Option.map (fun key -> ReadCache.entry key CacheSchema (page.Now ()) (freshOf token objects)))
                |> cached.KeepMany
    }
