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
docs) are not in this repository. This analysis and the implementation used
the expansion's own restatement of them; WI-0027 reconciled the domain
against the legacy text (DF-CHRONA-2026-0002) and WI-0043 closes the gaps it
found.

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
| CHX-003 | partial | tested | **0.3 Limen.** F# engine behind Limen with real-browser verification of the slice (`verification/kernel-slice`, `TransportTests`). No product surface. **WI-0046:** the product page runs on Limen: the F# engine (`Chrona.Engine.App`, pure update and projection over `Chrona.Domain`) on .NET WebAssembly decides everything; HTML is semantic structure bound by `data-*`; CSS is presentation; JavaScript is the kernel start-up only (`web/app.js`, `web-kernel/limen-wasm.js`). Held by `AppBoundaryTests` (protocol and binding agreement), `FoundationsConformanceTests` and the browser suite (`app.spec.js`). | WI-0046 |
| CHX-004 | partial | partial | **0.4 F# architecture.** Engine/Application/Wasm tiers exist; no `Chrona.Domain` or `Chrona.Integration` assembly. **WI-0020:** pure `Chrona.Domain` assembly added beneath the engine. `Chrona.Integration` remains. **WI-0046:** the engine now references `Chrona.Domain` (Domain ← Engine ← Application/Wasm ← browser host). | WI-0020, WI-0046 |
| CHX-005 | partial | tested | **0.5 Shared foundations.** Aegis at the dispatch boundary, Forma page and Folio declared (`FoundationsConformanceTests`); no product surface uses them yet. **WI-0046:** the product page uses Aegis at its dispatch boundary (`App.handle`), Forma components and the Chrona Forma brand; Folio is not used by a product report yet (WI-0039). **WI-0049:** Folio now prints the product's time report: the reports screen carries Folio's `<ef-print-document>` and Chrona's print pack opens the browser's print dialog (print or save as PDF); on paper only Folio's document shows (`app-reports.spec.js`). Aegis guards the app boundary and Forma presents every screen (WI-0046). | WI-0046, WI-0049 |
| CHX-011 | missing | tested | **1.1 Purpose.** Charter still generic; no time-tracking product. **WI-0027:** `PROJECT-CHARTER.md` and `context/CURRENT-STATE.md` describe the time-tracking product (`CompatibilityTests`). | WI-0027 |
| CHX-012 | missing | partial | **1.2 Preserve existing capabilities.** None of the listed capabilities exists; the legacy `time-tracking-application` sources are not in this repository. **WI-0027:** the legacy `DOMAIN-REQUIREMENTS.md` is reconciled rule by rule against `Chrona.Domain` (DF-CHRONA-2026-0002); gaps R1-R6 are WI-0043. Reference data, GitHub sync, offline use, observations and receipts remain with their slices. **WI-0043:** gaps R1-R6 closed: business purpose required, amendments revalidated, contiguous merge, evidence by record state, nearest-minute timer total with the 30-second rule, attestation statement (`LegacyRuleTests`). | WI-0020..WI-0023, WI-0027, WI-0043 |
| CHX-021 | missing | missing | **2.1 Authoritative storage.** No GitHub storage. | later |
| CHX-022 | missing | missing | **2.2 Login.** No GitHub token login. | later |
| CHX-023 | missing | missing | **2.3 Token safety.** No token handling. | later |
| CHX-024 | missing | missing | **2.4 Multi-application repositories.** No storage paths. | later |
| CHX-025 | missing | partial | **2.5 Organization isolation.** No organization model. **WI-0020:** every activity carries an OrganizationId and overlap is scoped by it. Folders, manifests and per-organization repositories remain. | WI-0020 |
| CHX-026 | missing | missing | **2.6 Organization manifest.** No manifest. | later |
| CHX-027 | missing | missing | **2.7 Production repository safety.** No repository visibility check. | later |
| CHX-030 | missing | missing | **3 Identity, membership, authorization.** No actors or capabilities. | later |
| CHX-040 | missing | tested | **4 Reference data.** No clients/projects/activity types/tags. **WI-0045:** an organization-scoped catalogue of clients, projects, engagements, activity types and tags with stable ids, revisions and active/archived state; only active items are selectable; newly assigned references must exist and be active while ones a record already carries stay valid when archived (scenario 15); concepts other systems own are mirrored by their stable id and change only through their owner (`Reference`, `ReferenceTests`). Storage on Arca is WI-0031. | WI-0045, WI-0031 |
| CHX-050 | missing | partial | **5 Authoritative activity record.** No activity record. **WI-0020:** `Activity` record carries every listed field (ids, occurrence, timing, exact minutes, classification, entry method, the three state dimensions, revision, timestamps, reason, work-item/external refs, evidence, lineage). Persistence remains. | WI-0020 |
| CHX-061 | missing | tested | **6.1 Record lifecycle.** No lifecycle. **WI-0020:** Recorded/Voided/Superseded as their own type; transitions arrive in WI-0021. **WI-0021:** void keeps the record, restore returns it to Recorded after an overlap recheck, split and merge supersede sources; nothing is deleted (`Ledger`, `LedgerTests` scenario 11). | WI-0020 |
| CHX-062 | missing | tested | **6.2 Review lifecycle.** No review states. **WI-0023:** Unsubmitted/Submitted/Approved/Rejected/Reopened with legal transitions, organization-configurable approval, and changed reviewed time reopened (`Review`, `ReviewBillingTests` scenarios 18-22). | WI-0023 |
| CHX-063 | missing | partial | **6.3 Publication/billing lifecycle.** No publication states. **WI-0023:** NotBillable/Unpublished/Published/AdjustmentRequired driven by billability, publication and later corrections. Summa's InvoicedExternally report and ReadyForPublication staging are not wired. | WI-0023 |
| CHX-070 | missing | tested | **7 Exact versus billable time.** No billing projection. **WI-0023:** versioned, scoped, dated billing policies (legacy six-minute up default; up/down/nearest/exact); projections never change exact minutes and carry policy id and version (`Billing`, scenario 24). | WI-0023 |
| CHX-080 | missing | tested | **8 Billability and rate references.** No billability. **WI-0023:** Billable/NonBillable/PendingClassification gate publication. Rate reference, billing class and contract identifiers are not yet fields. **WI-0045:** the activity retains a rate reference, billing class and contract identifier as identifiers only, amendable and carried by copies; money stays with Summa (`BillingReference`, `ReferenceTests`). | WI-0023, WI-0045 |
| CHX-090 | missing | tested | **9 Manual entry.** No manual entry. **WI-0020:** start/end, start+duration and duration-only entries with deterministic minutes; historical entries need a reason; future time refused; copies get a new identity; all problems reported together (`ManualEntry`, `TimeDomainTests`). **WI-0043:** a business purpose is required, and amendments are revalidated by the same rules (`LegacyRuleTests` R1, R2). | WI-0020, WI-0043 |
| CHX-101 | missing | partial | **10.1 Timer persistence.** No timer. **WI-0022:** elapsed time is derived only from persisted segment timestamps, never a counter (`Timer.elapsedMinutes`). Browser persistence across refresh/restart is not wired yet. | WI-0022 |
| CHX-102 | missing | partial | **10.2 Timer recovery.** No timer. **WI-0022:** `Timer.recover` returns a persisted timer explicitly with its elapsed minutes and never discards it (scenario 4). Startup wiring remains. | WI-0022 |
| CHX-103 | missing | tested | **10.3 Cross-midnight.** No timer. **WI-0022:** stopping splits every segment at business-day boundaries in the timer's zone; each piece keeps the timer id as lineage and the total is exact, including across DST (scenario 5, `TimerTests`). **WI-0043:** the working total is rounded once to the nearest minute (under 30 seconds records nothing) and shared out exactly across pause and midnight pieces, each placed inside its business day (`LegacyRuleTests` R5, a 500-timer property test). | WI-0022, WI-0043 |
| CHX-104 | missing | tested | **10.4 Long-running safety.** No timer. **WI-0022:** more than 720 minutes is held with `LongRunningTimerNeedsReview` and cannot become activities until reviewed (`TimerTests`). | WI-0022 |
| CHX-105 | missing | partial | **10.5 Multiple devices.** No timer. **WI-0022:** one active timer per state; overlapping timers of the same actor from different devices are reported as reconciliation pairs (scenario 9). The obligation queue remains. | WI-0022 |
| CHX-110 | missing | tested | **11 Time zone and calendar.** No time-zone model. **WI-0020:** UTC instants plus IANA zone, offset, local date and local start; DST days measured exactly; ambiguous and skipped local times refused unless disambiguated; a later zone change never rewrites history (`Time`, `TimeDomainTests` scenarios 6-7). | WI-0020 |
| CHX-120 | missing | partial | **12 Overlap and conflict rules.** No overlap rules. **WI-0020:** overlap scoped by actor and organization, half-open intervals, voided/superseded excluded, submitted/approved included, DST-aware daily capacity (`Overlap`, scenario 8). Restore recheck, imports and divergent-edit conflicts remain (WI-0021). **WI-0021:** restore rechecks against current state; same-record divergence is a RevisionConflict, not last-write-wins (scenarios 10, 32). The import path (observations) does not exist yet. **WI-0050:** accepted observations are recorded through the ordinary ledger rules, so an import that overlaps is refused (or needs attention when auto-accepted), never recorded over existing time. | WI-0020, WI-0021, WI-0050 |
| CHX-130 | missing | tested | **13 Split and merge.** No split/merge. **WI-0021:** split children inherit classification, keep source lineage and exact total time, and take each evidence id at most once; merge keeps every source id, supersedes sources and refuses incompatible actors, dates or publication states (scenarios 12-14). **WI-0043:** only contiguous sources with clock times merge, as the legacy rules required (`LegacyRuleTests` R3). | WI-0021, WI-0043 |
| CHX-140 | missing | tested | **14 Submission and approval.** No approval. **WI-0023:** submission records exact (id, revision) pairs; approval records approver, time, period, covered revisions and note; rejection records reason and reviewer; reopen is explicit; stale approvals are detected and refused (scenarios 18-22). | WI-0023 |
| CHX-150 | missing | tested | **15 Timesheet periods.** No periods. **WI-0048:** daily, weekly, biweekly, semi-monthly and monthly periods that tile the calendar; organization configuration of cadence, week start, time zone, submission expectation and approval requirement; period summaries with exact, billable (as billed), non-billable and unclassified time, submission and approval state and outstanding obligations (`Periods`, `PeriodTests`), shown on Today and configured under More (`app-lifecycle.spec.js`). Storing the configuration on Arca is WI-0036. | WI-0048, WI-0036 |
| CHX-160 | missing | tested | **16 Attestation.** No attestation. **WI-0023:** attestation snapshots ids and revisions for the actor's day, separate from approval; later changes and additions are reported (scenarios 16-17). **WI-0043:** an attestation carries the actor's statement; attesting again keeps both (`LegacyRuleTests` R6). | WI-0023, WI-0043 |
| CHX-170 | missing | partial | **17 Summa integration.** No publication. **WI-0023:** publication gates (recorded, billable, approved where required, project present, not already published, policy resolved), idempotent retry per publication id, and correction obligations after publication or invoicing (scenarios 23, 35-38). The Summa transport and contract remain. | WI-0023 |
| CHX-180 | missing | missing | **18 Receiver-owned integration assembly.** No `Chrona.Integration`. | later |
| CHX-190 | missing | partial | **19 Observation processing.** No observations. **WI-0050:** observation to candidate to receipt as pure rules: dispositions Pending, Accepted, AcceptedWithChanges, Rejected (kept, with reason), Duplicate and NeedsAttention; review by default unless a source policy allows auto-accept; invalid payloads get their own receipt, distinct from rejection; receipts follow the durable candidate and a missing one is repaired, never re-decided; accepted activities keep source system, observation id, work item, external URL and ingestion time (`Observations`, `ObservationTests` scenarios 25-30). Durable inboxes and reconciliation need Arca (WI-0038). | WI-0050, WI-0038 |
| CHX-200 | missing | missing | **20 Startup/reconciliation.** No reconciliation. | later |
| CHX-210 | missing | partial | **21 GitHub concurrency.** No revisions or storage. **WI-0021:** every command carries its expected revision and stale commands are refused with a stable diagnostic. Repository-state concurrency remains with storage. | WI-0021 |
| CHX-220 | missing | missing | **22 Storage scale.** No storage. | later |
| CHX-230 | missing | missing | **23 Offline behavior.** No offline model. | later |
| CHX-240 | missing | partial | **24 External artifacts and evidence.** No evidence references. **WI-0021:** evidence references (id, URL, kind, label, captured time, optional hash) link and unlink with audit and survive split/merge. Source and notes fields remain. **WI-0043:** evidence follows the record state: Recorded links and unlinks, Voided only links, Superseded neither (`LegacyRuleTests` R4). **WI-0047:** an evidence reference needs a label and a kind, and its link, when given, must be an http(s) address (`Activity.evidenceProblems`); evidence is linked, unlinked and assigned to split parts from the activity screen. Source and notes fields remain. | WI-0021, WI-0043, WI-0047 |
| CHX-250 | missing | partial | **25 Audit and provenance.** No audit trail. **WI-0021:** create, amend, void, restore, split, merge and evidence link/unlink append audit entries with performer, time, source, command, prior and resulting revisions, reason and correlation id; refusals change nothing. Review, publication and import audit remain. **WI-0023:** submit, approve, reject, reopen and publish are now audited too. **WI-0050:** imports are audited with an `observation:<source>` source, and every candidate keeps its decisions in order. | WI-0021, WI-0050 |
| CHX-260 | missing | partial | **26 Stable diagnostics.** No product diagnostics. **WI-0020:** stable `CHRONA.<AREA>.<NAME>` codes for entry, time, overlap, concurrency, lifecycle, timer, review and publication diagnostics. Authorization, integration and storage diagnostics remain. | WI-0020 |
| CHX-270 | missing | partial | **27 Search.** No search. **WI-0049:** search and filters over the session's records by date, actor, client, project, engagement, activity type, tag, billability, entry method and source, review and publication state, and words in the description, business purpose, work item or source (`Reports.matches`, `ReportTests`); the reports screen exposes the common ones. Rebuildable search indexes over stored data, and organization-wide search, need Arca (WI-0039). | WI-0049, WI-0039 |
| CHX-280 | missing | partial | **28 Reporting.** No reports. **WI-0049:** custom-range reports (daily, weekly, monthly as ranges) grouped by day, person, client, project, engagement, activity type or tag, with exact, billable-as-billed and non-billable time, timer versus manual, approved/unapproved, published/unpublished, amended-after-review and corrected-after-publication, always keeping exact and billable time apart (`Reports`, the reports screen). Reports across people and organizations need stored data (WI-0039). | WI-0049, WI-0039 |
| CHX-290 | missing | tested | **29 Export.** No export. **WI-0049:** deterministic CSV (RFC 4180, LF) and JSON exports that name the schema (`chrona.time-report/1`), organization, generation time, date range, filters and the exact-versus-billable semantics; the same records in any order export byte for byte the same (`ReportTests`); copied through Limen's clipboard effect (`app-reports.spec.js`). | WI-0049, WI-0039 |
| CHX-300 | missing | partial | **30 Legacy migration.** No migration. **WI-0027:** reconstruction, not migration; legacy data verified as demo-only; the import path is defined (preserved fields, unknown stays unknown, counts and totals compared) and recognised legacy formats resolve exactly, unknown ones are refused (`Compatibility.format`). The importer is WI-0044, only if production records appear. | WI-0027, WI-0044 |
| CHX-310 | missing | tested | **31 Rename/source compatibility.** No compatibility mapping. **WI-0027:** explicit alias tables for product names, assemblies, record formats and browser-storage keys; Chrona is canonical; unknown names are `CHRONA.LEGACY.UNKNOWN_SOURCE` (`Compatibility`, `CompatibilityTests`). | WI-0027 |
| CHX-320 | missing | partial | **32 Core UX.** No product UI. **WI-0046:** start timer, pause/resume, stop and complete (classify), add manual time and view today, in memory (`app.spec.js`). Amend, week view, review, submit, approve, sync state and conflicts remain (WI-0047, WI-0035). **WI-0047:** amend (correct), remove from totals and restore, split, merge, evidence, view the month, review and attest the day, in memory (`app-lifecycle.spec.js`). | WI-0046, WI-0047, WI-0035 |
| CHX-330 | missing | partial | **33 Quick entry.** No entry UI. **WI-0046:** keyboard-first and mobile-friendly entry; exact times are never changed by the form. Recent combinations, common durations and copy-as-draft in the UI remain. | WI-0046, WI-0035 |
| CHX-340 | missing | partial | **34 Obligations/work queue.** No obligations. **WI-0047:** obligations are one projection (`Project.obligations`) shown on Today, each with the place it is resolved: an unclassified (held) timer result, a day changed after its attestation, and a store answer that was not a success (conflict, failure, unknown outcome). Candidates, submissions, approvals, rejections, publication and Summa reconciliation join it with their slices. | WI-0047, WI-0035 |
| CHX-350 | partial | partial | **35 Accessibility.** The slice page uses semantic HTML and Forma; no product UI to verify. **WI-0046:** the product page: landmarks, labelled controls, a skip link, visible focus, no colour-only state (shape and text cues), forced-colours rules, a polite live region for transitions only and a `role="timer"` display that is never announced, 44px targets (`app.spec.js`). iPhone VoiceOver, zoom and reflow audits remain. | WI-0046, WI-0035 |
| CHX-360 | missing | partial | **36 Responsive/mobile.** No product UI. **WI-0046:** at phone width the sidebar becomes a bottom tab bar; start/stop/pause/resume, manual entry and today work with no sideways scrolling (`app.spec.js`). Candidates, weekly totals and sync problems remain. | WI-0046, WI-0035 |
| CHX-370 | missing | partial | **37 Browser reliability.** No product UI. **WI-0046:** every screen has a fragment route that survives Back, Forward and deep links. Records are in memory until storage exists, and the page says so; refresh, suspension, service-worker update and pending-time safety remain (WI-0033, WI-0035). | WI-0046, WI-0035 |
| CHX-380 | missing | missing | **38 GitHub API efficiency.** No GitHub access. | later |
| CHX-390 | missing | missing | **39 Data integrity.** No authoritative data. | later |
| CHX-400 | missing | missing | **40 Recovery.** No derived state. | later |
| CHX-410 | missing | missing | **41 Manual repository edits.** No authoritative files. | later |
| CHX-420 | partial | partial | **42 Telemetry/privacy.** Aegis faults carry no business content (slice tests); no product telemetry. | later |
| CHX-430 | missing | partial | **43 Required scenario tests.** 0 of 44 scenarios tested. **WI-0020:** scenarios 1, 6, 7 and 8 tested. **WI-0020, WI-0021:** scenarios 1, 6, 7, 8, 10, 11, 12, 13, 14, 32, 37 and 38 tested. **WI-0022:** scenarios 2, 3, 4, 5 and 9 added (17 of 44 tested in total). **WI-0023:** scenarios 16-24, 35 and 36 added (28 of 44 tested in total). **WI-0045:** scenario 15 added (29 of 44). **WI-0050:** scenarios 25-30 added (35 of 44 tested). | WI-0020..WI-0023, WI-0050 |
| CHX-440 | missing | missing | **44 Production completion gate.** Gate not met. | later |
| CHX-450 | missing | partial | **45 Architectural boundary.** No time authority or Summa boundary. **WI-0023:** Chrona decides authoritative time and publishes approved billable projections; money stays out of Chrona. | WI-0023 |

## Coverage after this programme

| Corpus | Sections | Current tested | Current partial | Current missing |
|---|---:|---:|---:|---:|
| CHRONA-REQUIREMENTS-EXPANSION | 63 | 18 | 29 | 16 |
