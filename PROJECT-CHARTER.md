---
id: PROJECT-CHARTER-chrona
title: Chrona Project Charter
status: accepted
version: 1.0.0
created: 2026-09-13
updated: 2026-10-08
provenance:
  contributions:
    EXE-20261008T084801800Z-78e42f18:
      operations: [modified]
      at: 2026-10-08T08:50:50.852Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Describe the time-tracking product (CHX-011)"
---

# Chrona project charter

## Purpose

Chrona records, corrects, reviews, reports and publishes business time for
downstream operational and financial use (requirements expansion 1.1).

Chrona is the authority for recorded time: it decides when a fact becomes
authoritative time, keeps exact minutes separate from any billing
projection, and publishes approved billable time to Summa, which owns money
(expansion 45). It replaces the legacy `time-tracking-application`
("Business Activity Ledger") by reconstruction, keeping its look and feel and
its business rules (DF-CHRONA-2026-0001, DF-CHRONA-2026-0002).

## Intended users

- People recording their own business time: timer and manual entry,
  corrections, daily attestation, on desktop and on a phone.
- Reviewers and approvers in organizations that require approval.
- Summa and other Echelon applications, as consumers of approved, published
  time through a receiver-owned contract.

## Scope

Included:

- Timer and manual entry in exact minutes; time-zone and business-day rules;
  overlap prevention.
- Amend, void, restore, split and merge with revisions, lineage and an
  append-only audit trail; evidence references.
- Billing projections, submission, approval, attestation and publication
  eligibility.
- Storage in GitHub through Arca at a per-deployment location, sign-in
  through Fides, offline use and reconciliation.
- Search, reports and exports (Folio for printable output).
- An accessible, mobile-first browser application on Limen and Forma.

Excluded:

- Money: rates, invoices and payments belong to Summa.
- A database (Strata): Chrona stores data through Arca.
- Migrating legacy code. Legacy data is imported only if production records
  appear (WI-0044).

## Success criteria

The production completion gate of the requirements expansion (section 44):
every section tested, the 44 required scenarios passing, and the
`implementation-gap-analysis.md` current column at `tested` throughout.

## Constraints

- Every Echelon foundation is required: Praxis, Ordo, Limen, Forma, Aegis,
  Folio (DF-CHRONA-FND-2026-0002).
- Functional style: a pure domain and engine; effects only at the edge.
- WCAG 2.2 AA; no compromise on engineering standards.
- Chrona owns no contract it consumes: Summa owns the Chrona-to-Summa
  contract.

## Owners and decision authority

The repository owner (kemiller2002) decides scope and priorities; material
decisions are `DF-` records under `research/decisions/`.
