/// The organization's records as the store holds them: what was read and at
/// which state, what a write is conditioned on, and how a failure reads for
/// the person.
module internal Chrona.Application.StoreState

open System
open System.Collections.Generic
open Arca
open Arca.GitHub
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.Model
open Chrona.Application.Bridge
open Chrona.Application.Store

/// The activity index as the store keeps it (WI-0034): derived state,
/// written in the same commit as the records it covers.
[<NoComparison; NoEquality>]
type Indexed =
    { /// The index, when it is kept: None until it is built, or once it
      /// could not be kept.
      Index: DerivedIndex option
      /// Its stored revision; None when it is not stored yet.
      Revision: Revision option
      /// Why it is not kept, or where it is known to differ from the records.
      Note: string option }

/// What a write is conditioned on (Arca 0.4.0, ARCA-CON-005): the state of
/// Chrona's own folder, so another application's commit elsewhere in a shared
/// repository moves nothing under it. After a commit whose folder state
/// cannot be observed at exactly the commit (someone committed since), the
/// next write falls back to the repository's state at the commit.
[<NoComparison; NoEquality>]
type Condition =
    | InNamespace of NamespaceState
    | InRepository of ChangeToken

/// The repository's change token a condition was observed at.
let changeTokenOf =
    function
    | InNamespace state -> state.RepositoryToken
    | InRepository token -> token

/// Reads pinned at the state a condition was observed at (WI-0081): what
/// is read after a refresh sees exactly what the refresh read.
let pinnedFor (connection: Connection) (condition: Condition) = pinnedAt connection (changeTokenOf condition)

/// Holds an operation to the condition.
let require (condition: Condition) (operation: Operation) =
    match condition with
    | InNamespace state -> Operation.requireNamespaceToken state.NamespaceToken operation
    | InRepository token -> Operation.requireChangeToken token operation

/// What was read under the condition, for the read cache.
let freshOf (condition: Condition) (objects: StoredObject list) =
    match condition with
    | InNamespace state -> Fresh.read state objects
    | InRepository token -> Fresh.readRepositoryWide token objects

/// What the records shown were read at.
[<NoComparison; NoEquality>]
type Basis =
    /// Read from GitHub at this state of the folder: every write is
    /// conditioned on it.
    | ReadAt of Condition
    /// Shown from the read cache, as GitHub last gave them at this time.
    /// Nothing is conditioned on a cached value (LCP-085): changes made now
    /// are queued and decided again on what GitHub holds before they are
    /// sent.
    | CachedAt of DateTimeOffset

/// The organization's records as opened.
[<NoComparison; NoEquality>]
type Opened =
    { Session: Session
      Folder: Namespace
      Provider: StorageProvider
      /// Reads pinned to a commit, where the provider offers them (WI-0077).
      PinAt: (ChangeToken -> PinnedReads) option
      Name: string
      /// What the records were read at: GitHub's change token (21), or the
      /// read cache while GitHub cannot be reached (WI-0057).
      Basis: Basis
      Stored: Stored.Stored
      /// Each folder's records as GitHub holds them at the basis's token, for
      /// the read cache; empty while shown from the cache.
      Read: Map<string, StoredObject list>
      /// The months read so far.
      Months: Set<int * int>
      /// Other people's months read for reports across people (WI-0039).
      Others: Set<string * (int * int)>
      /// Each activity record's history as last read, with the revision it
      /// was read at: a record whose revision is unchanged is not asked
      /// about again for this page's life (WI-0077).
      Histories: Map<string, Revision * HistoryEntry list>
      /// The organization as the deployment configures it.
      Organization: Deployment.OrganizationConfig
      /// This page's unsent changes, in order: Arca's offline queue (WI-0033).
      Queue: OfflineQueue
      /// The queue this tab holds in this browser, when it holds one it can
      /// keep (WI-0059: Arca's LimenQueue, in IndexedDB where it can be).
      Owned: Arca.Limen.OwnedQueue option
      /// Which tab holds this browser's queue (WI-0067).
      Holder: QueueHolder
      /// Why the queue is not kept in this browser, for the person.
      Note: string option
      /// What the person must be told about the kept changes (LCP-062, LCP-066).
      Notice: string option
      /// The activity index as last read or written (WI-0034).
      Index: Indexed }

