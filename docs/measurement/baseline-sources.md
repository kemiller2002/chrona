# Chrona: where the historical evidence lives

This is an index for the program's measurement work (measurement amendment,
item 4, user-approved 2026-10-13; WI-0076). It points to the evidence and
copies none of it. Commit hashes refer to `kemiller2002/chrona`. Every
backticked repository path and short commit hash here is checked to exist
(`tests/Chrona.Tests/MeasurementIndexTests.fs`, WI-0078). The record
of what was measured, and the metric definitions, are in
`kemiller2002/research-documents`.

## Evidence sources

| Source | Where | What it holds | Range |
|---|---|---|---|
| Work items | `.ros/work/queue.json` (`.ros/work/queue.md` is generated) | Each item's title, description, status and history fields | ROS installed 2026-09-13; WI-0001 to WI-0076 |
| Work context | `.ros/context/current.json` | Each item's state, evidence, conclusion and its Praxis execution ids | Same |
| Events | `.ros/events/events.jsonl` | Capture, start, checkpoint, block and complete events, with timestamps | From 2026-09-13T23:27:16.701Z (`ROS-INSTALL-2-0-1`) |
| Execution envelopes | `.ros/executions/<EXE-id>/` | Ordo execution envelopes and their events (role, capabilities, baseline revision, workspace, containment) | Three executions: WI-0073's (EXE-20261009T023556300Z-7aa2d7d5, written under Praxis 3.11.0), WI-0039's and WI-0076's. Earlier executions have none |
| Execution telemetry | `.ros/telemetry/executions/<EXE-id>.json` | Praxis's per-execution telemetry: identity, provenance, classification, metric capabilities | 71 files, EXE-20260913T232916655Z-58b69271 to EXE-20261013T094631634Z-b33f191e |
| Metric catalogue | `telemetry/metrics.json` | The provider-neutral metric ids Praxis understands | Installed with ROS |
| Decisions | `research/decisions/*.md`, generated `registries/decisions.json` | Decision records, with each change's execution in `provenance.contributions` | From 2026-10-05 |
| Gap analysis | `docs/requirements/implementation-gap-analysis.md` | Each CHX section's baseline and current status, and the WIs that changed it | From the requirements expansion |
| Handoff | `HANDOFF.md` | Current state per area, written at each WI | Rolling |
| Visual changes | `docs/legacy/visual-comparison.md`, `tests/visual/baselines/` | Why each baseline changed, and the images | From WI-0054 |
| Pull requests | GitHub, `kemiller2002/chrona` pulls | Bodies (evidence notes and, from WI-0039, a Measurement section), merge times, head SHAs | #2 (2026-09-21T14:52:32Z) to #88 |
| CI | GitHub Actions runs on `kemiller2002/chrona` | Every workflow run and check, per head SHA and event | First run 2026-09-21T14:57:17Z (Limen verify); 1,592 runs by 2026-10-13 |
| Pilot plan | `docs/PILOT-MEASUREMENT-PLAN.md` | The ROS pilot's evaluation question (draft 0.1.0) | 2026-09-13 |

## When each capability was adopted

Versions are those declared in `conditor.json` (after Conditor was adopted),
in `package.json` (npm) and in `Directory.Packages.props` (NuGet).

