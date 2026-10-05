# Chrona decisions

Material decisions use `DF-` records under `research/decisions/`. This compact
table is a navigation view, not a replacement for those records.

| Date | Decision | Status | Rationale | Record |
|---|---|---|---|---|
| 2026-09-13 | Use ROS 2.0.1 as a measured greenfield pilot. | provisional | Test portability and operational value on a real beginning project. | Not yet promoted to a `DF-` record |
| 2026-10-05 | Declare Aegis, Forma and Folio not yet applicable. | superseded | Chrona had no .NET tier, product browser surface or report. | `DF-CHRONA-FND-2026-0001` |
| 2026-10-05 | Build Chrona the same way as the other Echelon applications: the kernel verification slice promoted to an F# engine on WebAssembly behind Limen, Aegis at its boundary, Forma and Folio from the pinned `echelon-current` releases; all foundations required. | accepted | Owner: "we want all apps built the same way … Apply it to signal and chrona too." | `DF-CHRONA-FND-2026-0002` |
