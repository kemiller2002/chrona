# Chrona handoff

## Objective

Bootstrap Chrona as a greenfield Repository Operating System pilot.

## Current state

- ROS 2.0.1 greenfield profile installed on 2026-09-13. The
  `project-administration` profile is deliberately not installed: this
  repository is a contributing repository, not a central reporting hub.
- SDE execution package 1.1.1 installed under `.sde/` on 2026-09-13
  (`npx @echelon-foundry/sde init`). SDE and ROS are independent; neither
  requires the other. Files under `.sde/` are versioned methodology inputs
  and must not be hand-edited -- change them with `sde update`.
- `@echelon-foundry/typescript-wasm-kernel` 0.4.1 was added as the first
  runtime dependency on 2026-09-13. On 2026-10-05 (WI-0010) it was replaced
  by `@echelon-foundry/limen` pinned exactly at 0.7.0 (the same library under
  its new package name), and `verification/kernel-slice/` was adapted to the
  0.7.0 protocol. Limen 0.7.0 has no runtime dependencies; the transitive
  `repository-operating-system` 1.2.1 that 0.4.1 pulled in is gone. On
  2026-10-06 (WI-0015) Limen moved to 0.7.1 and Forma to 0.4.1, the
  `echelon-current` registry selections.
- Project charter is a draft.
- No first vertical slice, evidence record, hypothesis, or experiment has been
  accepted.
- The operating system is under evaluation.

## Validation

Run:

```bash
./ros registry check
./ros validate
npx @echelon-foundry/sde verify
```

All three passed locally on 2026-09-13. `./ros validate` enforces work-item
attribution, so a meaningful change with no active or completed work item
fails it; see the Work Protocol section of `AGENTS.md`.

CI runs the first two automatically via `.github/workflows/ros-validation.yml`
on every push and pull request. Those runs failed from repository creation
until 2026-09-14 because GitHub never dispatched a runner; an owner settings
change fixed it, and both branches have been green since
`a117b1796ee31c890bbde0fd7b2fb00e7156e7e3`. See `.ros/work/items/WI-0003.md`
for the diagnosis, the evidence, and one unexercised watch item.

## Time domain (2026-10-07)

- Requirement coverage per section is tracked in
  [`docs/requirements/implementation-gap-analysis.md`](docs/requirements/implementation-gap-analysis.md)
  (`CHX-NNM` ids, baseline and current columns; `GapAnalysisTests` holds the
  counts). 29 tested, 31 partial, 3 missing of 63 (baseline 0 / 7 / 56).
- `src/Chrona.Domain` is the pure time domain: `Diagnostics`, `Time`,
  `Activity`, `Overlap`, `ManualEntry` (WI-0020), `Ledger` (WI-0021),
  `Timer` (WI-0022), `Billing` and `Review` (WI-0023), `Compatibility`
  (WI-0027), the legacy rules R1-R6 (WI-0043), `Reference` (WI-0045),
  `Periods` (WI-0048), `Reports` (WI-0049), `Observations` (WI-0050), and,
  on Arca 0.2.0, `Organization` and `Storage` (WI-0028): the configured data
  location, Chrona's namespace, per-organization folders and repositories,
  the organization manifest and the public-production refusal, tested
  against Arca's in-memory provider (which passes Arca's conformance suite);
  `ActivityRecord` and `Persistence` (WI-0051): activities as partitioned
  Arca records, one command per commit under revision checks, integrity on
  load, and manual edits held for review.
  35 of the 44 section-43 scenarios are tested.
- The product UI (`web/index.html`, DF-CHRONA-2026-0003) runs on Limen and
  Forma in memory: the pure engine `src/Chrona.Engine/App`, the effectful
  `Chrona.Application.Runtime`, identity and store ports with local and
  in-memory implementations. Track (timer and manual entry), Today and More
  (reference data) exist (WI-0046); so do the activity screen (correct,
  remove and restore, split, evidence, history), merge, the day review and
  attestation, the month and the obligations queue (WI-0047), timesheet
  periods (WI-0048) and search, reports, deterministic CSV/JSON export and a
  Folio print document (WI-0049). The kernel
  verification slice moved to `web/kernel-slice.html`.
