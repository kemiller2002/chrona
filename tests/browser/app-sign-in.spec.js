// Sign-in through Fides, end to end in a real browser (WI-0029, CHX-022,
// CHX-023): the deployment's configuration names the exchange, the page
// leaves for GitHub, GitHub sends it back with a code, Fides' client (running
// in the WASM engine) exchanges it, and Chrona works as the identity GitHub
// resolved. The deployment's configuration, Fides' exchange and GitHub's
// authorize page are fakes served by Playwright routes: Fides is not deployed
// anywhere yet. Every check observes live behaviour.
import { test, expect, fakeDeployment as fake, PAGE, ACCESS_TOKEN, signInConfiguration as configuration } from "./support.js";

const fakeDeployment = (page) => fake(page, configuration);

test.use({ timezoneId: "America/New_York" });

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
