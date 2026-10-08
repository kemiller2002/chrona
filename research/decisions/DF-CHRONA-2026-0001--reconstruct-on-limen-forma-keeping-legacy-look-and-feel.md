---
id: DF-CHRONA-2026-0001
title: Chrona is built first, reconstructed (not migrated) from the legacy time-tracking-application, keeps its look and feel, and stores data through Arca without Strata
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
  - docs/requirements/CHRONA-REQUIREMENTS-EXPANSION.txt
  - docs/requirements/implementation-gap-analysis.md
  - docs/requirements/CHRONA-DATA-LOCATION.md
tags: [build-order, legacy, reconstruction, ui, storage, strata]
provenance:
  contributions:
    EXE-20261008T074345758Z-6c5a1aa8:
      operations: [created]
      at: 2026-10-08T07:44:41.707Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record user decisions of 2026-10-08 and the per-application data-location requirement"
---

# DF-CHRONA-2026-0001 — Build first; reconstruct, keep the look and feel

- **Date:** 2026-10-08
- **Status:** accepted (user decisions of 2026-10-08)

## Decisions

1. **Build order.** Chrona is built first, then Summa, then the other
   applications; Helix runs in parallel. Arca's and Fides's minimal slices
   come before Chrona's storage and sign-in items.
2. **Reconstruction, not migration.** The legacy application is
   `kemiller2002/time-tracking-application`. It is read-only reference
   material. Chrona **keeps its look and feel** (screens, layout, styles,
   interactions), and the code is rebuilt on Limen/Forma to all current
   standards, with no compromises. A look-and-feel inventory of the legacy
   application comes before the UI reconstruction.
3. **Legacy data.** `kemiller2002/time-tracking-data` holds the legacy
   append-only JSON event ledger. On 2026-10-08 it holds only demo records
   (`ACT-20260802-DEMO-*`, `EVL-20260802-DEMO-001`), so no data migration is
   needed now. If production records appear, importing them is a Chrona
   application-level migration on top of Arca's migration workflow.
4. **Storage.** Chrona stores data in GitHub through Arca (`kemiller2002/arca`)
   at a per-deployment location, in a Chrona-owned namespace
   (CHX-DATALOC-001..005). Chrona does **not** use Strata. Only applications
   that use a database use Strata.
5. **Sign-in.** Chrona signs in through Fides (`kemiller2002/fides`), GitHub
   only for now.
6. **Contract with Summa.** The application that accepts the data owns the
   contract, so **Summa owns the Chrona-to-Summa contract**. Summa publishes it
   as a small, versioned contracts package, and Chrona consumes it through
   `conditor.json`.
7. **PDFs.** Chrona's printable and PDF output uses Folio.
