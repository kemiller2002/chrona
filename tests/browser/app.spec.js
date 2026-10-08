// The Chrona application end to end (WI-0046): DOM event -> Limen kernel ->
// WASM shim -> F# engine (Chrona.Domain decides) -> view, in a real browser,
// with Forma and the Chrona brand presenting it. Every check observes live
// behaviour.
import { test, expect, setUp, yesterday } from "./support.js";

test.use({ timezoneId: "America/New_York" });

const announcement = (page) => page.locator("p[role=status][aria-live=polite]");

test("the page runs on Limen with the schedule, environment and print packs, in the Chrona brand", async ({ app: page }) => {
  await expect(page.locator("html")).toHaveAttribute("data-protocol", "1.4");
  await expect(page.locator("html")).toHaveAttribute("data-capabilities", "limen.schedule limen.environment chrona.print");
  await expect(page.locator("#zone")).toHaveText("America/New_York");

  // The legacy palette arrives as Forma tokens from the compiled brand.
  const token = (name) => page.evaluate((n) => getComputedStyle(document.documentElement).getPropertyValue(n).trim(), name);
  expect(await token("--ef-color-surface-primary")).toBe("#e8e4dc");
  expect(await token("--ef-color-accent-primary")).toBe("#215d57");
  await expect(page.locator("body")).toHaveCSS("background-color", "rgb(232, 228, 220)");
  await expect(page.locator(".chrona-sidebar")).toHaveCSS("background-color", "rgb(35, 49, 46)");

  // The store says where records live: this tab only, until storage exists.
  await expect(page.locator(".chrona-sidebar__status")).toContainText("Kept in this tab only");
  await expect(page.locator(".chrona-identity")).toHaveText("LS");
});

