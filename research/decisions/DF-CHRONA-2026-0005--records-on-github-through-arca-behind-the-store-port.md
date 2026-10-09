---
id: DF-CHRONA-2026-0005
title: Records live on GitHub through Arca's provider behind the store port, every commit conditioned on the repository state and decided again when it moved
status: accepted
version: 1.12.0
created: 2026-10-08
updated: 2026-10-09
owners:
  - chrona
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - research/decisions/DF-CHRONA-2026-0003--product-ui-on-limen-in-memory-with-identity-and-store-ports.md
  - research/decisions/DF-CHRONA-2026-0004--sign-in-through-fides-client-over-limen-requests.md
  - docs/deployment-configuration.md
tags: [storage, arca, github, concurrency, architecture]
derived_from: [DF-CHRONA-2026-0003, DF-CHRONA-2026-0004]
provenance:
  contributions:
    EXE-20261008T125405928Z-c10c8445:
      operations: [created]
      at: 2026-10-08T13:16:03.255Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record how records are stored on GitHub through Arca behind the store port (WI-0032)"
    EXE-20261008T134532885Z-23ecf533:
      operations: [modified]
      at: 2026-10-08T13:50:51.345Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Bootstrap administrators and the dedicated-repository recommendation (WI-0053)"
    EXE-20261008T140312075Z-5d2f78ca:
      operations: [modified]
      at: 2026-10-08T14:15:53.200Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Conflict resolution and acceptance of outside edits (WI-0035)"
    EXE-20261008T142708182Z-504eba9b:
      operations: [modified]
      at: 2026-10-08T14:37:57.174Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "The queue of unsent changes (WI-0033)"
    EXE-20261008T144745237Z-69fb3f15:
      operations: [modified]
      at: 2026-10-08T14:59:43.591Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Derived state: the activity index (WI-0034)"
    EXE-20261008T150927995Z-a5f4f4f2:
      operations: [modified]
      at: 2026-10-08T15:15:09.824Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Shared-device sign-out (WI-0058) and the offline read cache plan (WI-0057)"
    EXE-20261008T152358903Z-a0425e9f:
      operations: [modified]
      at: 2026-10-08T15:30:15.541Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "The audit trail as records (WI-0056)"
    EXE-20261008T155702353Z-04d86325:
      operations: [modified]
      at: 2026-10-08T16:19:22.339Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "The device's timer (WI-0055)"
    EXE-20261008T172418702Z-f7fee975:
      operations: [modified]
      at: 2026-10-08T17:24:25.926Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "One tab holds the queue (WI-0067)"
    EXE-20261008T222827697Z-bac96650:
      operations: [modified]
      at: 2026-10-08T23:30:00.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "The queue in IndexedDB through Arca.Limen (WI-0059)"
    EXE-20261008T235949355Z-28e2b305:
      operations: [modified]
      at: 2026-10-09T01:30:00.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Opening offline from the read cache (WI-0057)"
    EXE-20261009T012552338Z-e58b9a1f:
      operations: [modified]
      at: 2026-10-09T03:00:00.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Periods under review on Arca (WI-0036)"
    EXE-20261009T023709515Z-21115e9f:
      operations: [modified]
      at: 2026-10-09T04:00:00.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Observation inboxes, candidates and receipts (WI-0038)"
    EXE-20261009T023556300Z-7aa2d7d5:
      operations: [modified]
      at: 2026-10-09T05:00:00.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Arca 0.4.0: namespace-scoped conditions and sign-out by account id (WI-0073)"
---

# DF-CHRONA-2026-0005 — Records on GitHub through Arca

- **Date:** 2026-10-08
- **Status:** accepted
- **Work items:** WI-0028, WI-0051, WI-0032, WI-0053, WI-0035, WI-0033, WI-0034, WI-0058, WI-0056, WI-0055, WI-0067, WI-0059, WI-0057, WI-0036

## Context

The store port (DF-CHRONA-2026-0003) was answered in memory. Arca 0.2.0
offers a provider-neutral `StorageProvider`, a GitHub adapter whose every
request is data (driven through a host), an in-memory provider that passes
Arca's conformance suite, and the record, layout, integrity and history
primitives. Sign-in runs Fides' client over Limen requests
(DF-CHRONA-2026-0004). Requirement 21 asks for optimistic concurrency on
repository state, with reload, revalidation, independent changes preserved
and semantic conflicts surfaced.

## Decision

