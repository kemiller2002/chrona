import { BrowserKernel } from "@echelon-foundry/typescript-wasm-kernel";
import { createSliceTransport } from "./transport.js";

declare global {
  interface Window { __bridgeErrors: string[] }
}

window.__bridgeErrors = [];

const kernel = new BrowserKernel(createSliceTransport(), document, {
  report(event) {
    if (event.kind === "BridgeError") window.__bridgeErrors.push(`${event.phase}: ${event.detail}`);
  },
});

await kernel.start();
