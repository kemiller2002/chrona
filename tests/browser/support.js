// Shared harness for the browser suite: the page under test, loaded until
// Limen's kernel is running, and a guard that fails the test on any console
// error or page error, since Limen reports a broken engine (a bridge error)
// to the console rather than throwing.
import { test as base, expect } from "@playwright/test";

export const test = base.extend({
  // Fails the test on any console error or uncaught page error.
  page: async ({ page }, use) => {
    const problems = [];
    page.on("console", (message) => {
      if (message.type() === "error") problems.push(`console.error: ${message.text()}`);
    });
    page.on("pageerror", (error) => problems.push(`pageerror: ${error.message}`));
    await use(page);
    expect(problems, "the page reported errors").toEqual([]);
  },

  // The Chrona application, started at `#/more` (or the given fragment) with
  // the kernel running and the browser's time zone described to the engine.
  app: async ({ page }, use) => {
    await page.goto("/web/index.html#/more");
    await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
    await expect(page.locator("#zone")).not.toHaveText("Not yet known");
    await use(page);
  },

  slice: async ({ page }, use) => {
    // Records the kind of every message the kernel hands the WASM transport
    // (which serializes each one with JSON.stringify), so a test can observe
    // that a message reached the engine at all.
    await page.addInitScript(() => {
      window.__dispatched = [];
      const stringify = JSON.stringify;
      JSON.stringify = function (value, ...rest) {
        if (value && typeof value === "object" && typeof value.kind === "string" && "kind" in value &&
            ["Initialize", "Event", "EffectResult", "LocationChanged", "CapabilityFact"].includes(value.kind)) {
          window.__dispatched.push(value.kind);
        }
        return stringify.call(this, value, ...rest);
      };
    });
    await page.goto("/web/kernel-slice.html");
    await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
    await use(page);
  }
});

// Reference data every record needs, added through the More screen.
export async function setUp(page) {
  await page.fill("#new-project-name", "HelixNote");
  await page.click("#add-project");
  await page.fill("#new-activity-type-name", "Research");
  await page.click("#add-activity-type");
  await page.fill("#new-tag-name", "Backend");
  await page.click("#add-tag");
  await expect(page.locator("#projects .ef-checkbox__label")).toHaveText(["HelixNote"]);
  await expect(page.locator("#activity-types .ef-checkbox__label")).toHaveText(["Research"]);
}

// Yesterday in New York: a whole past day, so a fixed time of day is never in
// the future whenever the suite runs.
export const yesterday = () => {
  const date = new Date(Date.now() - 24 * 60 * 60 * 1000);
  return new Intl.DateTimeFormat("en-CA", { timeZone: "America/New_York", year: "numeric", month: "2-digit", day: "2-digit" }).format(date);
};


// Three in the afternoon of today in New York: a page clock fixed there makes
// a time earlier in the day a past time whenever the suite runs (after
// midnight in New York, 08:10 today would otherwise be in the future and
// refused as CHRONA.ENTRY.FUTURE_TIME).
export const afternoon = () => {
  const now = new Date();
  const day = new Intl.DateTimeFormat("en-CA", { timeZone: "America/New_York", year: "numeric", month: "2-digit", day: "2-digit" }).format(now);
  const offset = new Intl.DateTimeFormat("en-US", { timeZone: "America/New_York", timeZoneName: "longOffset" })
    .formatToParts(now)
    .find((part) => part.type === "timeZoneName")
    .value.replace("GMT", "");
  return new Date(`${day}T15:00:00${offset || "+00:00"}`);
};

