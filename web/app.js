// Limen kernel side for the Chrona application page: starts the kernel
// (web-kernel/limen-wasm.js) against the application engine. Nothing else.
import { startApp } from "../web-kernel/limen-wasm.js";

await startApp();
