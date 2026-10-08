---
id: DF-CHRONA-2026-0005
title: Records live on GitHub through Arca's provider behind the store port, every commit conditioned on the repository state and decided again when it moved
status: accepted
version: 1.0.0
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
---

# DF-CHRONA-2026-0005 — Records on GitHub through Arca

- **Date:** 2026-10-08
- **Status:** accepted
- **Work items:** WI-0028, WI-0051, WI-0032

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
4. **Bounded reads.** A change to a month not yet read reads that month
   first, so it is never checked against less than what is stored.
5. **The organization.** A storing deployment names the organization it
   serves; a signed-in session works in it. Choosing between several
   organizations, and storing rosters, is WI-0031.

## Consequences

- Any commit to the repository (including other applications' in a shared
  repository) makes the next Chrona commit decide again. That is the
  requirement's choice: correctness over fewer reloads. A repository of
  its own avoids the extra reloads.
- Manual-edit detection reads each loaded activity's history; derived
  indexes (WI-0034) can make that cheaper.
- Offline durability (WI-0033) adds Arca's offline queue behind the same
  port.

## Revisit when

Arca publishes a Limen-native driver, a cheaper external-edit signal, or
Fides/GitHub App tokens change what a commit can be attributed to.
