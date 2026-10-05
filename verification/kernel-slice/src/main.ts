import { BrowserKernel, type EngineTransport, type HandshakeVerdict, type KernelStatus } from "@echelon-foundry/limen";
import { createSliceTransport } from "./transport.js";

declare global {
  interface Window {
    __bridgeErrors: string[];
    __dispatched: string[];
    __handshake: HandshakeVerdict | null;
    __kernelStatus: KernelStatus | null;
  }
}

window.__bridgeErrors = [];
window.__dispatched = [];
window.__handshake = null;
window.__kernelStatus = null;

// Records the kind of every message the kernel dispatches, so the browser
// verification can observe that a message reached the engine at all.
const engine = createSliceTransport();
const observed: EngineTransport = {
  start: () => engine.start(),
  dispatch: (message) => {
    window.__dispatched.push(message.kind);
    return engine.dispatch(message);
  },
};

const kernel = new BrowserKernel(
  observed,
  document,
  {
    report(event) {
      switch (event.kind) {
        case "BridgeError":
          window.__bridgeErrors.push(`${event.phase}: ${event.detail}`);
          return;
        case "Handshake":
          window.__handshake = event.verdict;
          return;
        case "EffectTiming":
        case "Hydration":
          return;
        default: {
          const unhandled: never = event;
          throw new Error(`Unhandled diagnostic: ${JSON.stringify(unhandled)}`);
        }
      }
    },
  },
  // The slice always answers the handshake, so a kernel that would run it as
  // a legacy (pre-1.1) engine is refused rather than silently downgraded.
  { requireHandshake: true },
);

await kernel.start();
window.__kernelStatus = kernel.status;
