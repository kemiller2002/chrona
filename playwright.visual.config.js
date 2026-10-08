// Visual regression of Chrona's screens (WI-0054): every screen in light
// and dark, at the legacy captures' desktop and phone widths, compared with
// baseline images under tests/visual/baselines/.
//
// A screenshot depends on the browser build and the fonts it finds, so the
// baselines are rendered by exactly one browser: the Chromium build that the
// pinned Playwright version downloads, on CI's Ubuntu runner. This config
// therefore never substitutes another Chromium (unlike playwright.config.js),
// and CI runs it as its own step after the browser suite.
//
// To change baselines on purpose, run the "Re-render the visual baselines"
// workflow (.github/workflows/visual-baselines.yml, DF-CHRONA-2026-0006): it
// renders them with that browser and opens a pull request with the images.
// With CHRONA_VISUAL_PREVIEW=1, the screens are
// rendered locally into test-results/visual-preview/ for a look, compared
// with nothing.
import { existsSync } from "node:fs";
import { defineConfig, devices } from "@playwright/test";

const port = 4321;
const origin = `http://127.0.0.1:${port}`;
const preview = process.env.CHRONA_VISUAL_PREVIEW === "1";
const preinstalledChromium = "/opt/pw-browsers/chromium";
const launchOptions = preview && existsSync(preinstalledChromium) ? { executablePath: preinstalledChromium } : {};

export default defineConfig({
  testDir: "./tests/visual",
  snapshotPathTemplate: preview ? "test-results/visual-preview/{arg}{ext}" : "{testDir}/baselines/{arg}{ext}",
  // A missing baseline is written (and the test still fails), so CI's
  // failure artifact carries it for review; an existing one is only compared.
  updateSnapshots: preview ? "all" : "missing",
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,
  timeout: 120_000,
  reporter: process.env.CI ? [["github"], ["list"]] : [["list"]],
  expect: {
    toHaveScreenshot: { animations: "disabled", caret: "hide", scale: "css", maxDiffPixelRatio: 0.001 }
  },
  use: { baseURL: origin, trace: "retain-on-failure", timezoneId: "America/New_York", locale: "en-US" },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"], launchOptions } }],
  webServer: {
    command: `python3 -m http.server ${port} --bind 127.0.0.1`,
    url: `${origin}/web/index.html`,
    reuseExistingServer: !process.env.CI,
    timeout: 60_000
  }
});
