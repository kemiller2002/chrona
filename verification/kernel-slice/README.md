# Kernel verification slice

**This is not a Chrona product feature.** `PROJECT-CHARTER.md` leaves the first
user, the communication problem, and owner decision authority explicitly
unassigned. Choosing those is the owner's call, so this slice deliberately
claims no product territory. Its only job is to answer one question:

> Does the Limen kernel (`@echelon-foundry/limen`) actually work, in a real
> browser, when driven from this repository?

It first answered that with a TypeScript engine in this directory (assumption
**A2** in `research/journals/JR-WASMKERNEL-2026-17F6--*.md`). On 2026-10-05 it
was promoted to the way every Echelon application is built
(`research/decisions/DF-CHRONA-FND-2026-0002--build-chrona-on-the-full-echelon-foundation-stack.md`):
the same decisions, now made by an F# engine published to .NET WebAssembly,
behind Limen's kernel, under an Aegis boundary, presented with Forma and
printed with Folio. The TypeScript sources and tests were ported decision for
decision and assertion for assertion, and removed; they remain in the git
history (`git show 1cfbd29:verification/kernel-slice/src/domain.ts`).

## Where it lives now

| Path | Tier | Contents |
|---|---|---|
| `src/Chrona.Engine/KernelSlice.fs` | engine (pure) | `Phase`, `Command`, the label guard, `transition`. Effects as data. |
| `src/Chrona.Engine/KernelSliceView.fs` | engine (pure) | `project : Model -> View`. |
| `src/Chrona.Application/` | engine (application) | Limen protocol, handshake, Aegis boundary (`Wire.fs`, the old `transport.ts`). |
| `src/Chrona.Wasm/` | kernel | The `[JSExport]` shim. |
| `web-kernel/limen-wasm.js`, `web/` | kernel | `BrowserKernel` start-up; the page (Forma markup, Folio print surface). |
| `tests/Chrona.Tests/KernelSliceTests.fs` | | The ported `domain.test.mjs`. |
| `tests/browser/kernel-slice.spec.js` | | The ported `browser-verification.mjs`, on Playwright. |

## Running it

```sh
npm ci
npm run test:dotnet     # engine, protocol, Aegis boundary, conformance
npm run test:browser    # publishes the WASM engine, then drives Chromium
```

## Two deliberate oddities, both load-bearing

**`minlength="2"` is stricter than the engine guard**, which accepts one
character. Without a case HTML rejects and the engine accepts, the native
`reportValidity` gate is unobservable — `data-bind-disabled` disables the
button first and the gate is never reached. The mismatch is the probe.

**The Load button sits outside the `<form>`.** The kernel's flush list
registers every element inside a form whose trigger is not `submit`, and
`"form" in el` is true for `HTMLButtonElement`. With Load inside the form,
clicking Save dispatched `load` first, leaving the phase `Loading`, so the
subsequent `save` was rejected as illegal. Observed, not theorised — see
finding F11 in the journal.

## What this slice does not cover

It exercises `Storage` effects only. The `Http` path — and with it
`OutcomeUnknown`, cancellation, and header/body handling — remains
unexercised here.