1. **One provider-neutral path.** `Chrona.Application.Store` runs Arca's
   `StorageProvider`. In the browser it is Arca's GitHub adapter, whose
   requests go through the same bridge as Fides' (Limen Http, with the
   token from Fides' token provider added at request time; waits as
   `limen.schedule` timeouts). In tests it is Arca's in-memory provider.
   The domain owns the rules (`Storage`, `Organization`, `Stored`,
   `Persistence`); the store only sequences them, one job at a time.
2. **Opening.** After sign-in, when the deployment configures a location:
   resolve the repository with the person's credential (visibility,
   access, archived, branch rules); refuse production in a public
   repository; set up Chrona's and the organization's folders when new;
   open them only when their manifests match; read the reference folders
   and the month folders the current period needs; validate everything;
   hold records edited outside Chrona. Nothing can be done until this
   finishes, and a failure is shown with a retry.
3. **Committing.** A command's records become one Arca operation, one
   commit, each change conditioned on the revision last read and the whole
   commit on the change token the records were read at. When the
   repository moved, the store reloads and decides again: a changed record
   must be the next revision of what is now stored, and new time must not
   overlap stored time. If it still fits it is written (others'
   independent changes survive and are then shown); otherwise it is
   refused as a conflict and the stored records are shown. An unknown
   outcome is reconciled before anything is sent again.
6. **Resolving a conflict (WI-0035).** Deciding again is the domain's
   (`Reconcile.decide`); what no longer fits comes back as divergences,
   each with the person's version, what is stored now and a stable code.
   The engine keeps the change as a conflict under More and as an
   obligation until the person decides: keep what is stored (their change
   is set aside, knowingly), redo theirs on the current version through
   the usual form and its rules (a correction on the current revision, or
   new time back in the manual form), or try again when the repository
   only kept moving. Nothing is resolved by last-write-wins and neither
   side is dropped silently.
7. **Accepting an outside edit (WI-0035).** A person reviews a held record
   of theirs under More and accepts it. It must pass the rules of a
   recorded activity and may not claim a review, publication or superseded
   state, because those come only from Chrona's own transitions and their
   records. It is written as Chrona's next revision, so its newest commit
   is Chrona's and later loads trust it. The store releases it only if it
   is still stored as reviewed.
8. **The queue of unsent changes (WI-0033).** A decided change becomes an
   Arca operation conditioned on the state it was decided on, and is put
   in Arca's `OfflineQueue` before it is sent, kept in this browser's
   localStorage by Arca's `LocalStorageQueue` over Limen Storage requests
   (an IndexedDB store waits on Limen). Chrona drives the queue's pure
   transitions itself rather than `OfflineSync`, because it needs each
   commit's receipt to keep its revisions current. Entries go one at a
   time, in order; each step is kept before and after it is taken. A lost
   connection leaves the entry waiting and retries with back-off (5 s to
   60 s). An unknown outcome is reconciled before anything else. An entry
   the repository moved under (including every entry queued behind
   another) is read back (`Stored.changedOf`), decided again on what is
   stored now and revised, or abandoned as a conflict for the person. New
   changes are decided on what is stored with the unsent ones laid over it
   (`Stored.overlay`), which is also what the person sees. A change to a
   month not read yet is refused while GitHub is out of reach, because it
   could not be checked.
9. **Whose queue.** The queue is kept per organization folder in this
   browser. If it holds another account's unsent changes, they are left
   untouched for that account and this session's queue is kept in the page
   only, with a note. They are never sent with another person's credential.
10. **Derived state (WI-0034).** The activity index is an Arca derived
    index (`ActivityIndex.definition`, stored at
    `derived/indexes/activity-months.json`) with one entry per activity
    record: its path, content hash, actor, month, minutes and states.
    Because each entry carries its record's path and hash, the index's
    source set follows from the entries, so Chrona keeps it current by
    applying each commit's changes (`ActivityIndex.apply`) and writing it in
    the same commit; the result is exactly what `Derived.rebuild` makes.
    The store reads it when the records open (one read), and checks it
    against every month folder it reads: where they disagree (an outside
    edit), the page says the index is out of date. An administrator
    rebuilds it from every record under More (`Derived.rebuild`, which
    reads the whole folder): the recovery path, and how an organization
    from before the index gets one. Past 512,000 characters the index is no
    longer written with each change and says so; rebuilding still works.
    The page lists each month that holds the person's time from the index,
    and reads a month folder only when the person goes to it.
