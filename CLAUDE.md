# Claude Code entry point

Read and follow [`AGENTS.md`](AGENTS.md), the canonical ROS agent contract. Provider-specific telemetry integration is documented in [`docs/development-telemetry.md`](docs/development-telemetry.md); do not duplicate its rules here.

## Standing instruction — wasm kernel log

Owner instruction, 2026-09-14. While doing any work involving
`@echelon-foundry/typescript-wasm-kernel`, record every **assumption**,
**finding**, and **difficulty** in
[`research/journals/JR-WASMKERNEL-2026-17F6--wasm-kernel-assumptions-findings-difficulties.md`](research/journals/JR-WASMKERNEL-2026-17F6--wasm-kernel-assumptions-findings-difficulties.md).

Append; do not rewrite earlier entries. State how each finding was verified —
an unverified belief is an assumption, not a finding. Record difficulties even
when worked around.

This file is ROS-managed (`.ros/installation.json`); the section above is a
deliberate project-owned divergence, recorded under WI-0004, and
`ros-bootstrap verify` will report it.
