# Requirement implementation gap analysis

Work item: WI-0019 (gap analysis); implementation items WI-0020 through WI-0023  
Baseline: `main` at `e0d8416` (2026-10-07)  
Authoritative source: [`CHRONA-REQUIREMENTS-EXPANSION.txt`](CHRONA-REQUIREMENTS-EXPANSION.txt)

## Purpose and method

The requirements expansion is organized by numbered section, not by
requirement ID. This record gives every section and subsection a stable
identifier, `CHX-NNM` (section `NN`, subsection `M`, `0` when the section
has none; for example 10.3 is `CHX-103` and 25 is `CHX-250`), and compares
it against `src/` and `tests/`:

| Status | Meaning |
|---|---|
| `tested` | Implemented for the section's stated scope, with a test that would fail if it regressed. |
| `partial` | Some normative statements are implemented and tested; the note says which. |
| `missing` | Nothing in `src/` implements the section. |

A section is `tested` only when every normative statement in it is covered.
Domain rules that are implemented as pure functions but not yet wired to
storage or UI are at most `partial` when the section also requires storage
or UI behaviour.

The **Baseline** column is `main` at `e0d8416`. The **Current** column is
updated by each change that closes or narrows a gap, with its work item.

### What existed at baseline

A kernel verification slice (`Chrona.Engine.KernelSlice`: a label saved to
and loaded from browser storage through Limen), the Limen protocol and an
Aegis dispatch boundary (`Chrona.Application`), the WASM shim, a Forma page,
and 47 xUnit tests plus a Playwright suite. The slice states explicitly that
it is not a product feature. No time-tracking behaviour exists.

The legacy sources the expansion asks to migrate
(`time-tracking-application/docs/DOMAIN-REQUIREMENTS.md` and its integration
docs) are not in this repository. This analysis and the implementation use
the expansion's own restatement of them; reconciling against the legacy text
remains open (CHX-012, CHX-300).

## Highest-value gaps and the order they are closed

Chrona owns the decision that a fact becomes authoritative recorded time
(section 45), so the pure time domain comes first; storage, UI and
integration depend on it.

1. **WI-0020** `Chrona.Domain`: the activity record, separate state
   dimensions, manual entry, time-zone context, overlap rules and stable
   diagnostics.
2. **WI-0021** Revision-safe amend, void, restore, split and merge with
   lineage and audit provenance.
3. **WI-0022** The timer state machine: segments, pause/resume,
   cross-midnight split and long-running review.
4. **WI-0023** Billing projection without altering exact time, the review
   lifecycle with stale-approval detection, attestation, and Summa
   publication eligibility.

GitHub storage, authentication, offline sync, observations, the product UI,
reporting and export remain open (see the rows marked `later`).

## Summary

| Corpus | Sections | Baseline tested | Baseline partial | Baseline missing |
|---|---:|---:|---:|---:|
| CHRONA-REQUIREMENTS-EXPANSION | 63 | 0 | 7 | 56 |

## Sections