11. **The audit trail (WI-0056).** Each command's audit entries travel with
    its records and become immutable `chrona.audit` records
    (`records/chrona.audit/<person>/<yyyy>/<MM>/<instant>-<digest>.json`),
    kept with the activities they concern and written in the same commit.
    The id is the entry's instant and a digest of its content, so the same
    fact is never written twice. A change refused as a conflict writes no
    entry; one decided again carries its entries. The activity screen's
    history is read from them; revisions from before say so.
12. **The device's timer (WI-0055).** The active timer, or a stopped one
    whose time is held until completed, is kept in this browser's storage
    under `chrona.timer.<organization>.<person>` (`TimerRecord`): device
    state, never a record and never an authority, written whenever it
    changes and recovered explicitly at startup. It keeps working when the
    records cannot be opened. Sign-out counts it as unsent work under the
    shared-device policy. A running timer that overlaps time recorded since
    it started is an obligation (`CHRONA.TIMER.CONCURRENT_CONFLICT`).
13. **One tab holds the queue (WI-0067).** Before it loads, keeps or sends
    anything kept in this browser, a tab takes the queue through Arca's
    `LocalStorageQueue.own`: an exclusive Web Lock named for the
    organization's folder, through Limen's coordination pack, never stolen
    and held until the page goes. The owner keeps, reads back and sends the
    queue, as in 8. Another tab (`OwnedElsewhere`) never reads or writes
    the kept queue: it says that another tab is holding the unsent changes,
    keeps its own changes in the page and sends them directly while GitHub
    can be reached (OQ-001), and offers to take over. Taking over waits for
    the lock, which passes when the owner closes, crashes or navigates
    away; the new owner reads back what that tab kept, puts its own unsent
    changes after it, and sends them in order. An entry that tab was sending
    becomes an outcome to reconcile, so nothing is sent twice. Where the
    browser has no Web Locks (`OwnershipUnsupported`), each tab uses Arca's
    fenced store, and the page shows that mode. A save refused because
    another tab saved first is described as exactly that. A lost lock (only
    another context's steal) leaves the tab working from the page. The
    IndexedDB queue's owner replaced this lock in WI-0059 (14).
14. **The queue in IndexedDB (WI-0059, Arca 0.3.0, Limen 0.9.0).** The
    queue is opened through Arca's `LimenQueue.own` over a `LimenHost`
    whose Store is Limen's store pack (version 2, registered by the page as
    `storeCapability({ namespace: "chrona" })`), whose Lock is the
    coordination pack, whose LocalStorage is the Storage effect, and whose
    clock is the page's. Both packs' requests and results cross the
    boundary as Limen's own serialized contract values. Arca tries
    IndexedDB, then localStorage, then the page's memory
    (`QueueOptions.standard`), and Chrona shows where the changes are kept
    ("Kept in this browser (IndexedDB) by this tab"); kept only in the
    page, it says so before a change is made offline. One tab holds the
    queue; another answers `OwnedElsewhere`, keeps its own changes in the
    page and sends them while GitHub can be reached, and offers "Use this
    tab instead" (`LimenQueue.takeOver`), which takes the queue at once.
    The tab that lost it hears `LockLost`, or finds its next save fenced
    (it wrote nothing): what it kept is the new holder's to send or
    reconcile, never sent from both (`UnsentHandedOver`), and only what it
    added since stays in its page. When the holder closes, the other tab
    asks again when the person returns to it. Without Web Locks no durable
    store is written by several tabs: each keeps its changes in its page.
    The first load moves a queue an older Chrona kept in localStorage into
    IndexedDB (copied, verified, then removed); a move interrupted after
    the copy finishes on the next load without sending anything twice; a
    localStorage queue found while IndexedDB holds unsent changes waits
    and is said; one that cannot be read is left exactly as it is and is
    said. These notices are shown with the sync state, and on the screen
    where the records could not be opened. When the browser closes the
    database under the page (`ConnectionLost`), the queue is opened again
    and this tab's queue is kept there again. Sign-out discards only the
    account's pending, conflicted or refused entries, by Chrona's actor id
    (never a display name), and never one that may have landed (Arca's
    LCP-070 rule).
