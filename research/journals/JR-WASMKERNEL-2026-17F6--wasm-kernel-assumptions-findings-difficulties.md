---
id: JR-WASMKERNEL-2026-17F6
title: Running log — assumptions, findings, and difficulties using the wasm kernel
research_area: wasm-kernel
author_agent: claude-code
created: 2026-09-14
updated: 2026-09-14
status: active
related_mission:
related_package:
evidence_ids: []
hypothesis_ids: []
theory_ids: []
tags: [wasm-kernel, typescript-wasm-kernel, running-log, assumptions, findings, difficulties]
---

# Running log — wasm kernel

## Objective

Maintain a durable, append-only record of every **assumption**, **finding**,
and **difficulty** encountered while using
`@echelon-foundry/typescript-wasm-kernel` in Chrona.

Standing instruction from the repository owner, 2026-09-14: from that date
forward, all three categories are written here rather than left in
conversation. This entry is therefore long-lived and updated in place; it is
not a one-shot investigation record.

## How to use this log

- Append to the dated sections below. Do not rewrite earlier entries; if an
  entry turns out to be wrong, add a correcting entry and cross-reference it.
- Every **finding** states how it was verified. An unverified belief is an
  **assumption**, not a finding — keep the distinction strict, per the Tool
  Honesty rules in `docs/00-governance/Agent-Operating-Manual.md`.
- Record a **difficulty** even when it was worked around. The workaround is
  part of the record.
- Entries seeded on 2026-09-14 predate the standing instruction by a few
  hours; they are included because they were established in the same session
  and would otherwise be lost. They are marked `[seeded]`.

## Starting state

- `@echelon-foundry/typescript-wasm-kernel@0.4.1` installed as the sole
  runtime dependency (`^0.4.1`), commit `f3d417f`.
- No application code exists in Chrona. The kernel has never been run in a
  browser from this repository.
- Reference source cloned read-only to
  `/home/user/kemiller2002/typescript-wasm-kernel` at commit `43e56ec`
  ("Release 0.4.1 with ROS attribution").

---

## Findings

### F1 — The published package omits its own documentation `[seeded]`

`package.json` `files` is `["dist", "README.md", "LICENSE",
"architecture.yaml"]`. `docs/USAGE.md`, `docs/ROADMAP.md`, and `CLAUDE.md`
are **not** in the npm tarball, though `README.md` links to them.

*Verified:* `ls -R node_modules/@echelon-foundry/typescript-wasm-kernel/`.

*Consequence:* the bridge's usage contract cannot be read from an install
alone. See D1.

### F2 — The cloned source matches the installed version exactly

Clone HEAD `43e56ec` is the `0.4.1` release commit; `package.json` version in
the clone and in `node_modules` both read `0.4.1`; and
`src/kernel/browser-kernel.ts:234` contains the same
`#applyScope(instance.scope, item)` call as the shipped `dist`.

*Verified:* `git log -1`, two `grep` calls on the respective `package.json`
files, and a spot-check `grep` of source against compiled output.

*Consequence:* documentation read from the clone describes the installed
behavior. This was checked specifically because a clone of `main` could
otherwise have been ahead of the published release.

### F3 — `data-if` and `data-each` resolve bindings against different scopes

Inside a `data-each` template, bindings resolve against **the item record**,
not the outer view: `#applyEach` calls `#applyScope(instance.scope, item)`.
Inside a `data-if` template they resolve against the outer view:
`#applyIf` calls `#applyScope(binding.mounted.scope, view)`.

A `data-if` nested inside a `data-each` inherits the item scope, since the
item is what gets passed down as `view` at that point.

*Verified:* read `dist/kernel/browser-kernel.js` (`#applyEach`, `#applyIf`),
corroborated by `docs/USAGE.md`'s repeated-content example.

*Consequence:* this asymmetry is easy to get wrong and is not called out as a
caution in the docs. Treat it as a likely defect source.

### F4 — `data-each` cannot nest

`ViewItem` is `{ readonly [field: string]: ViewPrimitive }`. An item can hold
only string/number/boolean, so it structurally cannot contain the array a
nested `data-each` would need. One level of repetition, enforced by the type
system.

*Verified:* read `dist/protocol.d.ts`.

### F5 — Native HTML validation gates dispatch

`#fire` calls `el.reportValidity()` on a form and returns early if it fails,
so a malformed value never reaches the engine.

*Verified:* read `#fire` in `dist/kernel/browser-kernel.js`; reference
`index.html` relies on it with `type="email" required`.

### F6 — A timeout is always `OutcomeUnknown`, never `Failure`

`#classifyAbort` maps `signal.reason === "timeout"` to
`{kind: "OutcomeUnknown", reason: "timeout-after-dispatch"}`. The source
comment states the reason: `fetch()` may already have sent the request, so
the kernel cannot claim the effect did not occur.

*Verified:* read `#runHttp` / `#classifyAbort`.

### F7 — Bindings are scanned once

`start()` scans `document.body`; the only other binding pass is on template
instantiation inside `#applyIf` / `#applyEach`. There is no observer and no
re-scan.

*Verified:* read `start()`, `#bind`, `#bindElement`.

*Consequence:* DOM inserted by anything other than a `data-if` / `data-each`
template is invisible to the bridge.

### F8 — Diagnostics deliberately excludes request headers and body