| Capability | Version | Commit | Date | Note |
|---|---|---|---|---|
| Repository Operating System (ROS) | 2.0.1 | `.ros/installation.json` | 2026-09-13 | Greenfield profile |
| ROS / Praxis | 3.1.4 | `1cfbd29` (#11) | 2026-10-05 | |
| Praxis, Ordo | 3.7.1, 1.4.0 | `d8833c1` (WI-0016) | 2026-10-06 | Adds `.github/workflows/praxis-validation.yml` |
| Ordo | 1.4.1 | `d07d72e` (WI-0017) | 2026-10-06 | |
| Praxis, Ordo, **Conditor** adopted | 3.7.2, 1.4.2 | `7b1338b` (WI-0018) | 2026-10-06 | `conditor.json` first declared: ordo, praxis, visual-engineering 1.0.1 |
| Ordo | 1.5.0 | `e8e7176` (WI-0024) | 2026-10-08 | echelon-current 1.2.0 |
| Praxis | 3.11.0 | `5e9f607` (WI-0073) | 2026-10-09 | Through `conditor upgrade --current` (Conditor 0.8.2; echelon-current 1.17.0, then 1.18.0) |
| Limen (npm) | 0.7.0 | `490b2a5` (WI-0010) | 2026-10-05 | Replaced `typescript-wasm-kernel` 0.4.1 |
| Limen (npm) | 0.7.1 | `054922e` (WI-0015) | 2026-10-06 | |
| Limen (npm), Limen.Routing (NuGet, `limen-fsharp`) | 0.9.0 | `c986f34` (WI-0072) | 2026-10-08 | Through Conditor |
| Forma (design system) | 0.3.0 | `f5f715a` (#12) | 2026-10-05 | |
| Forma | 0.4.1 | `054922e` (WI-0015) | 2026-10-06 | Not declared in `conditor.json` |
| Folio (print components) | 0.3.0 | `f5f715a` (#12) | 2026-10-05 | Printing used from WI-0049 (`adaafee`) |
| Aegis | n/a | `3eb127e` | 2026-10-05 | Declared not yet applicable (DF-CHRONA-FND-2026-0001) |
| Arca | 0.2.0 | `331b82b` (WI-0028) | 2026-10-08 | |
| Arca | 0.2.1 | `ef63507` (WI-0065) | 2026-10-08 | Through Conditor |
| Arca | 0.3.0 | `a468f77` (WI-0059) | 2026-10-08 | IndexedDB queue and read cache |
| Arca | 0.4.0 | `5e9f607` (WI-0073) | 2026-10-09 | Namespace tokens |
| Arca | 0.4.1 | `0438045` (WI-0073) | 2026-10-09 | |
| Fides | 0.2.0 | `ade78ac` (WI-0029) | 2026-10-08 | |
| Summa contracts | 0.1.0 | `c749a94` (WI-0037) | 2026-10-08 | |
| GitHub Pages deployment | n/a | `a1a03d4` (WI-0066) | 2026-10-08 | `.github/workflows/pages.yml` |
| Visual baselines re-rendered by a workflow | n/a | `fdc737d` (WI-0070) | 2026-10-08 | DF-CHRONA-2026-0006 |
| Gated merge (every check on the exact head, merge with `sha=`) | n/a | first gated merge `ac4cfcb` (#79) | 2026-10-08 | See "Missing" |

## Not used

As of `c715424` (2026-10-13), these have not been used in this repository:

- Ordo construction (ECIR, typed construction plans, `praxis construct`).
- Dokimos checks. The word appears only in installed Praxis schemas and docs.
- Checklist enforcement.
- OS containment: execution envelopes record `containment: semantic-only`.
- `conditor.json` has `"execution": {"enabled": false}`.

## Missing

- **Tokens and cost.** No telemetry file holds a token observation (0 of
  71). Praxis marks the `tokens.*` metrics `supported-unavailable`. The
  runtime gave the agent no per-request usage in context. Ingesting
  transcripts is pending a program decision (CONSTRUCT-008).
- **Execution envelopes before Praxis 3.11.0.** `.ros/executions/` holds
  envelopes only from WI-0073 on.
- **The merge gate's source.** `chrona-gate.sh` and `gated-merge.sh` ran
  from the agent's session scratch directory and are not in the repository.
  Their effect is visible only in PR merge records (merged with `sha=` after
  the gate passed) and in the agents' reports. #78 (`782e17a`, 2026-10-08)
  was merged with failed checks before the gate existed.
- **Wall-clock times before WI-0039.** These come only from the
  `.ros/events/events.jsonl` timestamps and PR and CI times; no separate CI
  wait or repair-cycle count was recorded.
- **The agent's own transcripts.** These are outside the repository and are
  not indexed here.
- **Praxis CI batching.** The policy PR #8 (`chore/praxis-ci-batching-20260929`)
  is open and not merged, so CI has never run under it.
