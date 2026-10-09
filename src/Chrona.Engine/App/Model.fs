/// The Chrona application's state: plain, immutable data. Pure.
///
/// Two ports keep the later slices pluggable (DF-CHRONA-2026-0003):
/// - **Identity.** `Session` says who is working: a local session when the
///   deployment configures no sign-in, otherwise the identity Fides resolved
///   from GitHub (WI-0029).
/// - **Store.** The engine asks for what became authoritative to be made
///   durable (`StoreRequest`) and hears back (`StoreOutcome`). Today the
///   in-memory store acknowledges at once and keeps nothing beyond this tab;
///   Arca (WI-0032) answers the same requests from GitHub.
module Chrona.Engine.App.Model

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
      Audit: AuditRecord.Audited list }

/// A request with nothing in it yet.
let emptyRequest (commitId: string) =
    { CommitId = commitId
      Activities = []
      References = []
      Attestations = []
      Members = []
      RemovedMembers = []
      Accepted = []
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
      Notice: string option }

let initialSync =
    { Offline = false
      KeptInBrowser = true
      Note = None
      Holder = HeldHere
      Durability = InIndexedDb
      Notice = None }

/// The build of a page that was not built for a deployment: it is never
/// compared with what is deployed.
[<Literal>]
let Development = "development"

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
      /// Each person's months that hold time, from the activity index.
      History: ActivityIndex.MonthTotal list
      /// What the activity index covers, or why it is not kept.
      Index: string
      /// The organization has no administrator the deployment lists: why,
      /// and whether this person, being listed, may confirm themselves.
      Confirmation: (string * bool) option
      /// Shown from this browser's read cache while GitHub cannot be
      /// reached: when GitHub last gave them (WI-0057).
      Cached: DateTimeOffset option }

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
      /// Each person's months that hold time, from the activity index
      /// (derived, WI-0034).
      History: ActivityIndex.MonthTotal list
      /// What the activity index covers, or why it is not kept, for the person.
      Index: string }

/// The page's own address (its origin and path), for a link that opens a
/// place from anywhere. Never its query: a sign-in callback's code and state
/// were there.
type PageAddress = { Origin: string; Path: string }

/// Why the address shows no place (CHX-460): a clear page, never a blank one
/// or another place.
type RouteProblem =
    /// The address names nothing in Chrona, names it wrongly, or names a
    /// place this person may not see (Limen's route outcome).
    | AddressProblem of Limen.Routing.RouteError
    /// The address names a record that does not exist, by its kind and id.
    | RecordMissing of kind: string * id: string

/// What a form says about classification, as typed (strings, unvalidated).
type ClassificationDraft =
    { ActivityTypeId: string
      ProjectId: string
      Description: string
      BusinessPurpose: string
      Tags: string list }

let emptyClassification =
    { ActivityTypeId = ""
      ProjectId = ""
      Description = ""
      BusinessPurpose = ""
      Tags = [] }

type ManualDraft =
    { Classification: ClassificationDraft
      StartDate: string
      StartTime: string
      EndDate: string
      EndTime: string
      Reason: string }

let emptyManual =
    { Classification = emptyClassification
      StartDate = ""
      StartTime = ""
      EndDate = ""
      EndTime = ""
      Reason = "" }

/// The activity detail screen's drafts, opened at the revision the person
/// saw: every command they send names that revision, so a change made
/// elsewhere in between is a conflict, never overwritten (21).
type Detail =
    { ActivityId: string
      Revision: int
      Amend: ClassificationDraft
      AmendReason: string
      VoidReason: string
      SplitFirst: string
      SplitSecond: string
      /// Which split part (1 or 2) carries each evidence id; 0 is neither.
      SplitEvidence: Map<string, int>
      EvidenceKind: string
      EvidenceUrl: string
      EvidenceLabel: string
      EvidenceSource: string
      EvidenceNotes: string }

