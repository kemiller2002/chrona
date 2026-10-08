// Browser reliability (WI-0063, requirement 37) in a real browser: a page
// that comes back catches up, and a newer Chrona deployed since the page
// started is offered as a reload that keeps what is pending.
import { test, expect, setUp } from "./support.js";

test.use({ timezoneId: "America/New_York" });

const announcement = (page) => page.locator("p[role=status][aria-live=polite]");

const visibility = (page, state) =>
  page.evaluate((state) => {
    Object.defineProperty(document, "visibilityState", { value: state, configurable: true });
    document.dispatchEvent(new Event("visibilitychange"));
  }, state);

test("the build beside the page is the page's own: no update is offered", async ({ app: page }) => {
  const build = await page.request.get("/build/wasm/wwwroot/chrona-build.json");
  expect(build.ok()).toBeTruthy();
  expect((await build.json()).build).toMatch(/^[0-9a-f]{12}$|^development$/);
  await visibility(page, "hidden");
  await visibility(page, "visible");
  await page.waitForTimeout(500);
  await expect(page.locator("#shell-update")).toHaveCount(0);
});

test("a hidden page that comes back keeps its timer, and a newer Chrona is offered as a safe reload", async ({ app: page }) => {
  await setUp(page);
  await page.click(".chrona-nav__link:has-text('Track')");
  await page.selectOption("#timer-activity-type", { label: "Research" });
  await page.selectOption("#timer-project", { label: "HelixNote" });
  await page.click("#start-timer");
  await expect(page.locator("#timer-display")).toHaveText("00:00:01", { timeout: 5000 });

  // The page is hidden, then comes back: the timer still runs from its start.
  await visibility(page, "hidden");
  await page.waitForTimeout(1500);
  await visibility(page, "visible");
  await expect(page.locator("#tracker")).toHaveAttribute("data-state", "running");
  await expect(page.locator("#timer-display")).toHaveText(/^00:00:0[2-9]$/, { timeout: 5000 });

  // Meanwhile the deployment was updated.
  await page.route("**/build/wasm/wwwroot/chrona-build.json*", (route) => route.fulfill({ json: { build: "f00dfeedbeef" } }));
  await visibility(page, "hidden");
  await visibility(page, "visible");
  await expect(page.locator("#shell-update")).toContainText("A newer Chrona is ready.");
  await expect(page.locator("#shell-update")).toContainText("Your unsent changes and your timer are kept");

  // Reloading loads the page again (here the same build, so no newer one is
  // offered after it); the timer is recovered, not lost.
  await page.unroute("**/build/wasm/wwwroot/chrona-build.json*");
  await page.click("#reload-shell");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(announcement(page)).toContainText("Your timer was recovered and is still running", { timeout: 10000 });
  await expect(page.locator("#tracker")).toHaveAttribute("data-state", "running");
});
