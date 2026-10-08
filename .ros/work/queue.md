# Work Queue

| ID | Work | Status | Tags | Priority |
|---|---|---|---|---|
| CHR-REQ-001 | CHR-REQ-001 | complete |  |  |
| FOUNDATIONS-APPLICABILITY | FOUNDATIONS-APPLICABILITY | complete |  |  |
| GH-4 | Prepare Chrona implementation baseline | active | readiness,bootstrap | high |
| ROS-INSTALL-2-0-1 | ROS-INSTALL-2-0-1 | complete |  |  |
| WI-0001 | Install and verify Echelon Foundry toolchain: ROS, SDE, typescript-wasm-kernel | complete | setup,dependencies | high |
| WI-0002 | Record toolchain handoff state: SDE and wasm kernel alongside ROS | complete | governance | medium |
| WI-0003 | GitHub Actions never dispatches a runner: ROS validation workflow fails in ~3s with no job steps | complete | ci,infrastructure | high |
| WI-0004 | Maintain running log of assumptions, findings, and difficulties using the wasm kernel | blocked | wasm-kernel, journal | high |
| WI-0005 | Record wasm kernel assumptions, findings, and difficulties as a ROS research journal | complete | wasm-kernel, journal | high |
| WI-0006 | Kernel verification slice: run the wasm kernel bridge end to end in a real browser | complete | wasm-kernel, verification | high |
| WI-0007 | Upgrade the Limen installation to 0.7.0 (@echelon-foundry/limen) and declare the kernel slice as its boundary | complete |  | medium |
| WI-0008 | Align the declared Limen foundation baseline with the 0.7.0 installation | complete |  | medium |
| WI-0009 | Repin echelon-foundations workflow to praxis a95dbf2 (Limen 0.7.0-aware foundations verifier) | complete |  | medium |
| WI-0010 | Move the kernel slice to @echelon-foundry/limen 0.7.0 exactly and adapt it to the 0.7.0 protocol | complete |  | medium |
| WI-0011 | Upgrade the ROS installation to 3.1.4, the praxis baseline declared in .echelon/foundations.json (ECHELON-FND-PRAXIS-002) | complete |  | medium |
| WI-0012 | Restore Aegis to required in .echelon/foundations.json when the first .NET/F# project is added (DF-CHRONA-FND-2026-0001 trigger 1): reference EchelonFoundry.Aegis.Core 1.0.0, use it at the boundary, add aegis-boundaries.json | complete |  | medium |
| WI-0013 | Restore Forma to required in .echelon/foundations.json when the first interactive product surface is added or the kernel slice is promoted (DF-CHRONA-FND-2026-0001 trigger 2): consume the pinned @echelon-foundry/design-system | complete |  | medium |
| WI-0014 | Restore Folio to required in .echelon/foundations.json when the first time report, printable summary or PDF export is added (DF-CHRONA-FND-2026-0001 trigger 3): consume the pinned @echelon-foundry/print-components | complete |  | medium |
| WI-0015 | Upgrade to Forma 0.4.1 and Limen 0.7.1 | complete | foundations, limen | high |
| WI-0016 | Move Chrona to Praxis 3.7.1 (ROS -> Praxis rename) and Ordo 1.4.0 | complete | praxis, ordo, toolchain | medium |
| WI-0017 | Move chrona to Ordo 1.4.1 | complete | ordo, toolchain | medium |
| WI-0018 | Move chrona to Praxis 3.7.2, Ordo 1.4.2, Visual Engineering 1.0.1 and adopt Conditor | complete |  | medium |
| WI-0019 | Requirement gap analysis: compare every CHRONA-REQUIREMENTS-EXPANSION section against code and tests | complete | requirements,gap-analysis | high |
| WI-0020 | Chrona.Domain: authoritative activity record, separate state dimensions, manual entry and overlap rules (sections 5, 6, 9, 11, 12, 26) | complete | domain,activity,overlap | high |
| WI-0021 | Revision-safe lifecycle: amend, void, restore, split and merge with lineage and audit provenance (sections 12, 13, 21, 25) | complete | domain,lifecycle,audit | high |
| WI-0022 | Timer state machine: one active timer, pause/resume, persisted segments, cross-midnight split, long-running review (section 10) | complete | domain,timer | high |
| WI-0023 | Billing projection, review lifecycle, attestation and Summa publication eligibility (sections 7, 8, 14, 16, 17) | complete | domain,billing,approval | high |
| WI-0024 | Move chrona to Ordo 1.5.0 via echelon-current 1.2.0 (conditor upgrade --current) | complete |  | medium |
| WI-0025 | Capture the remaining Chrona requirements as dependency-ordered backlog slices; add CHX-DATALOC-001 and record the 2026-10-08 decisions | complete | planning, requirements | high |
| WI-0026 | Chrona 01: look-and-feel inventory of the legacy time-tracking-application (screens, layout, styles, interactions) | complete | chrona, order:01, legacy, ui, inventory | high |
| WI-0027 | Chrona 02: decide reconstruction versus migration, record legacy data status and rewrite the charter for the time-tracking product (CHX-011, CHX-012, CHX-300, CHX-310) | complete | chrona, order:02, legacy, decision | high |
| WI-0028 | Chrona 03: configurable data location and Chrona-owned namespace with organization manifests (CHX-DATALOC-001, CHX-024..027) | captured | chrona, order:03, data-location, depends:arca | high |
| WI-0029 | Chrona 04: GitHub sign-in through Fides and token safety (CHX-022, CHX-023) | captured | chrona, order:04, auth, depends:fides, depends:arca | high |
| WI-0030 | Chrona 05: actors, membership and capability-based authorization (CHX-030, CHX-260 remainder) | captured | chrona, order:05, authorization | high |
| WI-0031 | Chrona 06: reference data on Arca - authoritative reference records, their storage and administration across devices (CHX-040 remainder, CHX-063 remainder) | captured | chrona, order:06, reference-data | high |
| WI-0032 | Chrona 07: authoritative storage on Arca - partitioned layout, repository concurrency, integrity and manual edits (CHX-021, CHX-210, CHX-220, CHX-380, CHX-390, CHX-410, CHX-240 remainder, CHX-050 persistence) | captured | chrona, order:07, storage, depends:arca | high |
| WI-0033 | Chrona 08: offline use, persisted timer and sync reconciliation (CHX-101, CHX-102, CHX-105, CHX-200, CHX-230) | captured | chrona, order:08, offline, depends:arca | high |
| WI-0034 | Chrona 09: rebuildable derived state - indexes, totals, approval and publication projections (CHX-400) | captured | chrona, order:09, derived-state | medium |
| WI-0035 | Chrona 10: product UI remainder on Arca and Fides - sign-in, sync state and conflict resolution, persisted timer, offline and browser reliability, visual regression (CHX-003, CHX-005, CHX-320, CHX-330, CHX-340, CHX-350, CHX-360, CHX-370) | captured | chrona, order:10, ui, limen, forma | high |
| WI-0036 | Chrona 11: timesheet periods on Arca - persisted period configuration, closed periods and their effect on submission and approval (CHX-150 remainder) | captured | chrona, order:11, periods | medium |
| WI-0037 | Chrona 12: consume Summa's Chrona-to-Summa contracts package through conditor.json; Chrona.Integration and publication transport (CHX-170 remainder, CHX-180, CHX-063 remainder, CHX-450 remainder) | captured | chrona, order:12, integration, summa, contracts, depends:summa | high |
| WI-0038 | Chrona 13: observation processing - accept, modify, reject, deduplicate, receipts (CHX-190, CHX-120 import path, CHX-250 import audit) | captured | chrona, order:13, observations | medium |
| WI-0039 | Chrona 14: search, reporting and export on stored data - rebuildable search indexes and reports across people and organizations (CHX-270, CHX-280, CHX-290 remainders) | captured | chrona, order:14, reporting, folio | medium |
| WI-0040 | Chrona 15: telemetry privacy, remaining scenario tests and the production completion gate (CHX-420, CHX-430, CHX-440, CHX-001, CHX-002, CHX-004 remainders) | captured | chrona, order:15, quality, gate | medium |
| WI-0041 | Stop the Chromium install from hanging the browser-suite CI (unbounded apt-get update in playwright install --with-deps) | complete | ci, playwright, reliability | high |
| WI-0042 | Raise Forma contract gaps G1-G6 from the legacy look-and-feel inventory (sidebar/tab bar, surface radius, display title scale, inverse hero, time-column timeline, filled primary action and control radius) so Chrona's chrona-* composition can retire | captured | forma, ui, order:10 | medium |
| WI-0043 | Chrona.Domain: close the legacy rule gaps found reconciling DOMAIN-REQUIREMENTS.md (business purpose required, amendments revalidated, contiguous merge, timer nearest-minute and 30-second rule, evidence by record state, attestation statement) (CHX-012, CHX-090, CHX-103, CHX-130, CHX-160) | complete | chrona, domain, legacy, order:02b | high |
| WI-0044 | Chrona: import legacy time-tracking-application records on Arca if production records appear (CHX-300 remainder) | captured | chrona, legacy, migration, depends:arca | low |
| WI-0045 | Chrona 06a: reference data domain - clients, projects, engagements, activity types and tags with archive semantics, assignment rules, and billing references (CHX-040, CHX-080 remainder, scenario 15), in memory | complete | chrona, order:06a, reference-data, domain | high |
| WI-0046 | Chrona 10a: product UI on Limen/Forma in memory - app shell, Track (timer start/pause/resume/stop and completion), manual entry, Today ledger and reference-data administration, keeping the legacy look and feel (CHX-003, CHX-005, CHX-320, CHX-330 partial, CHX-350, CHX-360) | complete | chrona, ui, limen, forma | high |
| WI-0047 | Chrona 10b: lifecycle and review screens in memory - activity detail (amend, void, restore, split, evidence), merge, day review and attestation, month summary and the obligations queue (CHX-320, CHX-340 partial) | complete | chrona, ui, limen, forma | high |
| WI-0048 | Chrona 11a: timesheet periods in the pure domain - cadences, organization period configuration and period summaries, shown in the app in memory (CHX-150) | ready | chrona, periods, domain | medium |
| WI-0049 | Chrona 14a: search, reports and deterministic export in memory, printable through Folio (CHX-270, CHX-280, CHX-290, CHX-005) | captured | chrona, reporting, folio | medium |
