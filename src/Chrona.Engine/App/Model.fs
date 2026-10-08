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
      Accepted: Activity list }

/// A request with nothing in it yet.
let emptyRequest (commitId: string) =
    { CommitId = commitId
      Activities = []
      References = []
      Attestations = []
      Members = []
      RemovedMembers = []
      Accepted = [] }

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
      Note: string option }

let initialSync =
    { Offline = false
      KeptInBrowser = true
      Note = None }

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
      /// The organization has no administrator the deployment lists: why,
      /// and whether this person, being listed, may confirm themselves.
      Confirmation: (string * bool) option }

/// What the store read from the organization's folder.
type StoreContents =
    { /// Where the records live, for the person: `owner/repository`.
      Name: string
      Activities: Activity list
      References: Reference.Item list
      Attestations: Review.Attestation list
      /// The organization's roster as stored.
      Members: Access.Membership list
      /// Records edited outside Chrona, held for review (41).
      Held: Activity list
      Problems: Diagnostic list }

type Screen =
    | Today
    | Track
    | Month
    | More
    /// One activity's detail: amend, void, restore, split, evidence, history.
    | ActivityDetail of activityId: string
    /// One day's review and attestation.
    | DayReview
    /// Search, reports and export.
    | ReportsScreen

type Route = { Screen: Screen; Date: DateOnly option }

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

type IdentityState =
    { Mode: IdentityMode
      Retention: Retention
      /// The code of the last sign-in outcome to tell the person about, for
      /// example `state_expired` or `signed_out`.
      Notice: string option
      /// The provider's callback parameters this page was opened with, until
      /// the configuration is read and the sign-in can be completed.
      Callback: (string * string) list }

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
      Retention = ThisPage
      Notice = None
      Callback = [] }

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

[<NoComparison>]
type Model =
    { Route: Route
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
      HistoricalAfterDays: int }

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
    { Route = { Screen = Today; Date = None }
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
          Confirmation = None }
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
      HistoricalAfterDays = 0 }

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

/// The date the Today and review screens show.
let selectedDate (model: Model) = model.Route.Date |> Option.defaultWith (fun () -> today model)

/// The first day of the month the Month screen shows.
let selectedMonth (model: Model) =
    let date = selectedDate model
    DateOnly(date.Year, date.Month, 1)