// Adds a manual entry on `date` (yesterday by default) from the Track screen.
export async function addEntry(page, { start, end, description, purpose = "Delivery", date = yesterday() }) {
  await page.selectOption("#manual-activity-type", { label: "Research" });
  await page.selectOption("#manual-project", { label: "HelixNote" });
  await page.fill("#manual-start-date", date);
  await page.fill("#manual-start-time", start);
  await page.fill("#manual-end-date", date);
  await page.fill("#manual-end-time", end);
  await page.fill("#manual-description", description);
  await page.fill("#manual-purpose", purpose);
  await page.fill("#manual-reason", "Entered from notes");
  await page.click("#save-manual");
  await expect(page.locator("p[role=status][aria-live=polite]")).toHaveText(/^Saved /);
}

export { expect };

// ---- A signed-in deployment: Fides' exchange and GitHub's authorize page as
// Playwright routes (Fides is not deployed anywhere yet). -------------------

const ORIGIN = "http://127.0.0.1:4321";
export const PAGE = `${ORIGIN}/web/index.html`;
export const ACCESS_TOKEN = "gho_CHRONABROWSERACCESSTOKEN0123456789";
export const REFRESH_TOKEN = "ghr_CHRONABROWSERREFRESHTOKEN0123456789";

// A deployment with sign-in and no data location.
export const signInConfiguration = {
  environment: "test",
  environmentName: "test",
  identity: { exchange: "https://fides.test", application: "chrona-test", provider: "github", clientId: "Iv23liBROWSER", redirectUri: PAGE }
};

const cors = {
  "access-control-allow-origin": ORIGIN,
  "access-control-allow-methods": "POST, OPTIONS",
  "access-control-allow-headers": "content-type",
  vary: "Origin",
  "cache-control": "no-store"
};

// The person GitHub resolves for the fake exchange: octocat, unless a test
// signs in as someone else (signInAs).
const OCTOCAT = { provider: "github", subject: "583231", login: "octocat", name: "The Octocat" };
let identity = OCTOCAT;

// Signs the next sign-ins in as this person (a GitHub numeric id and login).
export const signInAs = (subject, login) => {
  identity = { provider: "github", subject, login, name: login };
};

// The deployment (with this configuration), Fides' exchange and GitHub's
// authorize page, as the page reaches them. Returns the exchange paths called.
export async function fakeDeployment(page, configuration) {
  identity = OCTOCAT;
  const exchanged = [];
  await page.route("**/web/chrona.deployment.json", (route) => route.fulfill({ json: configuration }));
  await page.route("https://fides.test/**", async (route) => {
    const request = route.request();
    if (request.method() === "OPTIONS") return route.fulfill({ status: 204, headers: cors });
    const path = new URL(request.url()).pathname;
    exchanged.push(path);
    const body = JSON.parse(request.postData() ?? "{}");
    if (path === "/v1/token" && body.code === "good-code") {
      const at = (hours) => new Date(Date.now() + hours * 3600_000).toISOString().replace(/\.\d+Z$/, "Z");
      return route.fulfill({
        status: 200,
        headers: cors,
        contentType: "application/json",
        body: JSON.stringify({
          accessToken: ACCESS_TOKEN,
          // A day, so a page clock fixed later today (`afternoon`) still
          // finds the token current.
          accessTokenExpiresAt: at(24),
          refreshToken: REFRESH_TOKEN,
          refreshTokenExpiresAt: at(24 * 180),
          identity
        })
      });
    }
    if (path === "/v1/token") return route.fulfill({ status: 400, headers: cors, contentType: "application/json", body: '{"error":"code_rejected"}' });
    if (path === "/v1/revoke") return route.fulfill({ status: 204, headers: cors });
    return route.fulfill({ status: 404, headers: cors, contentType: "application/json", body: '{"error":"not_found"}' });
  });
  // GitHub's authorize page: the person approves, GitHub redirects back.
  await page.route("https://github.com/login/oauth/authorize**", (route) => {
    const url = new URL(route.request().url());
    const back = new URL(url.searchParams.get("redirect_uri"));
    back.searchParams.set("code", "good-code");
    back.searchParams.set("state", url.searchParams.get("state"));
    return route.fulfill({ status: 302, headers: { location: back.href } });
  });
  return exchanged;
}
