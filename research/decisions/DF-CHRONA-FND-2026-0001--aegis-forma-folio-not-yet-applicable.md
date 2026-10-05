---
id: DF-CHRONA-FND-2026-0001
title: Aegis, Forma and Folio are declared not yet applicable until Chrona's first product slice
status: superseded
version: 1.0.1
created: 2026-10-05
updated: 2026-10-05
owners:
  - repository-governance
review_cycle: on-trigger
supersedes: []
superseded_by:
  - DF-CHRONA-FND-2026-0002
related_documents:
  - .echelon/foundations.json
  - docs/requirements/ECHELON-SHARED-APPLICATION-FOUNDATIONS.md
  - docs/requirements/CHRONA-REQUIREMENTS-EXPANSION.txt
  - verification/kernel-slice/README.md
  - .github/workflows/echelon-foundations.yml
tags: [governance, foundations, aegis, forma, folio, applicability]
---

# DF-CHRONA-FND-2026-0001 — Aegis, Forma and Folio are not yet applicable

- **Date:** 2026-10-05
- **Status:** superseded by [`DF-CHRONA-FND-2026-0002`](DF-CHRONA-FND-2026-0002--build-chrona-on-the-full-echelon-foundation-stack.md) (2026-10-05). The owner directed that every Echelon application be built the same way: "we want all apps built the same way … Apply it to signal and chrona too." Kept for history; it no longer governs.
- **Decision type:** applicability declaration (temporary, with restoration triggers)
- **Work item:** `FOUNDATIONS-APPLICABILITY`

## Context

`.echelon/foundations.json` declared Aegis, Forma and Folio `required: true`.
The Echelon foundations gate (Praxis `foundations verify`, pinned at
`a95dbf238e561eaac4b38ca7011efc1a496cf1c6`) therefore failed on `main` and on
every branch with:

```
[FAIL] aegis    installed=False pinned=False used=False evidence=False expected=1.0.0
[FAIL] forma    installed=False pinned=False used=False evidence=False expected=0.2.0
[FAIL] folio    installed=False pinned=False used=False evidence=False expected=0.3.0
ECHELON-FND-AEGIS-001 aegis is required but is not installed/declared.
ECHELON-FND-FORMA-001 forma is required but is not installed/declared.
ECHELON-FND-FOLIO-001 folio is required but is not installed/declared.
```

What the verifier counts (Praxis `src/Praxis.Cli/Foundations.fs` at that
commit):

| Capability | Installed | Used | Evidence |
|---|---|---|---|
| Aegis | a `PackageReference` to `EchelonFoundry.Aegis.Core` in a `.fsproj`/`.csproj`/`.props`/`.targets` | source contains `open Aegis`, `Aegis.capture`/`guard`, `Bootstrap.validate` or `Sinks.Collector` | the boundary manifest (`aegis-boundaries.json`) exists |
| Forma | `@echelon-foundry/design-system` in `package.json` | source references the package, `design-system/all.css` or an `<ef-*>` control | same as used |
| Folio | `@echelon-foundry/print-components` in `package.json` | source references the package, `<ef-print-*>`, `print.css` or `register` | same as used |

## Evidence: Chrona has no product code these capabilities govern

The portfolio has recorded Chrona's state in
`echelon-organization-administration`,
`application-governance/ENGINEERING-QUALITY-DECISION-INVENTORY.md`:
QDI-079 — Chrona is the time-entry/time-tracking application; its charter and
`context/CURRENT-STATE.md` are still template text; "the only code is a
TypeScript Limen verification slice (`verification/kernel-slice/src/*.ts`). It
has no F#/.NET project and no time library." QDI-036 asks for the foundation
baseline to be corrected "before the first vertical slice".

The repository confirms it:

- **Aegis.** There is no `.fsproj`, `.csproj` or `.fs` file. Aegis is a .NET
  package (`EchelonFoundry.Aegis.Core`); `ECHELON-SHARED-APPLICATION-FOUNDATIONS.md`
  §2.1 scopes it to "every .NET/F# host that owns an operational boundary".
  `CHRONA-REQUIREMENTS-EXPANSION.txt` §0.4 plans `Chrona.Domain`,
  `Chrona.Engine` and `Chrona.Wasm`; none exists yet, and neither does the
  planned `Chrona.Time` library (QDI-079 amendment).