/// The report form, as typed.
type ReportDraft =
    { From: string
      To: string
      ProjectId: string
      ActivityTypeId: string
      Tag: string
      /// "", "manual" or "timer".
      Method: string
      /// "", "billable", "non-billable" or "pending".
      Billability: string
      Text: string
      IncludeRemoved: bool
      /// "project", "activityType", "tag" or "day".
      Grouping: string
      /// "csv" or "json".
      Format: string
      /// When the export shown was generated: set when the report is opened
      /// or changed, so the text copied is the text the person saw.
      GeneratedAt: DateTimeOffset option }

let emptyReport =
    { From = ""
      To = ""
      ProjectId = ""
      ActivityTypeId = ""
      Tag = ""
      Method = ""
      Billability = ""
      Text = ""
      IncludeRemoved = false
      Grouping = "project"
      Format = "csv"
      GeneratedAt = None }

/// Where a signed-in session's tokens are kept (CHX-023). Session-only
/// retention is the default; neither survives closing the tab.
type Retention =
    /// In memory: gone when the page reloads or closes.
    | ThisPage
    /// In this tab's session storage: gone when the tab closes.
    | ThisTab

/// Where this tab is with sign-in (CHX-022).
type IdentityMode =
    /// Reading the deployment's configuration.
    | Configuring
    /// The deployment configures no sign-in: one person, in this tab.
    | LocalOnly
    /// The deployment's configuration cannot be used; nothing else can run.
    | Misconfigured of detail: string
    /// Sign-in is configured and no one is signed in. `Busy` while a sign-in
    /// or a callback is under way.
    | SignInRequired of busy: bool
    /// Someone signed in: `Model.Session` is their identity, resolved by the
    /// provider, never typed.
    | SignedInMode

/// Signing out while this account's changes have not reached GitHub
/// (WI-0058): nothing is lost silently, and nothing is left behind on the
/// device unknowingly.
type SignOutStep =
    /// The person chooses: send them now, keep them on this device for this
    /// account (when the deployment allows), or discard them.
    | ChoosingUnsent
    /// Sending them; signed out once every one is stored.
    | SendingUnsent
    /// Confirming that they are discarded.
    | ConfirmingDiscard
    /// Discarding them; signed out once they are gone.
    | DiscardingUnsent

/// Clearing Chrona's data from this browser ("Clear this device"; WI-0057,
/// Limen LCP-070, LCP-086): the unsent-changes queue and the read cache,
/// for every account, then signing out.
type DeviceClearStep =
    /// The person confirms; nothing is cleared yet.
    | ConfirmingClear
    /// Clearing; signed out once it is done.
    | Clearing

type IdentityState =
    { Mode: IdentityMode
      /// Signing out, while changes are unsent.
      SignOut: SignOutStep option
      /// Clearing this device.
      DeviceClear: DeviceClearStep option
      /// Why the device could not be cleared, for the person.
      DeviceNote: string option
      /// Why sending them did not finish, for the person.
      SignOutNote: string option
      Retention: Retention
      /// The code of the last sign-in outcome to tell the person about, for
      /// example `state_expired` or `signed_out`.
      Notice: string option
      /// The provider's callback parameters this page was opened with, until
      /// the configuration is read and the sign-in can be completed.
      Callback: (string * string) list
      /// The address to return to after sign-in, kept in this tab across the
      /// round trip to GitHub (its callback carries no fragment), until it
      /// is resumed (CHX-460).
      ReturnTo: string option
      /// A callback page is reading the kept return target; nothing is
      /// resumed until it is read.
      ReadingReturn: bool }

/// What the edge reports about sign-in.
type IdentityChange =
    | SignedInAs of Session
    | SigningIn
    /// No one is signed in now, and why (a callback failure, a sign-out, an
    /// expired or revoked session), when there is something to say.
    | SignedOutWith of notice: string option
    /// Signed in, but the provider or exchange could not be reached; the
    /// session is kept and the next attempt retries.
    | ProviderUnavailable

