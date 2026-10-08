---
id: DF-CHRONA-2026-0002
title: Chrona is reconstructed, not migrated; the legacy rules are reconciled, legacy data needs no migration today, and legacy names resolve through explicit aliases
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
  - research/decisions/DF-CHRONA-2026-0001--reconstruct-on-limen-forma-keeping-legacy-look-and-feel.md
  - research/evidence/EV-CHRONA-2026-0001--legacy-look-and-feel-inventory.md
  - docs/legacy/look-and-feel-inventory.md
  - docs/requirements/CHRONA-REQUIREMENTS-EXPANSION.txt
  - docs/requirements/implementation-gap-analysis.md
tags: [legacy, reconstruction, migration, compatibility, requirements]
provenance:
  contributions:
    EXE-20261008T084801800Z-78e42f18:
      operations: [created]
      at: 2026-10-08T08:50:50.149Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record the reconstruction decision, legacy rule reconciliation, data status and source compatibility"
derived_from: [DF-CHRONA-2026-0001, EV-CHRONA-2026-0001]
---

# DF-CHRONA-2026-0002 — Reconstruct, do not migrate

- **Date:** 2026-10-08
- **Status:** accepted (user decision of 2026-10-08, recorded in
  DF-CHRONA-2026-0001 decision 2; this record makes it operational)
- **Work item:** WI-0027
- **Requirements:** CHX-011, CHX-012, CHX-300, CHX-310

## Context

The legacy application, `kemiller2002/time-tracking-application`
("Business Activity Ledger", later "Echelon Ledger"), is an F# domain
compiled to WebAssembly with a hand-written DOM bridge, storing one JSON
document in browser storage and, optionally, in a GitHub repository. Its
data repository, `kemiller2002/time-tracking-data`, is an earlier
append-only JSON event ledger written by a Python tool. Chrona replaces it.

Three options were open:

| Option | For | Against |
|---|---|---|
| A. Migrate the legacy code forward | Fastest path to the same screens. | Carries a hand-written DOM bridge, a single-document store, single-owner assumptions and no Limen, Forma, Aegis or Folio. Every standard would be a retrofit. |
| B. Migrate the legacy data, rebuild the code | Preserves history. | There is no production history to preserve (see below). An importer now is speculative work. |
| C. Reconstruct; keep the look and feel and the rules; define, but defer, the import | Code to current standards from the start; the user experience and business rules survive. | The rules must be reconciled explicitly so nothing silently drops. |

## Decision

1. **Reconstruct (option C).** No legacy code, stylesheet or data format is
   copied into Chrona. The look and feel is kept through the inventory
   (EV-CHRONA-2026-0001) and the Chrona Forma brand; the business rules are
   kept through the reconciliation below. The legacy repository stays
   read-only reference material.
2. **The legacy rules are part of the requirements.** The expansion says
   "existing manual-entry rules remain", "existing duration invariants
   remain", "existing one-active-timer and pause/resume rules remain" and
   "existing daily attestation remains". Those existing rules are
   `time-tracking-application/docs/DOMAIN-REQUIREMENTS.md`. Every rule was
   compared with `Chrona.Domain`; the gaps (R1-R6) are WI-0043 and are closed
   in the domain before the UI is rebuilt on it.
3. **Legacy data needs no migration today.** Verified on 2026-10-08:
   - `kemiller2002/time-tracking-data` at
     `c23636d8040feba06fd762238045f8da66f0c3aa` (its remote `HEAD`) holds
     two activities and one evidence link, all demo records
     (`ACT-20260802-DEMO-MANUAL`, `ACT-20260802-DEMO-RESEARCH`,
     `EVL-20260802-DEMO-001`, actor `demo-owner`, client `demo`), plus the
     reports and indexes derived from them.
   - The shipped legacy app kept its own document in browser storage
     (`business-activity-ledger:v1`) and, when sync was configured, in
     `<folder>/<login>/ledger.json` (default folder `time-tracking-data`) of a
     repository each user chose. Chrona cannot see those; none was reported
     as holding production time.
4. **The migration path is defined but not built (CHX-300).** If production
   records appear, they are imported once, one way, as a Chrona
   application-level migration on Arca's migration workflow (WI-0044, after
   WI-0032):
   - Recognised formats only (`business-activity` 1.0.0,
     `business-evidence-link` 1.0.0, the `business-activity-ledger` document);
     anything else is refused with `CHRONA.LEGACY.UNKNOWN_FORMAT`, never
     guessed.
   - Preserved where present: activity id, revision, exact duration, start,
     project, activity type, tags, description, business purpose, entry
     method, lifecycle status, amendment provenance, split/merge lineage,
     evidence, attestations, billing projection inputs, identity metadata and
     settings. The legacy id is kept as the external reference.
   - Unknown remains unknown: an absent field is absent in Chrona, not
     defaulted (for example, legacy records carry no organization; the import
     names the target organization explicitly).
   - Record counts and exact-minute totals per day are compared before and
     after; any difference stops the import.
