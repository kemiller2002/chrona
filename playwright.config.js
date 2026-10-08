// End-to-end configuration for Chrona's pages: the application
// (web/index.html) and the kernel verification slice (web/kernel-slice.html).
//
// The .NET tests prove the engine decides correctly. They cannot prove the
// engine reaches the browser: the [JSExport] shim, the WASM transport,
// Limen's kernel, the HTML bindings and the Forma and Folio presentation sit
// between the two, and none of them is type-checked against the others.
// These tests drive the real page in a real browser.
import { existsSync } from "node:fs";
import { defineConfig, devices } from "@playwright/test";

const port = 4321;
const origin = `http://127.0.0.1:${port}`;

// Some environments ship a Chromium that Playwright did not download itself
// and must not try to. Where that binary exists it is used as-is; everywhere
// else Playwright resolves its own, so CI needs no special case.
const preinstalledChromium = "/opt/pw-browsers/chromium";
const launchOptions = existsSync(preinstalledChromium) ? { executablePath: preinstalledChromium } : {};

export default defineConfig({
  testDir: "./tests/browser",
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,
  timeout: 60_000,
  reporter: process.env.CI ? [["github"], ["list"]] : [["list"]],
  use: { baseURL: origin, trace: "retain-on-failure" },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"], launchOptions } }],
  // Served from the repository root: index.html reaches up into node_modules/
  // for Limen, Forma and Folio, into web-kernel/ for the kernel, and into
  // build/ for the published engine.
  webServer: {
    command: `python3 -m http.server ${port} --bind 127.0.0.1`,
    url: `${origin}/web/index.html`,
    reuseExistingServer: !process.env.CI,
    timeout: 60_000
  }
});