let initialIdentity =
    { Mode = Configuring
      SignOut = None
      DeviceClear = None
      DeviceNote = None
      SignOutNote = None
      Retention = ThisPage
      Notice = None
      Callback = []
      ReturnTo = None
      ReadingReturn = false }

/// A new member, as typed: their GitHub account's numeric id (shown to them
/// when they are not yet a member), a name, and what they may do.
type MemberDraft =
    { Id: string
      Name: string
      /// "ownTime", "reviewer" or "administrator".
      Access: string }

let emptyMember = { Id = ""; Name = ""; Access = "ownTime" }

/// Where a refusal is shown.
type Form =
    | TimerForm
    | CompletionForm
    | ManualForm
    | ReferenceForm
    | AmendForm
    | VoidForm
    | SplitForm
    | EvidenceForm
    | MergeForm
    | AttestForm
    /// The organization's period settings.
    | PeriodForm
    /// Copying or printing a report.
    | ExportForm
    /// The organization's members.
    | MemberForm
    /// Changes not stored because the records moved.
    | ConflictForm
    /// Accepting a record edited outside Chrona.
    | OutsideEditForm
    /// Rebuilding the activity index.
    | IndexForm

[<NoComparison>]
type Model =
    { /// Where the person is (CHX-460): the place the address names. While
      /// `RouteProblem` is set the page says why the address shows nothing.
      Place: Places.Place
      RouteProblem: RouteProblem option
      /// The canonical address the engine adopted or moved to last.
      Router: Limen.Routing.RouterState
      /// The page's own address, for "Copy link".
      Page: PageAddress
      /// What happened to the last "Copy link", in words.
      LinkStatus: string
      /// The link being copied, until the browser took it: shown to select by
      /// hand when the browser would not copy it.
      LinkText: string
      Session: Session
      /// Sign-in (CHX-022): whether it is configured and who is signed in.
      Identity: IdentityState
      /// The deployment's configuration, once read.
      Deployment: Deployment.DeploymentConfig option
      /// The organization's members and what each may do (3). Every command
      /// is checked against it.
      Roster: Access.Roster
      MemberDraft: MemberDraft
      Store: StoreState
      /// The business time zone; unknown until the browser describes it.
      Zone: Zone option
      /// The last instant the engine was told; projections never read a clock.
      Now: DateTimeOffset
      Ledger: Ledger.Ledger
      References: Reference.Catalogue
      Timer: Timer.TimerState
      /// Bumped whenever a timer starts or resumes, so a wake-up requested for
      /// an earlier run is recognised and ignored.
      TickGeneration: int
      /// A stopped timer whose time is held until it is completed.
      Stopped: Timer.Stopped option
      /// The person confirmed a long-running timer's time was all worked (10.4).
      LongTimerConfirmed: bool
      TimerDraft: ClassificationDraft
      CompletionDraft: ClassificationDraft
      Manual: ManualDraft
      Detail: Detail option
      /// Activities chosen on Today to merge, and the merged record's details.
      MergeSelection: string list
      MergeDraft: ClassificationDraft
      /// Daily attestations, newest last; earlier ones are never replaced (16).
      Attestations: Review.Attestation list
      AttestStatement: string
      /// The organization's timesheet periods (15).
      PeriodConfig: Periods.PeriodConfig
      Report: ReportDraft
      /// What happened to the last copy of an export, in words.
      CopyStatus: string
      NewNames: Map<Reference.Kind, string>
      Problems: Map<Form, Diagnostic list>
      /// Polite, one-off status text for screen readers: transitions only,
      /// never timer ticks (35).
      Announcement: string
      /// Entries for dates more than this many days before today need a
      /// reason. The legacy rule is 0: any date that is not today.
      HistoricalAfterDays: int
      /// What recovering the device's timer found, said again when the
      /// records open (WI-0055).
      TimerNote: string option
      /// The Chrona this page runs, and a newer one deployed since (WI-0063).
      Shell: ShellState }