- Arca 0.2.0 is a Conditor-installed project binding: attested release
  assets in `vendor/nuget` (`arca.lock`), mapped in `NuGet.config`, pinned
  in `Directory.Packages.props`, with the echelon-current 1.7.0 authority in
  `.conditor/`.
- Sign-in (WI-0029, DF-CHRONA-2026-0004): Fides 0.2.0's own client runs in
  `Chrona.Application.Identity`, every browser service it needs a Limen
  request (core Http and Storage, and Chrona's `chrona.host` pack). The
  deployment's document `web/chrona.deployment.json`
  (`docs/deployment-configuration.md`) names the exchange and client id;
  the repository's copy is a local session. Tested against a fake exchange
  and GitHub; the real check waits on a deployment (WI-0052).
- Authorization (WI-0030): `Access` principals, the fifteen capabilities
  and per-organization rosters; the engine checks every command by
  capability (`Update.requirement`). The session's person founds and
  administers the in-memory organization until rosters are stored (WI-0031).
- Storage on GitHub (WI-0032, DF-CHRONA-2026-0005): `Chrona.Application.Store`
  runs Arca's provider (the GitHub adapter through the bridge, tokens from
  Fides) behind the store port: it opens the organization's records after
  sign-in, writes one commit per command conditioned on the repository
  state, reloads and decides again when it moved, and holds records edited
  outside Chrona. A local deployment keeps records in the tab. Tested
  against Arca's in-memory provider; the real check is WI-0052.
- Conflicts and outside edits (WI-0035): Chrona's rules decide a change
  again on what is stored (`Reconcile`); what no longer fits is kept under
  More with what diverged and a stable code, never dropped. The person keeps
  what is stored, redoes theirs on the current version through the usual
  forms, or tries again when the repository only kept moving. A record of
  theirs edited outside Chrona is reviewed and accepted there, after the
  rules of a recorded activity, and stored as Chrona's next revision.
  Arca WI-0018 asks for namespace-scoped change tokens; until then a
  dedicated data repository avoids needless reloads.
- Offline (WI-0033): every change goes through Arca's offline queue, kept
  write-ahead in this browser's localStorage (Arca's LocalStorageQueue over
  Limen Storage) and sent in order; a lost connection backs off and
  retries, the queue survives refresh and restart and is sent after the
  records open, and a change the repository moved under is decided again
  and revised or becomes a conflict. The sync state is shown. Opening the
  records still needs GitHub; the persisted timer and starting offline are
  WI-0055.
- Rosters, people and organizations (WI-0031): member records in each
  organization's folder; only the configuration's listed administrators
  set an organization up or administer it first (WI-0053); only members work;
  administrators add people and change their access under More; a
  deployment lists its organizations and a person chooses one. Publication
  staging (ReadyForPublication) and Summa's invoice report
  (InvoicedExternally) are domain transitions; their transport is WI-0037.
- Not built: the persisted timer and starting offline (WI-0055), derived
  indexes (WI-0034), browser reliability, accessibility end to end, quick
  entry and visual regression (WI-0054),
  `Chrona.Integration` (Summa owns the contract), submission and approval screens (need actors and
  authorization, WI-0030 after Fides), and reports over stored data across
  people and organizations (WI-0039).
- The legacy `time-tracking-application` is reconstructed, not migrated
  (DF-CHRONA-2026-0001, DF-CHRONA-2026-0002): its look and feel is inventoried
  in `docs/legacy/` and expressed as the Chrona Forma brand (`brand/`), its
  rules are reconciled against `Chrona.Domain` (gaps R1-R6: WI-0043), and its
  data repository holds only demo records, so nothing is migrated.

## Next action (time domain)

Summa's contract package (WI-0037) once Summa publishes it; offline use
(WI-0033); the real sign-in and storage check once a deployment exists
(WI-0052).

## Unresolved questions

1. What concrete communication problem and user should the first slice serve?
2. What baseline workflow will be used for comparison?
3. What data, privacy, safety, and accessibility constraints apply?
4. Which outcome would distinguish useful engineering from additional process?

## Next action

Complete `PROJECT-CHARTER.md`, choose the first bounded outcome, and record its
baseline and acceptance criteria in `context/CURRENT-STATE.md`.