- **Forma.** The only browser page is `verification/kernel-slice/index.html`.
  Its README states "This is not a Chrona product feature": it exists only to
  prove that the Limen kernel works in a real browser, and its markup is
  deliberately shaped as a probe (for example the `minlength` mismatch that
  makes the native validity gate observable). Forma governs "interactive web
  application UI" (§3); presentation is genuinely outside this fixture's
  boundary, which is the condition §1 sets for marking a capability not
  applicable. The first time-entry UI is where Forma applies.
- **Folio.** Folio's Chrona scope (§4) is time reports, printable summaries,
  paper/PDF exports and invoice-supporting documents. None exists.

Installing the packages now would mean an unused dependency, an empty
project, or presentation dependencies inside a Limen test fixture. The
verifier's `used` check exists to reject the first two ("merely adding a
package does not satisfy the contract", Praxis
`docs/application-foundations.md`), and the third would make the fixture claim
product territory its README deliberately refuses.

## Policy basis

- **Repository requirement.** `docs/requirements/ECHELON-SHARED-APPLICATION-FOUNDATIONS.md`
  §1: requirements are inherited "when the capability is applicable … An
  implementation MAY mark one of Aegis, Forma, or Folio not applicable only
  when the capability is genuinely outside that feature's boundary. The reason
  MUST be explicit and reviewable." This record is that reason.
- **Verifier contract.** Praxis `docs/application-foundations.md`:
  applications own applicability; a capability declared `required: false` is
  reported as `N/A`, not PASS. The foundations schema
  (`echelon-foundations-v1.schema.json`) has no reason field
  (`additionalProperties: false`), so the reason lives here.
- **Portfolio inventory.** `QUALITY-REMEDIATION-INVENTORY.md` CHR-F2:
  remediation "Mark capabilities not-applicable with reasons until a slice
  needs them, or install them"; XC-16: "foundations must be justified by
  artifacts present".
- **QDI-036**: correct the foundation baseline before product code grows. This
  decision makes the declaration truthful and the gate meaningful now, and the
  triggers below bind installation to the first slice that needs each
  capability.

## Decision

In `.echelon/foundations.json`, set `aegis`, `forma` and `folio` to
`required: false`. Keep their `version`, `sourceCommit` and `boundaryManifest`
values unchanged so the baseline to restore is not lost.

This does **not** relax any requirement in
`ECHELON-SHARED-APPLICATION-FOUNDATIONS.md` or
`CHRONA-REQUIREMENTS-EXPANSION.txt` §0.5. Those apply in full to the first
code that owns the boundary in question.

## Restoration triggers (each flips the capability back to `required: true` in the same change)

1. **Aegis:** the first .NET/F# project is added (`Chrona.Domain`,
   `Chrona.Engine`, `Chrona.Wasm`, `Chrona.Time` or any host). That change must
   reference `EchelonFoundry.Aegis.Core` 1.0.0 (or the then-current baseline),
   use it at the operational boundary, and add `aegis-boundaries.json`.
2. **Forma:** the first interactive browser surface that is a Chrona product
   feature (for example time entry) is added, or the kernel slice is promoted
   to one. It must consume the pinned `@echelon-foundry/design-system`.
3. **Folio:** the first time report, printable summary or paper/PDF export is
   added. It must consume the pinned `@echelon-foundry/print-components`.

Reviewers of a change that crosses a trigger should reject it if
`foundations.json` still says `required: false` for that capability.

## Consequences

- `foundations verify` reports Aegis, Forma and Folio as `N/A`. The gate then
  reports only real drift in Limen, Ordo or Praxis. (At the time of this
  decision it still reports ECHELON-FND-PRAXIS-002: `.echelon/ros.json`
  `installedVersion` 3.1.3 against the declared 3.1.4. That finding is
  separate and is not addressed here.)
- The gate cannot itself detect that a trigger was crossed. That gap is
  portfolio-wide (XC-16) and is mitigated by the review rule above.

## Revisit trigger

Any restoration trigger above; a Praxis foundations schema change that adds an
explicit deferred/not-applicable state with reason (XC-16); or a QDI-079
review trigger (charter update, re-profiling, `Chrona.Time` implemented).