/// The principal a session acts as. A local session and a GitHub sign-in are
/// both a person; agents, services and integrations reach Chrona through
/// their own channels (observations, integrations), never this page.
let principalOf (session: Session) : Access.Principal =
    { PrincipalId = session.ActorId
      Kind = Access.Human
      DisplayName = session.DisplayName }

/// Whether the session's person holds the capability in its organization.
let permits (model: Model) (capability: Access.Capability) =
    Access.permits model.Roster model.Session.ActorId capability

let initial (session: Session) (store: StoreKind) (now: DateTimeOffset) =
    { Place = Places.Today
      RouteProblem = None
      Router = Limen.Routing.Navigation.initial
      Page = { Origin = ""; Path = "/" }
      LinkStatus = ""
      LinkText = ""
      Session = session
      Identity = initialIdentity
      Deployment = None
      Roster = Access.founded session.OrganizationId (principalOf session)
      MemberDraft = emptyMember
      Store =
        { Kind = store
          Pending = []
          Committed = 0
          Problem = None
          Opening = false
          Failure = None
          Integrity = []
          Held = []
          Conflicts = []
          Sync = initialSync
          Audited = 0
          Waiting = 0
          Months = []
          Reading = []
          History = []
          Index = ""
          Confirmation = None
          Cached = None }
      Zone = None
      Now = now
      Ledger = Ledger.empty
      References = Reference.empty session.OrganizationId
      Timer = Timer.Idle
      TickGeneration = 0
      Stopped = None
      LongTimerConfirmed = false
      TimerDraft = emptyClassification
      CompletionDraft = emptyClassification
      Manual = emptyManual
      Detail = None
      MergeSelection = []
      MergeDraft = emptyClassification
      Attestations = []
      AttestStatement = ""
      PeriodConfig = Periods.defaultConfig "UTC"
      Report = emptyReport
      CopyStatus = ""
      NewNames = Map.empty
      Problems = Map.empty
      Announcement = ""
      HistoricalAfterDays = 0
      TimerNote = None
      Shell = { Build = Development; Newer = None } }

/// Whether the person may work: the deployment runs locally, or someone is
/// signed in. Nothing is recorded, shown or stored for anyone else.
let canWork (model: Model) =
    match model.Identity.Mode with
    | LocalOnly
    | SignedInMode ->
        not model.Store.Opening
        && model.Store.Failure.IsNone
        && model.Store.Confirmation.IsNone
        // Only the organization's members work in it.
        && model.Roster.Members.ContainsKey model.Session.ActorId
    | Configuring
    | Misconfigured _
    | SignInRequired _ -> false

/// Today's business date in the model's zone (or UTC before it is known).
let today (model: Model) =
    match model.Zone with
    | Some zone -> (occurrence zone model.Now).LocalDate
    | None -> DateOnly.FromDateTime model.Now.UtcDateTime

/// The date the day, review and month screens show: the one the address
/// names, or today.
let selectedDate (model: Model) =
    match model.Place with
    | Places.Day(on, _)
    | Places.Week(on, _)
    | Places.Period on
    | Places.Review on -> on
    | Places.Month(year, month) -> DateOnly(year, month, 1)
    | _ -> today model

/// The first day of the month the Month screen shows.
let selectedMonth (model: Model) =
    let date = selectedDate model
    DateOnly(date.Year, date.Month, 1)

/// The week that contains a date, by the organization's first day of the week.
let weekOf (model: Model) (date: DateOnly) =
    Periods.containing { model.PeriodConfig with Cadence = Periods.Weekly } date

/// Whether addresses name the organization (CHX-460): someone signed in to a
/// deployment that serves several, so a link opens the same organization.
let namesOrganization (model: Model) =
    model.Identity.Mode = SignedInMode
    && (model.Deployment |> Option.exists (fun config -> config.Organizations.Length > 1))

/// The address of a place in the organization the person works in now.
let addressOf (model: Model) (place: Places.Place) : Places.Address =
    { Place = place
      Organization = if namesOrganization model then Some model.Session.OrganizationId else None }
