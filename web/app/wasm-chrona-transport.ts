import type {
  BrowserToEngineMessage,
  EngineToBrowserMessage,
  EngineTransport,
} from "@echelon-foundry/typescript-wasm-kernel/protocol";

type DotnetConfig = { readonly mainAssemblyName: string };

type DotnetRuntime = {
  getAssemblyExports(name: string): Promise<unknown>;
  getConfig(): DotnetConfig;
};

type DotnetBuilder = {
  withDiagnosticTracing(enabled: boolean): DotnetBuilder;
  create(): Promise<DotnetRuntime>;
};

type DotnetModule = {
  readonly dotnet: DotnetBuilder;
};

type ChronaExports = {
  readonly ChronaHostWasm?: {
    readonly Dispatch?: (messageJson: string) => string;
  };
};

export class WasmChronaTransport implements EngineTransport {
  #dispatch: ((messageJson: string) => string) | null = null;

  async start(): Promise<void> {
    const moduleUrl = new URL("../wasm/_framework/dotnet.js", import.meta.url).href;
    const loaded = await import(moduleUrl) as unknown as DotnetModule;
    const runtime = await loaded.dotnet.withDiagnosticTracing(false).create();
    const config = runtime.getConfig();
    const exports = await runtime.getAssemblyExports(config.mainAssemblyName) as ChronaExports;
    const dispatch = exports.ChronaHostWasm?.Dispatch;

    if (dispatch === undefined) {
      throw new Error("ChronaHostWasm.Dispatch export not found.");
    }

    this.#dispatch = dispatch;
  }

  async dispatch(message: BrowserToEngineMessage): Promise<EngineToBrowserMessage> {
    if (this.#dispatch === null) {
      throw new Error("WasmChronaTransport.dispatch() called before start().");
    }

    return JSON.parse(this.#dispatch(JSON.stringify(message))) as EngineToBrowserMessage;
  }
}
