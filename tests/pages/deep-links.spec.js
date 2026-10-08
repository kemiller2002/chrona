// Deep links on the published site (CHX-460, WI-0071): the Pages build (or,
// with CHRONA_SITE, the live deployment) opens every address cold, from the
// site root and from the application's own path, under the site's Content
// Security Policy. The site is a local demo: no sign-in, data in the tab.
import { test, expect } from "../browser/support.js";

test.use({ timezoneId: "America/New_York" });

// Addresses are relative to the site (baseURL), so a site served under a
// path works as well as one at a domain's root.
async function open(page, address) {
  await page.goto(address);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(page.locator(".chrona-shell")).toBeVisible();
}

test("an address at the site's root reaches the application with its place", async ({ page }) => {
  await open(page, "./#/day/2026-10-01");
  await expect(page).toHaveURL(/\/web\/#\/day\/2026-10-01$/);
  await expect(page.locator("#today-title")).toHaveText("Thursday, October 1");
  await expect(page.locator("[data-pages-banner]")).toBeVisible();

  // The root with no place reaches home.
  await open(page, "./");
  await expect(page).toHaveURL(/\/web\/(#\/)?$/);
  await expect(page.locator("#screen-today")).toBeVisible();
});

test("every place opens cold from the application's address", async ({ page }) => {
  for (const [fragment, screen] of [
    ["#/track", "#screen-track"],
    ["#/month/2026-09", "#screen-month"],
    ["#/review/2026-10-01", "#screen-review"],
    ["#/more/references", "#screen-more"],
    ["#/reports?from=2026-09-01&to=2026-09-30&method=manual&group=tag", "#screen-reports"]
  ]) {
    await open(page, `web/${fragment}`);
    await expect(page.locator(screen), fragment).toBeVisible();
    await expect(page).toHaveURL(new RegExp(`/web/${fragment.replace(/[?.]/g, "\\$&")}$`));
  }

  await expect(page.locator("#report-method")).toHaveValue("manual");
  await expect(page.locator("#report-grouping")).toHaveValue("tag");
});

test("an old address is corrected, and one that names nothing shows the not-found page", async ({ page }) => {
  await open(page, "web/#/today/2026-10-01");
  await expect(page).toHaveURL(/\/web\/#\/day\/2026-10-01$/);

  await open(page, "web/#/nowhere");
  await expect(page.locator("#screen-problem")).toBeVisible();
  await expect(page.locator("#problem-title")).toHaveText("Page not found");
});

test("Copy link gives the site's own address for the view", async ({ page, context, baseURL }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"], { origin: new URL(baseURL).origin });
  await open(page, "web/#/month/2026-09");
  await page.click("#copy-link");
  await expect(page.locator("#link-status")).toHaveText("Link copied.");
  const link = await page.evaluate(() => navigator.clipboard.readText());
  expect(link).toBe(new URL("web/#/month/2026-09", baseURL.endsWith("/") ? baseURL : `${baseURL}/`).href);

  const other = await context.newPage();
  await other.goto(link);
  await expect(other.locator("#month-title")).toHaveText("September 2026");
});