15. **The read cache (WI-0057, Arca 0.3.0's `IndexedDbReadCache`, Limen
    LCP-082..087).** Every read from GitHub is kept, best-effort, in this
    browser's IndexedDB: one entry per folder (keyed by the account's actor
    id, the organization's namespace and the folder), a second per folder
    listing the records held for review, and the activity index, each
    stamped with the change token it was read at. A commit this page makes
    updates the folders it touched at the receipt's token, which is exact
    because every commit is conditioned on the whole repository's token.
    When GitHub cannot be reached at the start (never when it refuses),
    the records open from the cache: the organization's people and
    reference data must be there, a month only when all of its folders
    are, and the records held for review are held again. They are shown
    "as of" when GitHub last gave them, never as current, and the sync
    state is offline. Nothing is conditioned on a cached value (LCP-085):
    a change made there is queued with no change token, and before it is
    sent, once the records are read from GitHub again, it is decided again
    like a change the repository moved under (sent once, or a visible
    conflict). GitHub is tried again after a back-off, when the browser
    is back online, or when the person returns to the page. The queue
    frees the cache's space when a save meets the quota (LCP-087).
    Signing out follows Arca's `SignOut.plan` under the deployment's
    shared-device policy: the account's cache is cleared, except under
    `ask` when the person keeps unsent work here, so the account opens
    offline and sees it in context. "Clear this device" (More) runs
    `LimenDevice.clear`, removing the queue and the cache for every
    account, then signs out; it is offered only when nothing of this
    account's is unsent, and refused while another account's unsent
    changes are kept here or another tab holds the databases.
4. **Bounded reads.** A change to a month not yet read reads that month
   first, so it is never checked against less than what is stored.
5. **The organization.** A storing deployment lists the organizations it
   serves; a signed-in session works in one (WI-0031). Only the accounts the
   configuration lists set an organization up or become its first
   administrators (WI-0053); one without a listed administrator is held
   until a listed account confirms.

16. **Periods under review (WI-0036).** The organization's period
    configuration is `records/chrona.configuration/periods.json`, mutable
    under its revision (a counter in the record): cadence (with a biweekly
    anchor), week start, whether time is submitted and whether submitted
    time is approved. The time zone is the organization's and is not
    stored there. Absent, the defaults apply. Saving it is the next
    revision; a save made from a stale read is a conflict
    (`PeriodsChanged`), never an overwrite. Each step of a person's period
    review (submission, approval, rejection, reopening) is an immutable
    `records/chrona.review/<person>/<yyyy>/<MM>/<kind>-<start>-<instant>.json`,
    filed under the month the period starts, and read with that month and
    the next. The activities' review states change with the step, without a
    new content revision (WI-0023); deciding a commit again accepts such a
    change when only the review state differs and the step is legal from
    the state stored now. The period's state is its last step: open,
    waiting for approval, closed (submitted without approval, or approved),
    or returned. While a period waits or is closed, its time is not added
    to, changed, moved or attested (`CHRONA.REVIEW.SUBMITTED_PERIOD`) until
    it is reopened with a reason: by its person while it waits, otherwise
    by someone who may reopen time. Someone who may approve or reject time
    also reads, where approval is required, the other members' review
    steps for the months open and the time their waiting submissions
    cover; that time is shown only for review, never mixed into their own.

17. **Observation inboxes (WI-0038).** A producer writes one observation
    as `inbox/<sourceSystem>/<observationId>.json` inside the
    organization's folder, in the receiver-owned contract
    `chrona.time-observation` version 1 (`Chrona.Integration`): every field
    stated (absent optional ones as null), instants with their offset, an
    interval or a number of minutes, at most 64 KiB; an unknown field, an
    unknown version or a payload filed under another source or id is
    refused with every reason, never guessed. Each time the records open
    from GitHub (and when the person asks, under More), Chrona reads the
    inboxes, at most 20 files a pass in path order, the next pass following
    at once, and only while nothing of the page's own waits to be sent. An
    observation becomes a candidate (`records/chrona.candidate/open/<id>.json`)
    with its receipt (`records/chrona.receipt/<source>/<id>.json`) and the
    file's removal, all in **one** commit conditioned on the records as
    read: the candidate is never without its receipt, nor the receipt
    without the candidate, which is stronger than the domain's
    candidate-then-receipt order; a candidate found without its receipt
    (written by another tool) is still repaired with the receipt alone. A
    payload that is not an observation for this organization gets a receipt
    with the reasons, and its file stays beside it for the producer. A
    stale token reads the records again and decides the file again, once.
    Receipts are immutable. A candidate is mutable under its revision (a
    counter in the record): accepting or rejecting it moves it to
    `records/chrona.candidate/decided/<yyyy>/<MM>/<id>.json` (the month of
    its decision) in the same commit as the activity it made and its audit
    entries; a decision made from a stale read is a conflict
    (`CandidateChanged`). Open candidates are read with the organization's
    common folders, decided ones with each month read; duplicates are
    therefore found among open candidates and those decided in the months
    read. A person sees the candidates observed of them and those that name
    no one; accepting needs `RecordOwnTime`, a classification (activity
    type, project, description, business purpose) and passes every ordinary
    rule, including the submitted-period restriction. Accepted time is
    `Imported <source>`, billability pending a person's decision.

