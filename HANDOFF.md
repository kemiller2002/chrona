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
- `@echelon-foundry/typescript-wasm-kernel` 0.4.1 added as the first runtime
  dependency. It pulls in `repository-operating-system` 1.2.1 transitively
  under `node_modules/`; that is the kernel's own library dependency and is
  unrelated to the ROS 2.0.1 scaffold governing this repository.
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

## Unresolved questions

1. What concrete communication problem and user should the first slice serve?
2. What baseline workflow will be used for comparison?
3. What data, privacy, safety, and accessibility constraints apply?
4. Which outcome would distinguish useful engineering from additional process?

## Next action

Complete `PROJECT-CHARTER.md`, choose the first bounded outcome, and record its
baseline and acceptance criteria in `context/CURRENT-STATE.md`.