`#executeEffect` reports only `correlationId` and `durationMs`. `docs/USAGE.md`
states this is enforced by `test/kernel.test.ts` ("a diagnostics sink never
receives request headers or body") because headers commonly carry credentials.

*Verified:* read `#executeEffect`; the test claim itself is **read, not run**
— see A4.

### F9 — `architecture.yaml` warns that most of its own claims are not machine-checked

Only `browser_interop.forbidden_modules` and `dynamic_types` are verified by
`scripts/check-architecture.ts`, and `dynamic_types` is implemented as a
blanket ban on the words "any"/"dynamic" anywhere under `src/engine` — cruder
than the file's own `allowed_modules` phrasing suggests. `state.*`,
`effects.representation`, and `capabilities.ambient_authority` rest on the
TypeScript compiler and code review.

*Verified:* read the header comment of `architecture.yaml`.

*Consequence:* do not cite `architecture.yaml` as evidence that a property is
mechanically enforced.

### F10 — The kernel's transitive ROS dependency is not the repository's ROS

`typescript-wasm-kernel@0.4.1` depends on
`@echelon-foundry/repository-operating-system@^1.2.1`, resolved to `1.2.1`
under `node_modules`. Chrona's own scaffold is ROS `2.0.1`.

*Verified:* `npm ls`, and the kernel's `package.json` `dependencies`.

*Consequence:* two different major versions of ROS coexist by design. Do not
"reconcile" them.

---

## Assumptions

### A1 — That `0.4.1` is a safe version to build against

It is the latest published version, but it is `0.x`. No stability policy for
pre-1.0 minor bumps has been read. A `0.5.0` could break the protocol.
`PROTOCOL_VERSION` is `1` and `Initialize` rejects a mismatch, so a protocol
break would at least fail loudly rather than silently.

*Unverified:* whether the project intends `0.x` minors to be breaking.

### A2 — That the kernel actually works in a browser from this repository

Never demonstrated here. What was demonstrated is only that the module
resolves and exports its documented names under Node:

```
BrowserKernel, DirectTypeScriptTransport, PROTOCOL_VERSION, ReferenceEngine, project
```

There is no HTML, no build, no server, and no browser run in Chrona. Any
claim beyond "it imports" is currently an assumption.

### A3 — That `ReferenceEngine` is demonstration code, not a starting point

`State`, `Command`, and `project` implement one specific feature (email
availability checking). Treating it as a template to extend rather than a
sample to replace is an assumption; the README calls it "the example," which
supports but does not settle it.

### A4 — That the kernel's own test suite passes

`docs/USAGE.md` asserts a test enforces the diagnostics secret-hygiene
invariant (F8). That test has been **read about, not executed**. No kernel
test has been run from Chrona.

### A5 — That `DirectTypeScriptTransport` is the right transport to start on

`EngineTransport` is documented as the WASM swap point, and the direct
transport is the only one shipped, so it is the only available choice today.
Whether a WASM transport changes the authoring model is unknown.

---

## Difficulties

### D1 — Bridge contract not obtainable from the install `[seeded]`

Because of F1, understanding the bridge required cloning the source repo.
This is friction for any consumer who installs from npm and expects the
linked docs to be present.

*Workaround:* read `dist/kernel/browser-kernel.js` (complete and readable),
then clone `kemiller2002/typescript-wasm-kernel` for `docs/USAGE.md`.

### D2 — ROS work-item attribution blocks commits `[seeded]`

`./ros validate` failed with 21 errors on the first attempt to commit the
toolchain install, because meaningful changed paths had no active or
completed work item. Not a defect — the enforcement is the point — but it is
a real workflow cost: every change needs a work item opened *before* the
work, or retrofitted after.

*Workaround:* capture via `./ros add`, promote with
`work backlog-transition --action ready`, then `work start`.

### D3 — F# CLI argument syntax differs from the documented Node syntax `[seeded]`

`./ros work ready WI-0001` and `./ros work start WI-0001` both fail; the F#
CLI requires `--id` and an explicit `--occurred-at`. `docs/work-protocol.md`
still shows the positional Node form. `AGENTS.md` documents the divergence,
but only after the failure has already been hit.

### D4 — Backlog `--tag a,b` is not split

`./ros add ... --tag setup,dependencies` stored a single tag
`"setup,dependencies"`. Repeating `--tag` works. Cosmetic, but it silently
produces the wrong data rather than erroring.

*Verified:* compare `WI-0001` tags to `WI-0004` tags in `.ros/work/queue.json`.

### D5 — No end-to-end verification path exists in Chrona yet

The kernel's reference feature needs `GET /api/email-availability` and a
served page. Chrona has neither, so nothing exercises the bridge. Until a
vertical slice exists, every behavioral claim about the kernel in this
repository is read-derived, not observed.

### D6 — Work type is chosen before the deliverable is known, and cannot be changed

`work start --type feature` was used for the work that produced this journal.
`ros.json` `completionEvidence` maps every type except `research` and
`mechanical` to the default `["implementation", "tests"]`, so completion was
refused:

```
ERROR completion evidence missing for 'WI-0004': implementation, tests
```

The deliverable is a research record, so the honest type was `research`
(`["research-record"]`). The CLI offers no way to retype an in-flight item,
and supplying a file as "implementation" evidence to satisfy the check would
be fabricated evidence, which the Agent Operating Manual forbids outright.

*Workaround:* block `WI-0004` with the misclassification as its stated reason,
open `WI-0005` typed `research`, and complete that. The false start stays in
the record rather than being erased.

*Consequence:* classify the deliverable before `work start`, not after. The
type is effectively immutable once work begins.

---

## Highest-value next step

Build one minimal vertical slice in Chrona that actually runs the bridge in a
browser, to convert A2 from assumption to finding. Until then, treat every
behavioral statement about the kernel here as derived from reading its source,
not from observing it run.
