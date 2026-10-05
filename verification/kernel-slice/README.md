# Kernel verification slice

**This is not a Chrona product feature.** `PROJECT-CHARTER.md` leaves the first
user, the communication problem, and owner decision authority explicitly
unassigned. Choosing those is the owner's call, so this slice deliberately
claims no product territory. Its only job is to answer one question:

> Does the Limen kernel (`@echelon-foundry/limen`, formerly
> `@echelon-foundry/typescript-wasm-kernel`) actually work, in a real
> browser, when driven from this repository?

Before it existed, every behavioral claim about the kernel in Chrona was
derived from reading its source. That gap was recorded as assumption **A2** in
`research/journals/JR-WASMKERNEL-2026-17F6--*.md`. This slice closes it.

## Layout

| Path | Tier | Contents |
|---|---|---|
| `src/domain.ts` | 1 / 2 | `Phase`, `Command`, guards, `transition`. Pure; no DOM, no fetch, no storage. |
| `src/projection.ts` | 3 | `project(model) -> ViewState`. Pure; returns data, never touches the DOM. |
| `src/transport.ts` | 3 | `EngineTransport` over a pure `step` fold. The WASM swap point. |
| `src/main.ts` | 4 | Wiring only. |
| `index.html` | 4 | The bridge vocabulary. |

Effects are returned as data by Tier 2 and executed by the kernel. The domain
never calls `localStorage`.

## Running it

```sh
npm run build:slice     # tsc
npm run test:slice      # 27 pure domain/projection/transport tests, no browser
npm run verify:slice    # 16 checks against real Chromium via Playwright
```

`verify:slice` serves the repository root on port 4173, drives the page, and
writes `browser-verification.png`. Playwright is resolved from the global
install; there is no Playwright project dependency. To use a Chromium that is
already on disk instead of a Playwright download, set
`PLAYWRIGHT_CHROMIUM_EXECUTABLE` to its path.

## Protocol

The slice depends on `@echelon-foundry/limen` at exactly `0.7.0` (protocol
1.4). `transport.ts` handles every `BrowserToEngineMessage` kind and every
`EffectResult` kind exhaustively:

| Message | Handling |
|---|---|
| `Initialize` | checks the protocol version and that `Storage` is offered, and answers the handshake with `answerHandshake` (core contract, no capability packs) |
| `Event` | domain command |
| `EffectResult` / `StorageResult` | domain command (`RecordStorage`) |
| `EffectResult` / `HttpResult`, `ClipboardResult`, `NavigationResult`, `CapabilityResult` | refused: the slice never requests these, so a result has no request behind it |
| `LocationChanged` | re-projects unchanged; the slice has no URL-driven state |
| `CapabilityFact` | refused: no capability was negotiated |

`main.ts` starts the kernel with `requireHandshake: true`, so the slice is never
silently run as a legacy (pre-1.1) engine.

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
