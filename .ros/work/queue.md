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
| WI-0023 | Billing projection, review lifecycle, attestation and Summa publication eligibility (sections 7, 8, 14, 16, 17) | ready | domain,billing,approval | high |
