/// The data of the application's two ports (DF-CHRONA-2026-0003): who is
/// working (`Session`), and what the engine asks the store to make durable
/// (`StoreRequest`), hears back (`StoreOutcome`) and knows of where the
/// records stand (`StoreState`, `SyncState`, `StoreContents`).
module Chrona.Engine.App.ModelPorts

open System
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Domain

type SessionKind =
    /// No sign-in: one person, in this browser tab.
    | LocalSession
    | SignedIn of provider: string

type Session =
    { ActorId: string
      OrganizationId: string
      DisplayName: string
      Kind: SessionKind }

/// What the engine asks the store to make durable: the authoritative records
/// a command produced or changed, whole.
type StoreRequest =
    { CommitId: string
      Activities: Activity list
      References: Reference.Item list
      Attestations: Review.Attestation list
      /// Memberships admitted or changed (3).
      Members: Access.Membership list
      /// Principals removed from the roster.
      RemovedMembers: string list
      /// Records edited outside Chrona that the person accepted (41), as
      /// they reviewed them: the store trusts them again only if they are
      /// still stored that way, and they are among `Activities` at their
      /// next revision.
      Accepted: Activity list
      /// The command's audit entries, where each is kept (25, WI-0056).
      Audit: AuditRecord.Audited list
      /// The organization's period configuration, saved (WI-0036).
      Periods: PeriodConfigRecord.StoredPeriods option
      /// Periods' review steps made: a submission, approval, rejection or
      /// reopening (WI-0036).
      Reviews: PeriodReview.PeriodReview list
      /// Observations' candidates decided (WI-0038).
      Candidates: Observations.Candidate list }

/// A request with nothing in it yet.
let emptyRequest (commitId: string) =
    { CommitId = commitId
      Activities = []
      References = []
      Attestations = []
      Members = []
      RemovedMembers = []
      Accepted = []
      Periods = None
      Reviews = []
      Candidates = []
      Audit = [] }

type StoreOutcome =
    | Committed
    /// The stored records moved and the change no longer fits them, decided
    /// again by Chrona's rules (21): what diverged, for the person.
    | Conflict of divergences: Reconcile.Divergence list
    | Failed of detail: string
    /// The store cannot tell whether the write took effect (21, 31).
    | OutcomeUnknown of detail: string

type StoreKind =
    /// Kept in this tab's memory only; gone when the tab closes.
    | InMemory
    | Durable of name: string

/// Which of this browser's Chrona tabs holds its unsent changes (WI-0067):
/// one tab at a time keeps, reads back and sends them (Arca's
/// LocalStorageQueue.own over a Web Lock), so two tabs never overwrite or
/// both send the same changes.
type QueueHolder =
    /// This tab holds them.
    | HeldHere
    /// Another tab holds them. This tab keeps its own changes in the page
    /// and sends them while GitHub can be reached. `waiting`: the person
    /// asked this tab to take them over, and it is doing so.
    | HeldElsewhere of waiting: bool

/// Where this browser keeps the unsent changes this tab holds (WI-0059,
/// Limen LCP-065): shown wherever the sync state is.
type Durability =
    /// IndexedDB: they survive closing the tab and restarting the browser.
    | InIndexedDb
    /// localStorage, where IndexedDB cannot be used: they survive too,
    /// within a smaller budget.
    | InLocalStorage
    /// This page only: closing it loses them. Said before a change is made
    /// offline.
    | InPage

/// Where this page's unsent changes stand (23, WI-0033).
type SyncState =
    { /// The last attempt to reach GitHub failed; changes wait to be sent
      /// and are retried with back-off.
      Offline: bool
      /// Unsent changes are kept in this browser's storage and survive a
      /// refresh or restart. False when the browser offers no storage, or
      /// its storage is full or holds another account's unsent changes.
      KeptInBrowser: bool
      /// Why they are not kept in this browser, for the person.
      Note: string option
      /// Which tab holds this browser's unsent changes.
      Holder: QueueHolder
      /// Where the changes this tab holds are kept.
      Durability: Durability
      /// Something about the kept changes the person must be told (moved
      /// from localStorage, waiting to be moved, lost by the browser).
      Notice: string option
      /// Unsent changes from an earlier version of Chrona that do not say
      /// whose they are (Arca 0.4.0): neither sent nor discarded until the
      /// person decides.
      Earlier: int }

let initialSync =
    { Offline = false
      KeptInBrowser = true
      Note = None
      Holder = HeldHere
      Durability = InIndexedDb
      Notice = None
      Earlier = 0 }

/// Which Chrona this page runs (WI-0063): its build, and a newer build the
/// deployment serves now, if it found one. A page never runs a stale shell
/// against a newer deployment without saying so.
type ShellState =
    { Build: string
      Newer: string option }

/// A change that was not stored because the stored records moved: kept,
/// with what diverged, until the person resolves it (23, 34). Neither side
/// is silently discarded.
type ConflictCase =
    { /// The commit that was refused.
      Id: string
      Request: StoreRequest
      Divergences: Reconcile.Divergence list }