let describeFailure =
    function
    | StorageFailure.Refused(WriteRefusal.CredentialUnavailable _) -> "You are not signed in to GitHub any more. Sign in again; nothing was saved."
    | StorageFailure.Refused WriteRefusal.ReadOnlyAccess -> "Your GitHub account can read this repository but not write to it."
    | StorageFailure.Refused WriteRefusal.BranchProtected -> "The data branch does not accept direct changes."
    | StorageFailure.Refused WriteRefusal.RepositoryArchived -> "The data repository is archived."
    | StorageFailure.Refused(WriteRefusal.RepositoryIdentityChanged _) -> "The configured repository is now a different repository."
    | StorageFailure.Refused(WriteRefusal.CapabilityUnavailable _) -> "The store cannot make this change."
    | StorageFailure.Conflicted _ -> "Something changed elsewhere."
    | StorageFailure.OutcomeUnknown _ -> "GitHub did not say whether the change was saved."
    | StorageFailure.ObjectTooLarge(path, _, _) -> $"{path} is too large to store."
    | StorageFailure.StaleChangeToken _ -> "The repository changed while saving."
    | StorageFailure.StaleNamespaceToken _ -> "The organization's records changed while saving."
    | StorageFailure.RateLimited _ -> "GitHub's rate limit is used up for now. Try again shortly."
    | StorageFailure.WrongLocation _ -> "The store is not serving the configured repository."
    | StorageFailure.IntegrityRefused(path, _) -> $"{path} could not be changed safely: it is not a valid record."
    | StorageFailure.ProviderFailed(_, _, detail) -> $"GitHub could not be reached ({detail})."

/// A failure to read, as a reason the records could not be opened.
let unopened (failure: StorageFailure) =
    match failure with
    | StorageFailure.ProviderFailed(_, true, _)
    | StorageFailure.RateLimited _ -> Unopened.Unreachable(describeFailure failure)
    | _ -> Unopened.Refused(describeFailure failure)

let describeAll (diagnostics: Diagnostic list) =
    diagnostics |> List.map code |> List.distinct |> String.concat ", "

let operationContext (session: Session) (key: string) (at: DateTimeOffset) : Result<Storage.OperationContext, Diagnostic list> =
    match ActorId.create session.ActorId, CorrelationId.create key, IdempotencyKey.create key with
    | Ok actor, Ok correlation, Ok idempotency ->
        Ok
            { Actor = { Kind = ActorKind.Human; Id = actor }
              ProviderIdentity = Some session.DisplayName
              CorrelationId = correlation
              IdempotencyKey = idempotency
              At = at }
    | _ -> Error [ StorageOperationRefused "the session's actor or the change's id is not a valid identifier" ]

let monthOf (date: DateOnly) = date.Year, date.Month
let firstOf (year: int, month: int) = DateOnly(year, month, 1)

/// The records a request asks to store.
let changeOf (request: StoreRequest) : Stored.Changed =
    { Activities = request.Activities
      References = request.References
      Attestations = request.Attestations
      Members = request.Members
      Removed = request.RemovedMembers
      Audit = request.Audit
      Periods = request.Periods
      Reviews = request.Reviews
      Candidates = request.Candidates
      Receipts = []
      Consumed = [] }

let isEmpty (changed: Stored.Changed) =
    changed.Activities.IsEmpty
    && changed.References.IsEmpty
    && changed.Attestations.IsEmpty
    && changed.Members.IsEmpty
    && changed.Removed.IsEmpty
    && changed.Audit.IsEmpty
    && changed.Periods.IsNone
    && changed.Reviews.IsEmpty
    && changed.Candidates.IsEmpty
    && changed.Receipts.IsEmpty
    && changed.Consumed.IsEmpty