18. **Arca 0.4.0 (WI-0073).** Every write is conditioned on the state of
    the organization's folder (`provider.NamespaceState`,
    `Operation.requireNamespaceToken`; the read cache is kept with
    `Fresh.read` of that state), so another application's commit elsewhere
    in a shared repository no longer makes Chrona decide again; a change
    inside the folder (`StaleNamespaceToken`) is decided again as before.
    After a commit, the folder's state is observed again: when GitHub's head
    is still that commit it is the next condition; otherwise someone
    committed since, and the next write is held to the repository at the
    commit (`requireChangeToken`), never to a folder state Chrona did not
    read. Queued changes name their account (`OfflineQueue.enqueueFor` with
    `AccountId.ofActor` of Chrona's actor id, "github:<numeric id>"), and
    sign-out counts and discards by it (`QueueSignOut.unsentOfAccount`,
    `OwnedQueue.DiscardAccount`, `Legacy = None`), never by a display name;
    an entry that may have landed is still never discarded. An entry queued
    before 0.4.0 is attributed to the actor it records itself when the
    queue loads, never to whoever is signed in. One that records no usable
    actor is neither sent nor discarded: the page says unsent changes from
    an earlier version are here, and the person sends them as theirs, keeps
    them, or discards them after a confirmation. Arca 0.4.0's
    `OfflineQueue.revise` drops the account id; Chrona stamps it again
    until Arca 0.4.1. Do not go back below Arca 0.4.0 while conditioned
    changes are queued: an older Arca would read them without their
    condition. Chrona erases nothing; an erased record (Arca's tombstone) is
    read as gone, and an erased receipt as received.

## Consequences

- Until Arca 0.4.0, any commit to the repository (including other
  applications' in a shared repository) made the next Chrona commit decide
  again: correctness over fewer reloads, decided on 2026-10-08. From
  WI-0073, only a change inside the organization's folder does (item 18).
- An outside edit that claims a state only Chrona gives stays held: it is
  repaired in the repository, not accepted in the application.
- Manual-edit detection reads each loaded activity's history; derived
  indexes (WI-0034) can make that cheaper.
- The queue holds operations, never record state, so it is not a
  competing authority: records are read only from GitHub.
- The index is one file for the organization, read by every member with
  access to the repository (as the records already are); the page shows
  each person only their own months. Rebuilding reads every record, which
  is costly on a large organization; it is an explicit administrator
  action, never automatic.
- Unsent changes include record content, kept in this browser's
  localStorage until sent. Signing out with unsent changes asks the person
  to send them now, keep them on this device for this account, or discard
  them after a confirmation naming how many (WI-0058, decision of
  2026-10-08); a deployment's `sharedDevicePolicy: "discardOnSignOut"`
  withdraws the keep option for shared computers. Nothing is lost silently
  and nothing is left behind unknowingly.
- Two tabs never overwrite each other's unsent changes, and never both send
  the same ones: one holds them at a time, and the hand-off on close
  reconciles before sending.
- Opening the records needs GitHub. Starting offline will come from a
  read-only, rebuildable cache in IndexedDB once Limen offers it (WI-0057);
  never from localStorage.

## Revisit when

Arca publishes a Limen-native driver, a cheaper external-edit signal, or
Fides/GitHub App tokens change what a commit can be attributed to.

Arca's `OwnedQueue.Discard` and `QueueSignOut` match an entry to an account
by its provider identity, falling back to its actor. Chrona records the
person's display name as the provider identity, and two accounts can share
one, so Chrona discards at sign-out by its own rule (14): by actor id, the
stable identifier, keeping Arca's rule that an entry which may have landed
is never discarded. Suggested to Arca: match accounts by a stable id (the
actor id, or a provider subject), never by a display name. Arca 0.4.0 does
(ARCA-OFF-007), and Chrona moved to `OwnedQueue.DiscardAccount` (item 18).
