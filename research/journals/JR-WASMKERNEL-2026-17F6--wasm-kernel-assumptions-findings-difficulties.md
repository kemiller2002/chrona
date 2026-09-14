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

### F11 — A form's flush list fires every `data-event` inside it, buttons included

`#bindEvent` pushes an element onto the form's flush list whenever
`trigger !== "submit" && form !== null`. `"form" in el` is true for
`HTMLButtonElement`, so **any** `<button data-event="...">` inside a `<form>`
is fired on every submit of that form — not only "pending change-bound
fields" as `docs/USAGE.md` describes it.

*Verified by observation, 2026-09-14:* with a
`<button type="button" data-event="load">` inside the slice's form, clicking
Save produced log entries `load requested` / `Loading: Success` and left the
phase `Loading`, so the subsequent `save` was rejected as illegal from that
phase. The status read `Nothing stored yet.` and `localStorage` was still
`null`. Moving the button outside the form fixed it, with no other change.

*Consequence:* an action button that is not a form control must live outside
the form, or it becomes a hidden side effect of submitting. The failure is
silent — no bridge error, no console error — and presents as "save is broken"
rather than "load fired."

*Methodological note:* the flush code was **read** during the 2026-09-14
review and this behavior was not noticed; it surfaced within minutes of
running. This is a direct instance of the caution in
`.sde/method/AGENT-EXECUTION-RULES.md` — a green read is not behavioral proof.

### F12 — Consumers need an import map; the compiled output keeps the bare specifier

`tsc` emits `import { BrowserKernel } from "@echelon-foundry/typescript-wasm-kernel"`
unchanged, and browsers cannot resolve a bare specifier. Loading the page
without an import map fails at module resolution.

*Verified:* inspected `dist/main.js` after compilation; page loads only once
an `importmap` mapping the package name to
`/node_modules/.../dist/index.js` is present.

*Consequence:* `docs/USAGE.md` step 6 (`npm run build; python3 -m http.server`)
works for the kernel's own repository, whose imports are relative, but is
insufficient for a consumer installing from npm. A bundler or an import map is
required and is not mentioned.

### F13 — The kernel runs correctly in a real browser from this repository (A2 resolved)

14 checks pass against real Chromium, covering: initial projection;
`data-bind-disabled` reflected as a DOM property; `data-event` +
`data-on="input"` per keystroke; `data-if` mount and unmount; the engine guard
shadowing native validation; native `reportValidity` gating dispatch when
reachable; a complete `Storage` effect round trip (request → kernel → result →
transition); the value genuinely present in `localStorage`; `data-each`
rendering from item scope; state surviving reload; keyed reconciliation
preserving DOM node identity; and no bridge or page errors across the run.

*Verified:* `npm run verify:slice`
(`verification/kernel-slice/test/browser-verification.mjs`), screenshot at
`verification/kernel-slice/browser-verification.png`.

*Consequence:* **A2 is withdrawn as an assumption.** Behavioral claims about
the primitives this slice exercises are now observed. Claims about the `Http`
path remain read-derived — see A6.

### F14 — An engine-driven `data-bind-disabled` can shadow native validation entirely

Where the projection disables the submit control for the same condition HTML
would reject, `reportValidity` is never reached: the button is already
disabled. The two mechanisms overlap silently and the engine guard wins.

*Verified by observation:* an empty field left `#save` disabled, so a click
never produced a native validation gate. Only a case the engine accepts and
HTML rejects (`minlength="2"` vs. a 1-character guard) made the gate
observable.

*Consequence:* F5 is true of the kernel but frequently unobservable in
practice. Do not rely on native validation as a backstop for a rule the engine
already guards — it may never run.

### F15 — Keyed reconciliation genuinely preserves DOM node identity

An attribute set by hand on the first `data-each` item survived a subsequent
projection that appended two further entries.

*Verified by observation:* set `data-probe="kept"` on the first `<li>`,
triggered another save, re-read the attribute — still present.

---

## Assumptions

### A1 — That `0.4.1` is a safe version to build against

It is the latest published version, but it is `0.x`. No stability policy for
pre-1.0 minor bumps has been read. A `0.5.0` could break the protocol.
`PROTOCOL_VERSION` is `1` and `Initialize` rejects a mismatch, so a protocol
break would at least fail loudly rather than silently.

*Unverified:* whether the project intends `0.x` minors to be breaking.

### A2 — ~~That the kernel actually works in a browser from this repository~~ **WITHDRAWN 2026-09-14**

Superseded by finding **F13**. The original entry read: the kernel had never
been run in a browser from Chrona, only imported under Node, so any claim
beyond "it imports" was an assumption.

Resolved by building `verification/kernel-slice/` and running it against real
Chromium. Retained here rather than deleted, per this log's append-only rule.
Scope of the resolution is limited to the primitives that slice exercises;
`Http` remains unexercised (A6).

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

### A6 — That the `Http` effect path behaves as its source suggests

The verification slice uses `Storage` only. `OutcomeUnknown`,
`timeout-after-dispatch`, cancellation via `cancellations`, and header/body
handling are all still read-derived. F6 in particular — that a timeout is
always `OutcomeUnknown` — has not been observed.

### A7 — That the flush behavior in F11 is unintended rather than designed

F11 is recorded as observed behavior. Whether the kernel's authors consider a
`data-event` button inside a form firing on submit a defect or an intentional
consequence of the mechanical rule has not been established. No issue has been
filed and no maintainer statement has been read.

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

### D7 — A silent misdispatch presented as the wrong defect

F11 cost the majority of the debugging time on the slice. The symptom was
"saving does not work": correct-looking status text, no bridge error, no
console error, `localStorage` empty. The actual cause was a *different* event
firing first. Nothing in the failure pointed at the form flush.

*Workaround:* dump the round-trip log and read which event actually arrived,
rather than reasoning forward from the click.

*Consequence:* build an observable log of dispatched events into any non-trivial
slice. Without the `data-each` round-trip log, this would have been far harder
to see.

### D8 — Playwright is global, and ESM cannot reach it the usual way

`NODE_PATH` does not apply to ESM imports, so `import { chromium } from
"playwright"` fails from a project script. Resolving `npm root -g` and
importing the absolute path works, but Playwright ships CJS, so `chromium`
may only appear under `.default` depending on lexing — both shapes have to be
handled.

### D9 — A verification check that silently verified nothing

The first version of the browser run asserted `window.__bridgeErrors` was
empty *after* a `page.reload()`, which wipes it. The check passed while
proving nothing. Caught only because the run was re-read after the failures
were fixed.

*Consequence:* a check that passes on a broken run is worse than no check.
Bridge errors are now drained before the reload and combined afterwards.

### D10 — TypeScript was not present and had to be added

The scaffold ships no compiler. `typescript@^5.9.2` was added as a
devDependency to build the slice, matching the kernel's own pinned version.
Recorded as a dependency decision rather than made silently.

---

## Highest-value next step

**Done 2026-09-14** — `verification/kernel-slice/` exists and A2 is withdrawn
(F13).

Next: exercise the `Http` effect path, which would resolve A6 and let F6
(timeout is always `OutcomeUnknown`) be observed rather than read. That needs a
stub endpoint that can be made to hang, so the slice would gain a small server.

Then decide whether F11 warrants an upstream issue, which first requires
resolving A7 — whether the behavior is considered a defect. A one-line
reproduction already exists in this repository's git history.