5. **Legacy names resolve through explicit aliases (CHX-310).** The canonical
   product name is **Chrona**. Historical names resolve to it only through
   the alias table in `Chrona.Domain.Compatibility`:

   | Kind | Legacy value | Resolves to |
   |---|---|---|
   | Application | `time-tracking-application`, `Business Activity Ledger`, `echelon-business-activity-ledger`, `Echelon Ledger`, `Ledger` | Chrona |
   | Assembly | `Ledger.Domain`, `Ledger.Engine`, `Ledger.Wasm` | `Chrona.Domain`, `Chrona.Engine`, `Chrona.Wasm` |
   | Record format | `business-activity` 1.0.0, `business-evidence-link` 1.0.0 | Legacy activity, legacy evidence link |
   | Browser storage | `business-activity-ledger:v1`, `business-activity-ledger:github-config:v1` | Legacy document, legacy sync settings |

   Matching ignores surrounding whitespace and letter case and nothing else.
   An unrecognised name is `CHRONA.LEGACY.UNKNOWN_SOURCE`. Chrona never reads
   or writes the legacy storage keys; they are recognised so an import can
   name what it found.
6. **The charter describes the product (CHX-011).** `PROJECT-CHARTER.md` and
   `context/CURRENT-STATE.md` are rewritten for the time-tracking product.

## Rule reconciliation

`DOMAIN-REQUIREMENTS.md` against `Chrona.Domain` at `018b7f5`:

| Legacy rule | Chrona.Domain | Result |
|---|---|---|
| Exact minutes; six-minute billing is derived, per activity, rounded up | `Billing.legacyDefault`, `billableMinutes`; minutes never changed | Kept |
| Activity cannot cross midnight | `CrossesBusinessDay`; timers split at the boundary | Kept (timer split is an expansion addition) |
| Status Recorded / Voided / Superseded; revision starts at 1 | `RecordState`, `Revision = 1` | Kept |
| Only Recorded may be amended, split, merged or voided; voided restores; superseded is final | `requireRecorded`, `Restore` | Kept |
| Amend, split and void check the expected revision | `RevisionConflict` | Kept |
| Description required | `MissingField "description"` | Kept |
| **Business purpose required** | not checked | **Gap R1** |
| Past-date manual entry needs a reason | `ReasonRequiredForHistoricalEntry` (window configurable) | Kept |
| Entry method recorded and shown | `EntryMethod` | Kept |
| No overlap with another Recorded activity | `Overlap.check` (half-open, scoped) | Kept |
| **Amended fields revalidated by the creation rules** | only overlap rechecked | **Gap R2** |
| Split: source at least two minutes, point strictly inside, durations sum exactly | at least two positive parts summing exactly | Kept |
| **Merge: two or more sources, same date, contiguous** | same date checked; non-contiguous merged as a duration | **Gap R3** |
| Restore revalidates against current state | overlap rechecked on restore | Kept |
| **Evidence: link and unlink on Recorded; link only on Voided; none on Superseded** | no record-state check | **Gap R4** |
| One active timer; pauses excluded | `Timer.start`, segments | Kept |
| **Stop rounds the working total to the nearest minute; under 30 seconds records nothing** | each piece floored separately, so pauses can lose up to a minute each | **Gap R5** |
| Timer activity validated like a manual one | `toActivities` builds records; validation happens on `Ledger.Record` (overlap only) | Covered by R1 and R2 |
| **Attestation: a statement plus a snapshot; re-attesting keeps both** | snapshot of ids and revisions kept, no statement | **Gap R6** |
| Project, activity type and tags must exist and be active when newly assigned | no reference data yet | WI-0031 |
| Stable, specific diagnostics | `Diagnostics.code` | Kept |
| Persistence: four load outcomes; saves report success, failure, conflict or unknown; unknown outcomes reconcilable | no storage yet | WI-0032, WI-0033 |

## Consequences

- WI-0043 closes R1-R6 in the pure domain before the UI slice uses it.
- WI-0044 exists only so the import is not forgotten; it is low priority and
  is abandoned if no production legacy records ever appear.
- CHX-011 and CHX-310 become tested; CHX-012 and CHX-300 stay partial.

## Revisit when

Production legacy records are found; or a legacy rule is shown to conflict
with the expansion (the expansion then wins and this record is revised).
