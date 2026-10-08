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
  review, attestation, publication eligibility, reference data, periods,
  reports, inbound observations, legacy compatibility) and, on Arca 0.2.0,
  where data lives: the configured data location, Chrona's own namespace,
  one folder per organization, the organization manifest and the
  public-production refusal (`Storage`, `Organization`, WI-0028), and
  activities as partitioned Arca records with integrity on load
  (`ActivityRecord`, `Persistence`, WI-0051).
- Arca 0.2.0 and Fides 0.2.0 come from attested release assets in
  `vendor/nuget`, installed and proven by Conditor (`conditor.json`,
  `NuGet.config`).
- Sign-in with GitHub through Fides' client (WI-0029), configured by the
  deployment's document `web/chrona.deployment.json`; without an identity
  section the deployment is a local session.
- Capability-based authorization (WI-0030): every command is checked
  against the organization's roster (`Access`).
- Conflict resolution and review of outside edits (WI-0035): a change that
  no longer fits what is stored is decided again by Chrona's rules
  (`Reconcile`) and, if it still does not fit, kept under More for the
  person to resolve; records of theirs edited outside Chrona are accepted
  there after the rules of a recorded activity.
- `Chrona.Engine` and `Chrona.Application`: the Limen engine and its Aegis
  boundary; `Chrona.Wasm`: the WebAssembly shim.
- The Chrona Forma brand (`brand/chrona.brand.json`) and the legacy
  look-and-feel inventory (`docs/legacy/`).
- Requirement coverage: `docs/requirements/implementation-gap-analysis.md`;
  the ordered backlog: `docs/requirements/backlog-plan.md` and
  `./praxis work ready`.

## Not yet built

Real sign-in and storage
against a deployed Fides exchange (WI-0052), offline sync and the persisted
timer (WI-0033), derived indexes (WI-0034), browser reliability and visual
regression (WI-0054), and the Summa contract (owned and published by Summa). A deployment with a data
location keeps its records on GitHub through Arca (WI-0032); a local
deployment keeps them in the tab.

## Legacy data

No migration is needed: `kemiller2002/time-tracking-data` holds only demo
records (DF-CHRONA-2026-0002).

## Active work

See `./praxis status` and `HANDOFF.md`.
