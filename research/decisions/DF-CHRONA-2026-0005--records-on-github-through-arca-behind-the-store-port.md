---
id: DF-CHRONA-2026-0005
title: Records live on GitHub through Arca's provider behind the store port, every commit conditioned on the repository state and decided again when it moved
status: accepted
version: 1.2.0
created: 2026-10-08
updated: 2026-10-08
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
---

# DF-CHRONA-2026-0005 — Records on GitHub through Arca

- **Date:** 2026-10-08
- **Status:** accepted
- **Work items:** WI-0028, WI-0051, WI-0032, WI-0053, WI-0035, WI-0033

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
4. **Bounded reads.** A change to a month not yet read reads that month
   first, so it is never checked against less than what is stored.
5. **The organization.** A storing deployment lists the organizations it
   serves; a signed-in session works in one (WI-0031). Only the accounts the
   configuration lists set an organization up or become its first
   administrators (WI-0053); one without a listed administrator is held
   until a listed account confirms.

## Consequences

- Any commit to the repository (including other applications' in a shared
  repository) makes the next Chrona commit decide again. That is the
  requirement's choice: correctness over fewer reloads, kept by decision
  on 2026-10-08. A repository of Chrona's own is the recommended setup and
  avoids the extra reloads; the check is not narrowed to Chrona's own
  paths unless Arca supports that safely. Arca's backlog has WI-0018 for
  namespace-scoped change tokens.
- An outside edit that claims a state only Chrona gives stays held: it is
  repaired in the repository, not accepted in the application.
- Manual-edit detection reads each loaded activity's history; derived
  indexes (WI-0034) can make that cheaper.
- The queue holds operations, never record state, so it is not a
  competing authority: records are read only from GitHub, and opening them
  needs a connection. Starting offline is WI-0055.
- Unsent changes include record content, kept in this browser's
  localStorage until sent; signing out does not discard them, because
  nothing is ever dropped silently. A shared computer keeps them for their
  account.

## Revisit when

Arca publishes a Limen-native driver, a cheaper external-edit signal, or
Fides/GitHub App tokens change what a commit can be attributed to.
