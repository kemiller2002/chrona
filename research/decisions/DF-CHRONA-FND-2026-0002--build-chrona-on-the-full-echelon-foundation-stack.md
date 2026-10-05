---
id: DF-CHRONA-FND-2026-0002
title: Chrona is built the same way as the other Echelon applications, on Limen, Forma, Aegis and Folio
status: accepted
version: 1.0.0
created: 2026-10-05
updated: 2026-10-05
owners:
  - repository-governance
review_cycle: on-trigger
supersedes:
  - DF-CHRONA-FND-2026-0001
superseded_by: []
related_documents:
  - .echelon/foundations.json
  - aegis-boundaries.json
  - limen.config.json
  - package.json
  - Directory.Packages.props
  - docs/requirements/ECHELON-SHARED-APPLICATION-FOUNDATIONS.md
  - verification/kernel-slice/README.md
  - .github/workflows/build.yml
  - .github/workflows/echelon-foundations.yml
  - research/decisions/DF-CHRONA-FND-2026-0001--aegis-forma-folio-not-yet-applicable.md
tags: [governance, foundations, aegis, forma, folio, limen, architecture]
---

# DF-CHRONA-FND-2026-0002: Chrona is built the same way as the other Echelon applications

- **Date:** 2026-10-05
- **Status:** accepted
- **Decision type:** architecture and applicability (supersedes an applicability declaration)
- **Work items:** `WI-0012` (Aegis), `WI-0013` (Forma), `WI-0014` (Folio)
- **Authority:** repository owner instruction, 2026-10-05: "we want all apps built
  the same way … Apply it to signal and chrona too."

## Context

[`DF-CHRONA-FND-2026-0001`](DF-CHRONA-FND-2026-0001--aegis-forma-folio-not-yet-applicable.md)
declared Aegis, Forma and Folio `required: false` because Chrona had no
.NET/F# project, no product browser surface and no report. Its restoration
triggers were the first .NET/F# project (Aegis), the first product surface
"or the kernel slice is promoted to one" (Forma), and the first printable
summary (Folio). The owner has now directed that every Echelon application be
built the same way; under the repository's authority order that settles the
timing, and this change crosses all three triggers together.

## What "built the same way" means

The pattern is the one Summa adopted in `DF-SUMMA-FND-2026-0002`
(kemiller2002/summa#13) and Signal in `DF-SIGNAL-FND-2026-0002`, taken from
the applications fully on the foundation stack (Vigila, Forma Studio,
Tekmerion) and the Praxis foundations verifier (`src/Praxis.Cli/Foundations.fs`
at the pinned `a95dbf238e561eaac4b38ca7011efc1a496cf1c6`): every foundation
`required: true` at the `echelon-current` channel's versions (Aegis 1.0.0,
Forma 0.3.0, Folio 0.3.0, Limen 0.7.0); an F# engine on .NET WebAssembly
behind Limen's kernel through a C# `[JSExport]` shim; Aegis at the engine's
operational boundary with `aegis-boundaries.json`; Forma for presentation and
Folio for the printable projection, consumed from the pinned packages. It is
also the direction `CHRONA-REQUIREMENTS-EXPANSION.txt` §0.3-§0.4 sets: F# owns
domain and application logic, JavaScript is limited to Limen interop.

## What it is applied to: the kernel verification slice, promoted

Chrona's only browser code was the kernel verification slice, a TypeScript
engine (`verification/kernel-slice/src`) that is explicitly not a product
feature. The charter still leaves the first user and product outcome to the
owner, and Chrona's role (time) is unchanged, so this decision claims no
product territory. It promotes the slice, as `DF-CHRONA-FND-2026-0001`
anticipated, the same way Summa replaced its TypeScript engines:

- `src/Chrona.Engine` (F#, pure): `KernelSlice.fs` and `KernelSliceView.fs`,
  the slice's phases, label guard, transitions, Storage effects as data and
  projection, ported decision for decision from `domain.ts` and
  `projection.ts`. (`Chrona.Domain` and `Chrona.Time`, named by the
  requirements, stay free for the time domain.)
- `src/Chrona.Application` (F#): Limen protocol codec (including Storage
  effects and results), the handshake (Core, no packs; `HandshakeMissing` to a
  pre-1.1 kernel), and the Aegis boundary. `Wire.fs` is the old
  `transport.ts`.
- `src/Chrona.Wasm` (C#, WebAssembly SDK): a one-method `[JSExport]` shim.
- `web-kernel/limen-wasm.js` and `web/`: the `BrowserKernel` start-up (which
  also registers Folio) and the page.
- `limen.config.json` names the two F# projects as engine and the shim and the
  two browser directories as kernel (it named the TypeScript files before).
- The TypeScript sources, tests, `tsconfig.json`, the bespoke Playwright
  runner and the `typescript` dependency are removed; the history keeps them.

Every test of `domain.test.mjs` is ported assertion for assertion
(`tests/Chrona.Tests/KernelSliceTests.fs`, `TransportTests.fs`), and every
check of `browser-verification.mjs` to Playwright
(`tests/browser/kernel-slice.spec.js`). One behaviour changes deliberately:
where the TypeScript `step` threw (and the kernel logged a bridge error) for a
kernel message it could not honour (wrong protocol version, no Storage
effect, a result or capability fact it never asked for, an unknown message
kind), the F# engine now raises a classified Aegis fault
(`CHRONA.BOUNDARY.MESSAGE_INVALID` or `CAPABILITY_UNAVAILABLE`), shown through
Forma's fault component with the state unchanged. It is still refused and
never applied. An unknown event name remains a defect that fails loudly.

## Decision

1. **All foundations required.** `.echelon/foundations.json` sets Aegis
   1.0.0, Forma 0.3.0 and Folio 0.3.0 to `required: true` (Limen 0.7.0, Ordo
   and Praxis unchanged). Folio's `sourceCommit` pin is dropped because the
   dependency is now the immutable `v0.3.0` release artifact.
2. **Aegis** (`EchelonFoundry.Aegis.Core` 1.0.0, central package pin) is
   configured once and validated, and every kernel message runs inside
   `Aegis.capture` with one classifier. An invalid label, a storage failure the
   kernel reports and a stale storage result stay typed engine outcomes.
3. **Forma** (`@echelon-foundry/design-system` from the `v0.3.0` release
   tarball) is imported from the installed package (`dist/all.css`); markup
   uses record header, field, alert, fault-inline and status lozenge inside
   inert `<ef-*>` wrappers. The slice's inline `<style>` is gone; state cues
   are `data-*` attributes set by the engine (`data-phase`, `data-problem`),
   and there is no `data-bind-style`.
4. **Folio** (`@echelon-foundry/print-components` from the `v0.3.0` release
   tarball) supplies `print.css` and `register.js`; the page carries an
   `<ef-print-document>` that prints the observed round trips as a
   verification record.
5. **Verification** is part of the build (`.github/workflows/build.yml`):
   .NET tests (the ported slice tests, the Aegis boundary with a collector
   sink, foundation conformance, binding agreement), Limen `verify --strict`,
   a WebAssembly publish, and the Playwright suite.

## Consequences

- `foundations verify` checks Aegis, Forma and Folio for real; a regression in
  any of them fails the gate and the conformance tests.
- Building Chrona needs the .NET 10 SDK and Node 24 (Folio declares
  `>=24 <25`), as the other applications do.
- The first time-entry surface inherits the stack instead of introducing it.

## Alternatives considered

- **Keep the TypeScript slice and add a separate F# project for Aegis.**
  Rejected: an F# project that owns no boundary would satisfy the gate without
  meeting it, and two engines for one page is not "the same way".
- **Build a first time-entry slice now.** Rejected for this change: choosing
  the first product outcome is the owner's call (charter), and the
  instruction was about how apps are built, not what Chrona does first.
- **Pin Forma 0.2.0 and Folio commit `273b18f` as Vigila does.** Rejected: the
  instruction was to use the `echelon-current` selection, as Summa and Signal
  do.

## Revisit trigger

A new `echelon-current` channel selection for Aegis, Forma, Folio or Limen; a
Praxis foundations-verifier change; or the first Chrona product surface.