| Section | Baseline | Current | Evidence or gap | Work items |
|---|---|---|---|---|
| CHX-001 | partial | partial | **0.1 ROS.** Praxis work protocol, `praxis validate` and registry checks run in CI (`praxis-validation.yml`). No product work has yet been delivered under it. | all |
| CHX-002 | partial | partial | **0.2 SDE / Ordo.** The kernel slice models explicit phases, legal transitions, effects as data and stale-result rejection (`KernelSlice.fs`, `KernelSliceTests`). No product domain states. **WI-0021:** product states as separate types, legal transitions with explicit refusals, optimistic revision checks and append-only audit in `Chrona.Domain`. | WI-0020 |
| CHX-003 | partial | partial | **0.3 Limen.** F# engine behind Limen with real-browser verification of the slice (`verification/kernel-slice`, `TransportTests`). No product surface. | later |
| CHX-004 | partial | partial | **0.4 F# architecture.** Engine/Application/Wasm tiers exist; no `Chrona.Domain` or `Chrona.Integration` assembly. **WI-0020:** pure `Chrona.Domain` assembly added beneath the engine. `Chrona.Integration` remains. | WI-0020 |
| CHX-005 | partial | partial | **0.5 Shared foundations.** Aegis at the dispatch boundary, Forma page and Folio declared (`FoundationsConformanceTests`); no product surface uses them yet. | later |
| CHX-011 | missing | missing | **1.1 Purpose.** Charter still generic; no time-tracking product. | later |
| CHX-012 | missing | missing | **1.2 Preserve existing capabilities.** None of the listed capabilities exists; the legacy `time-tracking-application` sources are not in this repository. | WI-0020..WI-0023 |
| CHX-021 | missing | missing | **2.1 Authoritative storage.** No GitHub storage. | later |
| CHX-022 | missing | missing | **2.2 Login.** No GitHub token login. | later |
| CHX-023 | missing | missing | **2.3 Token safety.** No token handling. | later |
| CHX-024 | missing | missing | **2.4 Multi-application repositories.** No storage paths. | later |
| CHX-025 | missing | partial | **2.5 Organization isolation.** No organization model. **WI-0020:** every activity carries an OrganizationId and overlap is scoped by it. Folders, manifests and per-organization repositories remain. | WI-0020 |
| CHX-026 | missing | missing | **2.6 Organization manifest.** No manifest. | later |
| CHX-027 | missing | missing | **2.7 Production repository safety.** No repository visibility check. | later |
| CHX-030 | missing | missing | **3 Identity, membership, authorization.** No actors or capabilities. | later |
| CHX-040 | missing | missing | **4 Reference data.** No clients/projects/activity types/tags. | WI-0020 |
| CHX-050 | missing | partial | **5 Authoritative activity record.** No activity record. **WI-0020:** `Activity` record carries every listed field (ids, occurrence, timing, exact minutes, classification, entry method, the three state dimensions, revision, timestamps, reason, work-item/external refs, evidence, lineage). Persistence remains. | WI-0020 |
| CHX-061 | missing | tested | **6.1 Record lifecycle.** No lifecycle. **WI-0020:** Recorded/Voided/Superseded as their own type; transitions arrive in WI-0021. **WI-0021:** void keeps the record, restore returns it to Recorded after an overlap recheck, split and merge supersede sources; nothing is deleted (`Ledger`, `LedgerTests` scenario 11). | WI-0020 |
| CHX-062 | missing | missing | **6.2 Review lifecycle.** No review states. | WI-0023 |
| CHX-063 | missing | missing | **6.3 Publication/billing lifecycle.** No publication states. | WI-0023 |
| CHX-070 | missing | missing | **7 Exact versus billable time.** No billing projection. | WI-0023 |
| CHX-080 | missing | missing | **8 Billability and rate references.** No billability. | WI-0023 |
| CHX-090 | missing | tested | **9 Manual entry.** No manual entry. **WI-0020:** start/end, start+duration and duration-only entries with deterministic minutes; historical entries need a reason; future time refused; copies get a new identity; all problems reported together (`ManualEntry`, `TimeDomainTests`). | WI-0020 |
| CHX-101 | missing | missing | **10.1 Timer persistence.** No timer. | WI-0022 |
| CHX-102 | missing | missing | **10.2 Timer recovery.** No timer. | WI-0022 |
| CHX-103 | missing | missing | **10.3 Cross-midnight.** No timer. | WI-0022 |
| CHX-104 | missing | missing | **10.4 Long-running safety.** No timer. | WI-0022 |
| CHX-105 | missing | missing | **10.5 Multiple devices.** No timer. | WI-0022 |
| CHX-110 | missing | tested | **11 Time zone and calendar.** No time-zone model. **WI-0020:** UTC instants plus IANA zone, offset, local date and local start; DST days measured exactly; ambiguous and skipped local times refused unless disambiguated; a later zone change never rewrites history (`Time`, `TimeDomainTests` scenarios 6-7). | WI-0020 |
| CHX-120 | missing | partial | **12 Overlap and conflict rules.** No overlap rules. **WI-0020:** overlap scoped by actor and organization, half-open intervals, voided/superseded excluded, submitted/approved included, DST-aware daily capacity (`Overlap`, scenario 8). Restore recheck, imports and divergent-edit conflicts remain (WI-0021). **WI-0021:** restore rechecks against current state; same-record divergence is a RevisionConflict, not last-write-wins (scenarios 10, 32). The import path (observations) does not exist yet. | WI-0020, WI-0021 |
| CHX-130 | missing | tested | **13 Split and merge.** No split/merge. **WI-0021:** split children inherit classification, keep source lineage and exact total time, and take each evidence id at most once; merge keeps every source id, supersedes sources and refuses incompatible actors, dates or publication states (scenarios 12-14). | WI-0021 |
| CHX-140 | missing | missing | **14 Submission and approval.** No approval. | WI-0023 |
| CHX-150 | missing | missing | **15 Timesheet periods.** No periods. | later |
| CHX-160 | missing | missing | **16 Attestation.** No attestation. | WI-0023 |
| CHX-170 | missing | missing | **17 Summa integration.** No publication. | WI-0023 |
| CHX-180 | missing | missing | **18 Receiver-owned integration assembly.** No `Chrona.Integration`. | later |
| CHX-190 | missing | missing | **19 Observation processing.** No observations. | later |
| CHX-200 | missing | missing | **20 Startup/reconciliation.** No reconciliation. | later |
| CHX-210 | missing | partial | **21 GitHub concurrency.** No revisions or storage. **WI-0021:** every command carries its expected revision and stale commands are refused with a stable diagnostic. Repository-state concurrency remains with storage. | WI-0021 |
| CHX-220 | missing | missing | **22 Storage scale.** No storage. | later |
| CHX-230 | missing | missing | **23 Offline behavior.** No offline model. | later |
| CHX-240 | missing | partial | **24 External artifacts and evidence.** No evidence references. **WI-0021:** evidence references (id, URL, kind, label, captured time, optional hash) link and unlink with audit and survive split/merge. Source and notes fields remain. | WI-0021 |
| CHX-250 | missing | partial | **25 Audit and provenance.** No audit trail. **WI-0021:** create, amend, void, restore, split, merge and evidence link/unlink append audit entries with performer, time, source, command, prior and resulting revisions, reason and correlation id; refusals change nothing. Review, publication and import audit remain. | WI-0021 |
| CHX-260 | missing | partial | **26 Stable diagnostics.** No product diagnostics. **WI-0020:** stable `CHRONA.<AREA>.<NAME>` codes for entry, time, overlap, concurrency, lifecycle, timer, review and publication diagnostics. Authorization, integration and storage diagnostics remain. | WI-0020 |
| CHX-270 | missing | missing | **27 Search.** No search. | later |
| CHX-280 | missing | missing | **28 Reporting.** No reports. | later |
| CHX-290 | missing | missing | **29 Export.** No export. | later |
| CHX-300 | missing | missing | **30 Legacy migration.** No migration. | later |
| CHX-310 | missing | missing | **31 Rename/source compatibility.** No compatibility mapping. | later |
| CHX-320 | missing | missing | **32 Core UX.** No product UI. | later |
| CHX-330 | missing | missing | **33 Quick entry.** No entry UI. | later |
| CHX-340 | missing | missing | **34 Obligations/work queue.** No obligations. | later |
| CHX-350 | partial | partial | **35 Accessibility.** The slice page uses semantic HTML and Forma; no product UI to verify. | later |
| CHX-360 | missing | missing | **36 Responsive/mobile.** No product UI. | later |
| CHX-370 | missing | missing | **37 Browser reliability.** No product UI. | later |
| CHX-380 | missing | missing | **38 GitHub API efficiency.** No GitHub access. | later |
| CHX-390 | missing | missing | **39 Data integrity.** No authoritative data. | later |
| CHX-400 | missing | missing | **40 Recovery.** No derived state. | later |
| CHX-410 | missing | missing | **41 Manual repository edits.** No authoritative files. | later |
| CHX-420 | partial | partial | **42 Telemetry/privacy.** Aegis faults carry no business content (slice tests); no product telemetry. | later |
| CHX-430 | missing | partial | **43 Required scenario tests.** 0 of 44 scenarios tested. **WI-0020:** scenarios 1, 6, 7 and 8 tested. **WI-0020, WI-0021:** scenarios 1, 6, 7, 8, 10, 11, 12, 13, 14, 32, 37 and 38 tested. | WI-0020..WI-0023 |
| CHX-440 | missing | missing | **44 Production completion gate.** Gate not met. | later |
| CHX-450 | missing | missing | **45 Architectural boundary.** No time authority or Summa boundary. | WI-0023 |

## Coverage after this programme

| Corpus | Sections | Current tested | Current partial | Current missing |
|---|---:|---:|---:|---:|
| CHRONA-REQUIREMENTS-EXPANSION | 63 | 4 | 15 | 44 |