type StoreState =
    { Kind: StoreKind
      /// Commits sent and not yet answered, oldest first.
      Pending: StoreRequest list
      Committed: int
      /// The last answer that was not `Committed`, until the next success.
      Problem: StoreOutcome option
      /// The organization's records are being read; nothing can be done yet.
      Opening: bool
      /// Why the records could not be read, until a retry succeeds.
      Failure: string option
      /// What was found wrong in the records read (39, 41).
      Integrity: Diagnostic list
      /// The person's records edited outside Chrona, held until reviewed (41).
      Held: Activity list
      /// Changes not stored because the records moved, awaiting the person.
      Conflicts: ConflictCase list
      /// Where the unsent changes stand.
      Sync: SyncState
      /// How many of the ledger's audit entries were handed to the store.
      Audited: int
      /// This account's changes waiting in this browser while the records
      /// cannot be opened (WI-0055).
      Waiting: int
      /// The months whose folders were read, as (year, month).
      Months: (int * int) list
      /// Months asked for and not read yet.
      Reading: (int * int) list
      /// Other people's months read for reports across people (WI-0039).
      Others: (string * (int * int)) list
      /// Other people's months asked for and not read yet.
      OthersReading: (string * (int * int)) list
      /// Each person's months that hold time, from the activity index.
      History: ActivityIndex.MonthTotal list
      /// What the activity index covers, or why it is not kept.
      Index: string
      /// The activity index is kept and agrees with every folder read, so
      /// the months it lists are the only ones that hold time (WI-0039).
      IndexCurrent: bool
      /// The organization has no administrator the deployment lists: why,
      /// and whether this person, being listed, may confirm themselves.
      Confirmation: (string * bool) option
      /// Shown from this browser's read cache while GitHub cannot be
      /// reached: when GitHub last gave them (WI-0057).
      Cached: DateTimeOffset option }

/// Where reading producers' inboxes stands (20, WI-0038): never in the way of
/// recording time, and observable.
type InboxStatus =
    { /// A pass is reading the inboxes now.
      Running: bool
      /// Observations received so far: each with its candidate and receipt.
      Received: int
      /// Observations still waiting in the inboxes, for a later pass.
      Waiting: int
      /// Payloads that are not observations (a receipt says why), left in
      /// the inbox for their producer: source, observation id, reasons.
      Invalid: (string * string * string list) list
      /// Observations that could not be processed this pass; tried again.
      Failed: int
      /// Receipts that name a candidate no folder holds (written outside
      /// Chrona, or the candidate removed): source, observation id and the
      /// candidate named (expansion 39, WI-0074).
      Orphaned: (string * string * string) list
      LastRun: DateTimeOffset option }

let idleInbox =
    { Running = false
      Received = 0
      Waiting = 0
      Invalid = []
      Failed = 0
      Orphaned = []
      LastRun = None }

/// Another of the deployment's organizations, read for a report across
/// organizations (WI-0075): what this person may see of its time.
type Elsewhere =
    { OrganizationId: string
      DisplayName: string
      /// The person is one of its members; otherwise nothing of it is read.
      Member: bool
      /// The person may see everyone's time there; otherwise only their own
      /// was read.
      SeesOrganization: bool
      Activities: Activity list
      /// Its reference data, to name its projects and activity types.
      References: Reference.Item list
      Members: Access.Membership list
      /// What was read: everyone's time or only the person's, and the months.
      Everyone: bool
      Months: (int * int) list
      /// Why it could not be read, when it could not.
      Failure: string option }

/// What the store read from the organization's folder.
type StoreContents =
    { /// Where the records live, for the person: `owner/repository`.
      Name: string
      /// Shown from this browser's read cache while GitHub cannot be
      /// reached: when GitHub last gave them (WI-0057). None when read from
      /// GitHub now.
      Cached: DateTimeOffset option
      Activities: Activity list
      References: Reference.Item list
      Attestations: Review.Attestation list
      /// The organization's roster as stored.
      Members: Access.Membership list
      /// Records edited outside Chrona, held for review (41).
      Held: Activity list
      /// The audit trail of the records read, oldest first (25).
      Audit: Ledger.AuditEntry list
      Problems: Diagnostic list
      /// The months whose folders were read, as (year, month).
      Months: (int * int) list
      /// Other people's months read, for reports across people (WI-0039).
      Others: (string * (int * int)) list
      /// Each person's months that hold time, from the activity index
      /// (derived, WI-0034).
      History: ActivityIndex.MonthTotal list
      /// What the activity index covers, or why it is not kept, for the person.
      Index: string
      /// The activity index is kept and agrees with every folder read.
      IndexCurrent: bool
      /// The organization's period configuration, when it was ever saved
      /// (WI-0036).
      Periods: PeriodConfigRecord.StoredPeriods option
      /// Periods' review steps read: this person's, and, for someone who
      /// approves time, everyone's (WI-0036).
      Reviews: PeriodReview.PeriodReview list
      /// Other people's time that was read: what waits for this person's
      /// approval (WI-0036), and the months a report across everyone read
      /// (WI-0039).
      Reviewing: Activity list
      /// Observations' candidates read: every one still to decide, and those
      /// decided in the months read (WI-0038).
      Candidates: Observations.Candidate list }
