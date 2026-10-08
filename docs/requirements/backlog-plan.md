# Chrona backlog plan

Captured 2026-10-08 (WI-0025) from the missing and partial rows of [`implementation-gap-analysis.md`](implementation-gap-analysis.md). Build order, per the user (DF-CHRONA-2026-0001): **Chrona first**, then Summa, then the rest. The minimal Arca slices (kemiller2002/arca, slices 1-6, plus 8-9 for integrity and offline) and Fides slices (kemiller2002/fides, slices 1-7) come before Chrona's storage and sign-in items. `GapAnalysisTests` fails if any row that is not yet `tested` is not named by an open work item.

| Order | Work item | Slice | Depends on |
|---:|---|---|---|
| 1 | WI-0026 | Chrona 01: look-and-feel inventory of the legacy time-tracking-application (screens, layout, styles, interactions) | - |
| 2 | WI-0027 | Chrona 02: decide reconstruction versus migration and record legacy data status (CHX-012, CHX-300, CHX-310) | WI-0026 |
| 2b | WI-0043 | Chrona.Domain: close the legacy rule gaps R1-R6 found reconciling DOMAIN-REQUIREMENTS.md (DF-CHRONA-2026-0002) | WI-0027 |
| 3 | WI-0028 | Chrona 03: configurable data location and Chrona-owned namespace with organization manifests (CHX-DATALOC-001, CHX-024..027) | arca slice 2 (data location, ARCA-LOC) and arca slice 3 (record format, ARCA-REC) (data location and record format) |
| 4 | WI-0029 | Chrona 04: GitHub sign-in through Fides and token safety (CHX-022, CHX-023) | fides slice 7 (WASM client and Arca token provider, FID-CLI) (WASM client and Arca token provider) and arca slice 5 (token-provider port, ARCA-AUTH) (token-provider port) |
| 5 | WI-0030 | Chrona 05: actors, membership and capability-based authorization (CHX-030, CHX-260 remainder) | WI-0029 |
| 6 | WI-0031 | Chrona 06: reference data - clients, projects, activity types, tags, rate references and billing class (CHX-040, CHX-080 remainder, CHX-063 remainder) | WI-0028 |
| 7 | WI-0032 | Chrona 07: authoritative storage on Arca - partitioned layout, repository concurrency, integrity and manual edits (CHX-021, CHX-210, CHX-220, CHX-380, CHX-390, CHX-410, CHX-240 remainder) | WI-0028, WI-0030, arca slice 6 (GitHub adapter, ARCA-API/OUT) (GitHub adapter) and arca slice 8 (integrity, ARCA-INT) (integrity) |
| 8 | WI-0033 | Chrona 08: offline use, persisted timer and sync reconciliation (CHX-101, CHX-102, CHX-105, CHX-200, CHX-230) | WI-0032 and arca slice 9 (offline queue, ARCA-OFF) (offline queue) |
| 9 | WI-0034 | Chrona 09: rebuildable derived state - indexes, totals, approval and publication projections (CHX-400) | WI-0032 and arca slice 10 (derived indexes and migration, ARCA-MIG) |
| 10 | WI-0035 | Chrona 10: conflict resolution and review of outside edits in the application (CHX-260, CHX-410, CHX-320 remainders) | WI-0032 and WI-0029 |
| 11 | WI-0036 | Chrona 11: timesheet periods (CHX-150) | WI-0032 |
| 12 | WI-0037 | Chrona 12: consume Summa's Chrona-to-Summa contracts package through conditor.json; Chrona.Integration and publication transport (CHX-170 remainder, CHX-180, CHX-063 remainder, CHX-450 remainder) | summa item "Summa 01: Chrona-to-Summa contracts package" (the contracts package; the receiving application owns the contract, so Summa owns it, user decision 2026-10-08) and WI-0032 |
| 13 | WI-0038 | Chrona 13: observation processing - accept, modify, reject, deduplicate, receipts (CHX-190, CHX-120 import path, CHX-250 import audit) | WI-0032 |
| 14 | WI-0039 | Chrona 14: search, reporting and export with Folio (CHX-270, CHX-280, CHX-290) | WI-0034 and WI-0035 |
| 16 | WI-0054 | Chrona 16: browser reliability, accessibility end to end, quick-entry conveniences and visual regression (CHX-330, CHX-350, CHX-360, CHX-370) | WI-0033 |
| 15 | WI-0040 | Chrona 15: telemetry privacy, remaining scenario tests and the production completion gate (CHX-420, CHX-430, CHX-440, CHX-001, CHX-002, CHX-004 remainders) | WI-0035, WI-0037, WI-0038 and WI-0039 |

The backlog itself lives in `.ros/work/queue.json` and is managed only through the Praxis CLI. This table is a readable snapshot from when the slices were captured.

Added 2026-10-08 by WI-0027: WI-0043 (above) and WI-0044, the legacy import on Arca, captured only so it is not forgotten; it runs after WI-0032 and only if production legacy records appear.

Added 2026-10-08 by WI-0035: the product UI built in memory (WI-0046, WI-0047) left WI-0035 with the write path's conflict resolution and outside-edit review; its browser reliability, accessibility, quick-entry and visual-regression remainder moved to WI-0054.
