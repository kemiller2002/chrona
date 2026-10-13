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
///
/// The ports' data is in `ModelPorts`, the forms' in `ModelForms`; this
/// module holds the model itself and what is read from it.
module Chrona.Engine.App.Model

open System
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Domain

open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms

/// The build of a page that was not built for a deployment: it is never
/// compared with what is deployed.
[<Literal>]
let Development = "development"

/// The page's own address (its origin and path), for a link that opens a
/// place from anywhere. Never its query: a sign-in callback's code and state
/// were there.
type PageAddress = { Origin: string; Path: string }

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
    /// Submitting, approving, returning or reopening a period (WI-0036).
    | ReviewForm
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
    /// Accepting or rejecting an observation's candidate (WI-0038).
    | CandidateForm

/// Unsent changes from an earlier version, naming no one: what the person
/// chose to do with them.
type EarlierDecision =
    | Undecided
    /// Left as they are, in this browser.
    | KeptEarlier
    /// Asked to discard them; waiting for the confirmation.
    | ConfirmingEarlierDiscard

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
      /// The organization's timesheet periods (15), as edited under More.
      PeriodConfig: Periods.PeriodConfig
      /// The period configuration as stored, when it was ever saved
      /// (WI-0036); what the organization's periods follow.
      PeriodsStored: PeriodConfigRecord.StoredPeriods option
      /// Periods' review steps (WI-0036): this person's, and everyone's for
      /// someone who approves.
      Reviews: PeriodReview.PeriodReview list
      /// Other people's time that was read: what waits for this person's
      /// approval (WI-0036), and the months a report across everyone read
      /// (WI-0039).
      Reviewing: Activity list
      /// The deployment's other organizations read for a report across
      /// organizations, by id (WI-0075).
      Elsewhere: Map<string, Elsewhere>
      /// Other organizations asked for and not read yet.
      ElsewhereReading: string list
      /// What the approver typed for each submission: a note to approve with,
      /// or the reason to return it.
      ReviewNotes: Map<string, string>
      /// Why the person reopens their period.
      ReopenReason: string
      /// Observations' candidates as stored (WI-0038).
      Candidates: Observations.Candidate list
      /// Where reading producers' inboxes stands.
      Inbox: InboxStatus
      /// The candidate on screen as the person classifies it to accept it:
      /// opened from what the observation proposes when its page is entered.
      CandidateDraft: ClassificationDraft
      /// Which candidate the draft is for.
      CandidateDraftFor: string
      /// Why the candidate on screen is rejected.
      CandidateReason: string
      /// What the person decided about unsent changes from an earlier
      /// version that name no one (Arca 0.4.0).
      Earlier: EarlierDecision
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
          Others = []
          OthersReading = []
          History = []
          Index = ""
          IndexCurrent = false
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
      PeriodsStored = None
      Reviews = []
      Reviewing = []
      Elsewhere = Map.empty
      ElsewhereReading = []
      ReviewNotes = Map.empty
      ReopenReason = ""
      Candidates = []
      Inbox = idleInbox
      CandidateDraft = emptyClassification
      CandidateDraftFor = ""
      CandidateReason = ""
      Earlier = Undecided
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

/// The candidates a person sees (WI-0038): those observed of them, and those
/// that name no one, which whoever accepts them makes their own.
let visibleCandidates (model: Model) =
    model.Candidates
    |> List.filter (fun candidate -> candidate.Observation.ActorId |> Option.forall ((=) model.Session.ActorId))

/// The business date an observation's work happened on, in the
/// organization's zone when it is known.
let observedDate (model: Model) (observation: Observations.Observation) =
    let at =
        match observation.Timing with
        | Observations.ObservedInterval(start, _) -> start
        | Observations.ObservedDuration _ -> observation.ObservedAt

    match model.Zone with
    | Some zone -> (occurrence zone at).LocalDate
    | None -> DateOnly.FromDateTime at.UtcDateTime
