/// The Chrona application's state: plain, immutable data. Pure.
///
/// Two ports keep the later slices pluggable (DF-CHRONA-2026-0003):
/// - **Identity.** `Session` says who is working. Today it is a local
///   session; Fides sign-in (WI-0029) supplies a signed-in one.
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
      References: Reference.Item list }

type StoreOutcome =
    | Committed
    | Conflict of detail: string
    | Failed of detail: string
    /// The store cannot tell whether the write took effect (21, 31).
    | OutcomeUnknown of detail: string

type StoreKind =
    /// Kept in this tab's memory only; gone when the tab closes.
    | InMemory
    | Durable of name: string

type StoreState =
    { Kind: StoreKind
      /// Commits sent and not yet answered.
      Pending: string list
      Committed: int
      /// The last answer that was not `Committed`, until the next success.
      Problem: StoreOutcome option }

type Screen =
    | Today
    | Track
    | More

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

/// Where a refusal is shown.
type Form =
    | TimerForm
    | CompletionForm
    | ManualForm
    | ReferenceForm

[<NoComparison>]
type Model =
    { Route: Route
      Session: Session
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
      NewNames: Map<Reference.Kind, string>
      Problems: Map<Form, Diagnostic list>
      /// Polite, one-off status text for screen readers: transitions only,
      /// never timer ticks (35).
      Announcement: string
      /// Entries for dates more than this many days before today need a
      /// reason. The legacy rule is 0: any date that is not today.
      HistoricalAfterDays: int }

let initial (session: Session) (store: StoreKind) (now: DateTimeOffset) =
    { Route = { Screen = Today; Date = None }
      Session = session
      Store =
        { Kind = store
          Pending = []
          Committed = 0
          Problem = None }
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
      NewNames = Map.empty
      Problems = Map.empty
      Announcement = ""
      HistoricalAfterDays = 0 }

/// Today's business date in the model's zone (or UTC before it is known).
let today (model: Model) =
    match model.Zone with
    | Some zone -> (occurrence zone model.Now).LocalDate
    | None -> DateOnly.FromDateTime model.Now.UtcDateTime

/// The date the Today screen shows.
let selectedDate (model: Model) = model.Route.Date |> Option.defaultWith (fun () -> today model)
