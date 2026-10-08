// Limen kernel side - browser interop only, for Chrona's pages (web/): the
// application (index.html) and the kernel verification slice
// (kernel-slice.html).
//
// Loads the published .NET WebAssembly runtime, hands each Limen message, as
// JSON, to the [JSExport] in the Chrona.Wasm shim, and starts
// Limen's BrowserKernel. It never inspects a message: every application
// decision is made by the F# engine on the far side of the transport
// (src/Chrona.Engine, src/Chrona.Application).
//
// Folio's print components are registered here too, so the print surface
// upgrades; they are inert light-DOM elements with no behaviour.
import "../node_modules/@echelon-foundry/print-components/src/components/register.js";
import { BrowserKernel } from "../node_modules/@echelon-foundry/limen/dist/kernel/browser-kernel.js";
import { scheduleCapability } from "../node_modules/@echelon-foundry/limen/dist/capabilities/schedule/index.js";
import { environmentCapability } from "../node_modules/@echelon-foundry/limen/dist/capabilities/environment/index.js";

// Relative to this module, so it resolves the same in the checkout and over
// HTTP (the repository root is what is served).
const FRAMEWORK = "../build/wasm/wwwroot/_framework";

class WasmEngineTransport {
  #dispatch = null;
  #exportName;

  constructor(exportName) {
    this.#exportName = exportName;
  }

  async start() {
    const { dotnet } = await import(`${FRAMEWORK}/dotnet.js`);
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    const dispatch = exports.ChronaWasm?.[this.#exportName];
    if (typeof dispatch !== "function") {
      throw new Error(`ChronaWasm.${this.#exportName} export not found; run \`npm run build:wasm\` to republish the engine.`);
    }
    this.#dispatch = dispatch;
  }

  async dispatch(message) {
    if (this.#dispatch === null) throw new Error("dispatch() called before start()");
    return JSON.parse(this.#dispatch(JSON.stringify(message)));
  }
}

// Limen reports a bridge failure to its diagnostics sink rather than
// throwing, so the default no-op sink would make a broken engine look like an
// empty page. Failures are loud; everything else is debug output.
const diagnostics = {
  report(event) {
    if (event.kind === "BridgeError") {
      console.error(`[limen] bridge error during ${event.phase}: ${event.detail}`);
    } else if (event.kind === "Handshake" && event.verdict.kind === "Incompatible") {
      console.error(`[limen] engine and kernel are incompatible: ${JSON.stringify(event.verdict.reason)}`);
    } else if (event.kind === "Handshake") {
      // Published so the browser suite can observe what was negotiated: a
      // legacy (un-negotiated) run would show no protocol here.
      const { negotiation } = event.verdict;
      if (negotiation?.kind === "Negotiated") {
        document.documentElement.dataset.protocol = `${negotiation.protocol.major}.${negotiation.protocol.minor}`;
        document.documentElement.dataset.capabilities = negotiation.capabilities.map((c) => c.id).join(" ");
      }
      console.debug("[limen]", event.kind);
    } else {
      console.debug("[limen]", event.kind);
    }
  }
};

// Starts the kernel for a page. `exportName` is the ChronaWasm export;
// `capabilities` are the optional Limen packs the page's engine may select.
// The kernel's status is published on <html data-kernel> so the page (and
// its browser tests) can tell "running" from a failed start.
export async function startPage(exportName, capabilities = []) {
  const kernel = new BrowserKernel(new WasmEngineTransport(exportName), document, diagnostics, {
    requireHandshake: true,
    capabilities
  });
  await kernel.start();
  document.documentElement.dataset.kernel = kernel.status;
}

// The Chrona application: its engine selects the schedule pack (the running
// timer's once-a-second wake-up) and the environment pack (the browser's time
// zone). Both are the installed Limen package's own, never copies.
export async function startApp() {
  await startPage("DispatchApp", [scheduleCapability(), environmentCapability()]);
}
