import { BrowserKernel } from "@echelon-foundry/typescript-wasm-kernel";
import { WasmChronaTransport } from "./wasm-chrona-transport.js";

const diagnostics = {
  report(event: { kind: string; phase?: string; detail?: string }): void {
    if (event.kind === "BridgeError") {
      console.error(`[chrona:${event.phase}]`, event.detail);
    }
  },
};

await new BrowserKernel(new WasmChronaTransport(), document, diagnostics).start();