test("manual entry reports every problem, then records exact time on Today", async ({ app: page }) => {
  await setUp(page);
  await page.click(".chrona-nav__link:has-text('Track')");
  await expect(page).toHaveURL(/#\/track$/);

  await page.click("#save-manual");
  await expect(page.locator("#manual-problems li")).toHaveText([
    "Add a start date and time.",
    "Add an end date and time.",
    "Add a project.",
    "Add an activity type.",
    "Add what you did.",
    "Add the business purpose: why this work mattered to the business."
  ]);
  await expect(page.locator("#manual-problems li").first()).toHaveAttribute("data-code", "CHRONA.ENTRY.MISSING_FIELD");

  await page.selectOption("#manual-activity-type", { label: "Research" });
  await page.selectOption("#manual-project", { label: "HelixNote" });
  await page.fill("#manual-start-date", yesterday());
  await page.fill("#manual-start-time", "00:10");
  await page.fill("#manual-end-time", "00:17");
  await page.fill("#manual-description", "Visual research");
  await page.fill("#manual-purpose", "Define the mobile ledger");
  await page.check("#manual-entry input[type=checkbox]");
  await page.click("#save-manual");
  // An earlier day needs a reason (the legacy rule).
  await expect(page.locator("#manual-problems li")).toHaveText(["Say why you are entering this after the fact: it is for an earlier day."]);
  await page.fill("#manual-reason", "Entered from notes");
  await page.click("#save-manual");

  await expect(announcement(page)).toHaveText(/^Saved 7m on /);
  await expect(page.locator("#manual-problems")).toHaveCount(0);
  await expect(page.locator("#manual-description")).toHaveValue("");

  await page.click(".chrona-nav__link:has-text('Today')");
  await expect(page.locator("#day-total")).toHaveText("0m");
  await page.fill("#day-picker", yesterday());
  await expect(page).toHaveURL(new RegExp(`#/today/${yesterday()}$`));
  await expect(page.locator("#day-total")).toHaveText("7m");
  await expect(page.locator(".chrona-summary-count")).toContainText("12m billed");
  const record = page.locator("#day-records .chrona-record");
  await expect(record).toHaveCount(1);
  await expect(record.locator(".chrona-record__title")).toHaveText("Visual research");
  await expect(record.locator(".chrona-record__time")).toHaveText("12:10 AM12:17 AM");
  await expect(record.locator(".chrona-record__classification")).toHaveText("Research · HelixNote");
  await expect(record.locator(".ef-status-lozenge").first()).toHaveText("Manual entry");

  // Overlapping time is refused, naming what it overlaps.
  await page.click(".chrona-nav__link:has-text('Track')");
  await page.selectOption("#manual-activity-type", { label: "Research" });
  await page.selectOption("#manual-project", { label: "HelixNote" });
  await page.fill("#manual-start-date", yesterday());
  await page.fill("#manual-start-time", "00:15");
  await page.fill("#manual-end-time", "00:20");
  await page.fill("#manual-description", "Overlap");
  await page.fill("#manual-purpose", "Test");
  await page.fill("#manual-reason", "From notes");
  await page.click("#save-manual");
  await expect(page.locator("#manual-problems li")).toHaveText(["This overlaps \"Visual research\". Adjust the times so they do not overlap."]);
});

test("the timer starts, ticks from timestamps, pauses, resumes and stops", async ({ app: page }) => {
  await setUp(page);
  await page.click(".chrona-nav__link:has-text('Track')");

  await page.click("#start-timer");
  await expect(page.locator("#timer-problems li")).toHaveText(["Add an activity type.", "Add a project."]);

  await page.selectOption("#timer-activity-type", { label: "Research" });
  await page.selectOption("#timer-project", { label: "HelixNote" });
  await page.fill("#timer-description", "Product research");
  await page.click("#start-timer");

  await expect(page.locator("#tracker")).toHaveAttribute("data-state", "running");
  await expect(page.locator("#timer-state")).toHaveText("Timing now");
  await expect(page.locator("#tracker-title")).toHaveText("Research");
  await expect(announcement(page)).toHaveText("Timer started.");
  // The display advances on its own (schedule wake-ups), computed by the engine.
  await expect(page.locator("#timer-display")).toHaveText("00:00:02", { timeout: 5000 });
  // The timer is not a live region: ticks are never announced (35).
  await expect(page.locator("#timer-display")).toHaveAttribute("role", "timer");
  await expect(page.locator("#timer-display")).not.toHaveAttribute("aria-live", /.+/);
  await expect(announcement(page)).toHaveText("Timer started.");

  await page.click("#pause-timer");
  await expect(page.locator("#timer-state")).toHaveText("Paused");
  const pausedAt = await page.locator("#timer-display").textContent();
  await page.waitForTimeout(1500);
  await expect(page.locator("#timer-display")).toHaveText(pausedAt);

  await page.click("#resume-timer");
  await expect(page.locator("#timer-state")).toHaveText("Timing now");

  // Everywhere but Track, a compact chip shows the running timer.
  await page.click(".chrona-nav__link:has-text('Today')");
  await expect(page.locator("#timer-chip")).toContainText("Research");
  await page.click("#timer-chip");
  await expect(page).toHaveURL(/#\/track$/);

  await page.click("#stop-timer");
  await expect(announcement(page)).toHaveText("Timer stopped after less than thirty seconds. Nothing was recorded.");
  await expect(page.locator("#tracker")).toHaveAttribute("data-state", "idle");
});

test("a stopped timer is held for completion and recorded with its business purpose", async ({ app: page }) => {
  test.setTimeout(90_000);
  await setUp(page);
  await page.click(".chrona-nav__link:has-text('Track')");
  await page.selectOption("#timer-activity-type", { label: "Research" });
  await page.selectOption("#timer-project", { label: "HelixNote" });
  await page.fill("#timer-description", "Timer work");
  await page.click("#start-timer");
  await expect(page.locator("#timer-display")).toHaveText("00:00:31", { timeout: 40_000 });
  await page.click("#stop-timer");

  await expect(page.locator("#completion")).toBeVisible();
  await expect(page.locator(".chrona-completion-total")).toHaveText("1m");
  await expect(page.locator("#complete-description")).toHaveValue("Timer work");
  await page.click("#save-completion");
  await expect(page.locator("#completion-problems li")).toHaveText(["Add the business purpose: why this work mattered to the business."]);

  await page.fill("#complete-purpose", "Ship the slice");
  await page.click("#save-completion");
  await expect(announcement(page)).toHaveText("Saved 1m of Timer work.");
  await expect(page.locator("#completion")).toHaveCount(0);

  await page.click(".chrona-nav__link:has-text('Today')");
  await expect(page.locator("#day-records .chrona-record .ef-status-lozenge").first()).toHaveText("Timer");
});

test("archived reference data stays listed but is no longer offered for new work", async ({ app: page }) => {
  await setUp(page);
  await page.uncheck("#projects input[type=checkbox]");
  await expect(page.locator("#projects .ef-checkbox__description")).toHaveText("Archived: kept on past records");
  await page.click(".chrona-nav__link:has-text('Track')");
  await expect(page.locator("#manual-project option:not([value=''])")).toHaveCount(0);
  await expect(page.locator(".chrona-notice").first()).toContainText("Add a project and an activity type first");
});

test("routes survive Back, Forward and deep links", async ({ app: page }) => {
  await page.click(".chrona-nav__link:has-text('Track')");
  await expect(page.locator("#screen-track")).toBeVisible();
  await page.click(".chrona-nav__link:has-text('Today')");
  await expect(page.locator("#screen-today")).toBeVisible();
  await expect(page.locator(".chrona-nav__link[aria-current=page]")).toHaveText("TDToday");

  await page.goBack();
  await expect(page.locator("#screen-track")).toBeVisible();
  await page.goForward();
  await expect(page.locator("#screen-today")).toBeVisible();

  await page.click("[data-event=previousDay]");
  await expect(page).toHaveURL(/#\/today\/\d{4}-\d{2}-\d{2}$/);
  await expect(page.locator("#today-title")).not.toHaveText("");

  await page.goto("/web/index.html#/track");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(page.locator("#screen-track")).toBeVisible();
});

test("keyboard only: skip link, navigation and a manual entry", async ({ app: page }) => {
  // The first Tab reaches the skip link, which appears only when focused.
  await expect(page.locator(".chrona-skip-link")).not.toBeInViewport();
  await page.keyboard.press("Tab");
  await expect(page.locator(".chrona-skip-link")).toBeFocused();
  await expect(page.locator(".chrona-skip-link")).toBeInViewport();
  await setUp(page);

  // Navigation buttons are reachable and operable from the keyboard.
  await page.locator(".chrona-nav__link:has-text('Track')").focus();
  await page.keyboard.press("Enter");
  await expect(page.locator("#screen-track")).toBeVisible();

  await page.locator("#manual-activity-type").focus();
  await page.keyboard.press("ArrowDown");
  await page.locator("#manual-project").focus();
  await page.keyboard.press("ArrowDown");
  await page.locator("#save-manual").focus();
  await page.keyboard.press("Enter");
  await expect(page.locator("#manual-problems")).toBeVisible();
  // Focus is visible: Forma's focus ring is drawn.
  await expect(page.locator("#save-manual")).not.toHaveCSS("box-shadow", "none");
});

test.describe("on a phone", () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test("the sidebar gives way to a bottom tab bar, with no sideways scrolling", async ({ app: page }) => {
    await expect(page.locator(".chrona-sidebar")).toBeHidden();
    await expect(page.locator(".chrona-tabbar")).toBeVisible();
    await page.click(".chrona-tabbar__link:has-text('Track')");
    await expect(page.locator("#screen-track")).toBeVisible();
    await expect(page.locator(".chrona-tabbar__link[aria-current=page]")).toHaveText("TRTrack");
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBe(0);
    // Touch targets are at least 44 CSS pixels tall.
    for (const box of await page.locator(".chrona-tabbar__link, #start-timer").evaluateAll((els) => els.map((e) => e.getBoundingClientRect().height))) {
      expect(box).toBeGreaterThanOrEqual(44);
    }
  });
});
