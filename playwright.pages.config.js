// Deep links against the published site (CHX-460, WI-0071): the Pages
// build that tools/pages/assemble-site.mjs writes to dist-pages/, served as
// GitHub Pages serves it (a static host, the site's root at /), or, with
// CHRONA_SITE set, the live deployment itself (pages.yml runs it after each
// deploy). Every address is opened cold.
//
//   node tools/pages/assemble-site.mjs dist-pages
//   npx playwright test -c playwright.pages.config.js
//   CHRONA_SITE=https://chrona.echelonfoundry.com npx playwright test -c playwright.pages.config.js
import { existsSync } from "node:fs";
import { defineConfig, devices } from "@playwright/test";

const port = 4322;
const site = process.env.CHRONA_SITE ?? `http://127.0.0.1:${port}`;

// The same browser as the rest of the suite (playwright.config.js).
const preinstalledChromium = "/opt/pw-browsers/chromium";
const launchOptions = existsSync(preinstalledChromium) ? { executablePath: preinstalledChromium } : {};

export default defineConfig({
  testDir: "./tests/pages",
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  // The live site is reached over the network; one retry absorbs a dropped
  // connection there, never a wrong answer.
  retries: process.env.CHRONA_SITE ? 1 : 0,
  timeout: 60_000,
  reporter: process.env.CI ? [["github"], ["list"]] : [["list"]],
  use: { baseURL: site, trace: "retain-on-failure" },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"], launchOptions } }],
  webServer: process.env.CHRONA_SITE
    ? undefined
    : {
        command: `python3 -m http.server ${port} --bind 127.0.0.1 --directory dist-pages`,
        url: `${site}/web/index.html`,
        reuseExistingServer: !process.env.CI,
        timeout: 60_000
      }
});
