# Chrona current state

## Product

Chrona is the Echelon time-tracking application: it records, corrects,
reviews, reports and publishes business time (`PROJECT-CHARTER.md`). It is a
reconstruction of the legacy `time-tracking-application`, keeping its look
and feel and its rules, rebuilt on Limen, Forma, Aegis and Folio
(DF-CHRONA-2026-0001, DF-CHRONA-2026-0002).

## What exists

- `Chrona.Domain`: the pure time domain (activity record, manual entry,
  time zones, overlap, the revision-safe ledger, the timer, billing,
  review, attestation, publication eligibility, legacy compatibility).
- `Chrona.Engine` and `Chrona.Application`: the Limen engine and its Aegis
  boundary; `Chrona.Wasm`: the WebAssembly shim.
- The Chrona Forma brand (`brand/chrona.brand.json`) and the legacy
  look-and-feel inventory (`docs/legacy/`).
- Requirement coverage: `docs/requirements/implementation-gap-analysis.md`;
  the ordered backlog: `docs/requirements/backlog-plan.md` and
  `./praxis work ready`.

## Not yet built

Storage through Arca and sign-in through Fides (their minimal slices come
first), offline sync, the Summa contract (owned and published by Summa),
observations, reporting on Folio.

## Legacy data

No migration is needed: `kemiller2002/time-tracking-data` holds only demo
records (DF-CHRONA-2026-0002).

## Active work

See `./praxis status` and `HANDOFF.md`.
