// Limen kernel side for the slice page: starts the kernel
// (web-kernel/limen-wasm.js) against the engine's export. Nothing else.
import { startPage } from "../web-kernel/limen-wasm.js";

await startPage("Dispatch");
