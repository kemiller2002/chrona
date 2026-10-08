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
  counts). 15 tested, 26 partial, 22 missing of 63 (baseline 0 / 7 / 56).
- `src/Chrona.Domain` is the pure time domain: `Diagnostics`, `Time`,
  `Activity`, `Overlap`, `ManualEntry` (WI-0020), `Ledger` (WI-0021),
  `Timer` (WI-0022), `Billing` and `Review` (WI-0023), `Compatibility`
  (WI-0027), the legacy rules R1-R6 (WI-0043) and `Reference` (WI-0045).
  29 of the 44 section-43 scenarios are tested.
- The product UI (`web/index.html`, DF-CHRONA-2026-0003) runs on Limen and
  Forma in memory: the pure engine `src/Chrona.Engine/App`, the effectful
  `Chrona.Application.Runtime`, identity and store ports with local and
  in-memory implementations. Track (timer and manual entry), Today and More
  (reference data) exist (WI-0046). The kernel verification slice moved to
  `web/kernel-slice.html`.
- Not built: GitHub storage (Arca) and sign-in (Fides), organizations and
  authorization, offline sync, observations and `Chrona.Integration` (Summa
  owns the contract), the lifecycle and review screens (WI-0047), reports
  and exports on Folio.
- The legacy `time-tracking-application` is reconstructed, not migrated
  (DF-CHRONA-2026-0001, DF-CHRONA-2026-0002): its look and feel is inventoried
  in `docs/legacy/` and expressed as the Chrona Forma brand (`brand/`), its
  rules are reconciled against `Chrona.Domain` (gaps R1-R6: WI-0043), and its
  data repository holds only demo records, so nothing is migrated.

## Next action (time domain)

The lifecycle and review screens on the same engine (WI-0047), then storage
on Arca and sign-in through Fides once their minimal slices exist (WI-0028,
WI-0029, WI-0032), and Summa's contract package (WI-0037).

## Unresolved questions

1. What concrete communication problem and user should the first slice serve?
2. What baseline workflow will be used for comparison?
3. What data, privacy, safety, and accessibility constraints apply?
4. Which outcome would distinguish useful engineering from additional process?

## Next action

Complete `PROJECT-CHARTER.md`, choose the first bounded outcome, and record its
baseline and acceptance criteria in `context/CURRENT-STATE.md`.
