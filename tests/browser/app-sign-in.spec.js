// Sign-in through Fides, end to end in a real browser (WI-0029, CHX-022,
// CHX-023): the deployment's configuration names the exchange, the page
// leaves for GitHub, GitHub sends it back with a code, Fides' client (running
// in the WASM engine) exchanges it, and Chrona works as the identity GitHub
// resolved. The deployment's configuration, Fides' exchange and GitHub's
// authorize page are fakes served by Playwright routes: Fides is not deployed
// anywhere yet. Every check observes live behaviour.
import { test, expect } from "./support.js";

test.use({ timezoneId: "America/New_York" });

const ORIGIN = "http://127.0.0.1:4321";
const PAGE = `${ORIGIN}/web/index.html`;
const ACCESS_TOKEN = "gho_CHRONABROWSERACCESSTOKEN0123456789";
const REFRESH_TOKEN = "ghr_CHRONABROWSERREFRESHTOKEN0123456789";

const configuration = {
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

// The deployment, Fides' exchange and GitHub, as the page reaches them.
async function fakeDeployment(page) {
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
          accessTokenExpiresAt: at(8),
          refreshToken: REFRESH_TOKEN,
          refreshTokenExpiresAt: at(24 * 180),
          identity: { provider: "github", subject: "583231", login: "octocat", name: "The Octocat" }
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

const storageText = (page, store) => page.evaluate((name) => JSON.stringify({ ...window[name] }), store);

test("a person signs in with GitHub and works as the identity GitHub resolved, then signs out", async ({ page }) => {
  const exchanged = await fakeDeployment(page);
  await page.goto("/web/index.html");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");

  // The legacy sign-in page: a dark story panel beside the form. Nothing else.
  await expect(page.locator("#sign-in")).toBeVisible();
  await expect(page.locator(".chrona-shell")).toHaveCount(0);
  await expect(page.locator(".chrona-signin__story")).toHaveCSS("background-color", "rgb(35, 49, 46)");
  await expect(page.locator("#retention-page")).toBeChecked();

  await page.check("#retention-tab");
  await page.click("#sign-in-button");

  // Back from GitHub: signed in, with the callback removed from the address.
  await expect(page.locator(".chrona-shell")).toBeVisible();
  await expect(page).toHaveURL(PAGE);
  expect(exchanged).toEqual(["/v1/token"]);
  await page.click(".chrona-nav__link:has-text('More')");
  await expect(page.locator("#account-login")).toHaveText("octocat");
  await expect(page.locator(".chrona-identity")).toHaveAttribute("aria-label", "octocat");

  // The token is in this tab's session storage, as chosen, and nowhere else.
  expect(await storageText(page, "sessionStorage")).toContain(ACCESS_TOKEN);
  expect(await storageText(page, "localStorage")).not.toContain("gho_");
  expect(await page.content()).not.toContain(ACCESS_TOKEN);

  // Kept for the tab: a reload keeps the session.
  await page.reload();
  await expect(page.locator(".chrona-shell")).toBeVisible();
  await page.click(".chrona-nav__link:has-text('More')");
  await expect(page.locator("#account-login")).toHaveText("octocat");

  // Sign out: the token is cleared from the tab and revoked at GitHub.
  await page.click("#sign-out");
  await expect(page.locator("#sign-in")).toBeVisible();
  await expect(page.locator("#sign-in-notice")).toHaveAttribute("data-code", "signed_out");
  expect(exchanged).toContain("/v1/revoke");
  expect(await storageText(page, "sessionStorage")).not.toContain(ACCESS_TOKEN);
});

test("session-only retention keeps the token in the page, never in storage", async ({ page }) => {
  await fakeDeployment(page);
  await page.goto("/web/index.html");
  await page.click("#sign-in-button");
  await expect(page.locator(".chrona-shell")).toBeVisible();
  expect(await storageText(page, "sessionStorage")).not.toContain(ACCESS_TOKEN);
  expect(await storageText(page, "localStorage")).not.toContain(ACCESS_TOKEN);

  // Reloading the page ends a page-only session.
  await page.reload();
  await expect(page.locator("#sign-in")).toBeVisible();
});

test("a callback that did not start in this tab is refused without calling the exchange", async ({ page }) => {
  const exchanged = await fakeDeployment(page);
  await page.goto("/web/index.html?code=good-code&state=forged");
  await expect(page.locator("#sign-in-notice")).toHaveAttribute("data-code", "state_invalid");
  await expect(page.locator(".chrona-shell")).toHaveCount(0);
  expect(exchanged).toEqual([]);
});
