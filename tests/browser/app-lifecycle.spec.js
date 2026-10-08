// The Chrona application's lifecycle and review screens end to end
// (WI-0047): correct, remove, restore, split and merge activities, link
// evidence, review and attest a day, and the month, in a real browser.
import { test, expect, setUp, yesterday, addEntry } from "./support.js";

test.use({ timezoneId: "America/New_York" });

const announcement = (page) => page.locator("p[role=status][aria-live=polite]");

// Reference data and three entries yesterday: 9:00-10:00, 10:00-10:30 and
// 11:00-11:30, shown on that day's Today screen.
async function day(page) {
  await setUp(page);
  await page.click(".chrona-nav__link:has-text('Track')");
  await addEntry(page, { start: "09:00", end: "10:00", description: "First" });
  await addEntry(page, { start: "10:00", end: "10:30", description: "Second" });
  await addEntry(page, { start: "11:00", end: "11:30", description: "Third" });
  await page.click(".chrona-nav__link:has-text('Today')");
  await page.fill("#day-picker", yesterday());
  await expect(page.locator("#day-total")).toHaveText("2h");
}

const record = (page, title) => page.locator("#day-records .chrona-record", { has: page.locator(`button:text-is("${title}")`) });

test("an activity is corrected, removed from totals and restored, with its history kept", async ({ app: page }) => {
  await day(page);
  await record(page, "First").locator("button.chrona-link").click();
  await expect(page).toHaveURL(/#\/activity\//);
  await expect(page.locator("#activity-title")).toHaveText("First");
  await expect(page.locator("#detail-revision")).toHaveText("1");

  await page.fill("#amend-purpose", "");
  await page.click("#save-amend");
  await expect(page.locator("#amend-problems li")).toHaveText(["Add the business purpose: why this work mattered to the business."]);

  await page.fill("#amend-purpose", "Delivery");
  await page.fill("#amend-description", "First, corrected");
  await page.fill("#amend-reason", "Typo");
  await page.click("#save-amend");
  await expect(announcement(page)).toHaveText("Amended. The earlier version stays in the history.");
  await expect(page.locator("#activity-title")).toHaveText("First, corrected");
  await expect(page.locator("#detail-revision")).toHaveText("2");
  await expect(page.locator("#history li strong")).toHaveText(["Amended", "Recorded"]);

  await page.fill("#void-reason", "Duplicate");
  await page.click("#void");
  await expect(page.locator("#activity-title")).toHaveText("First, corrected");
  await expect(page.locator("#screen-activity .chrona-eyebrow")).toHaveText("Removed from totals");
  await page.click("#restore");
  await expect(announcement(page)).toHaveText("Restored to totals.");
  await expect(page.locator("#history li strong")).toHaveText(["Restored", "Removed from totals", "Amended", "Recorded"]);
});

test("evidence is linked and checked, and a split assigns it to one part", async ({ app: page }) => {
  await day(page);
  await record(page, "First").locator("button.chrona-link").click();

  await page.fill("#evidence-url", "not a link");
  await page.click("#attach");
  await expect(page.locator("#evidence-problems li")).toHaveText([
    "Add a label saying what the evidence is.",
    "A link must be a web address starting with https:// or http://."
  ]);
  await page.selectOption("#evidence-kind", { label: "Pull request" });
  await page.fill("#evidence-label", "PR 4");
  await page.fill("#evidence-url", "https://example.test/pr/4");
  await page.fill("#evidence-source", "GitHub");
  await page.fill("#evidence-notes", "The change that fixed the overlap.");
  await page.click("#attach");
  await expect(page.locator("#evidence-list li")).toHaveCount(1);
  await expect(page.locator("#evidence-list li")).toContainText("PR 4");
  await expect(page.locator("#evidence-list .chrona-evidence__source")).toHaveText("Source: GitHub");
  await expect(page.locator("#evidence-list .chrona-evidence__notes")).toHaveText("The change that fixed the overlap.");

  await page.fill("#split-first", "20");
  await page.fill("#split-second", "20");
  await page.click("#save-split");
  await expect(page.locator("#split-problems li")).toHaveText(["The parts add up to 40 minutes; they must add up to exactly 60."]);
  await page.fill("#split-second", "40");
  await page.selectOption("#split select", { label: "Second part" });
  await page.click("#save-split");
  await expect(page.locator("#replaced-by")).toContainText("Replaced by \"First\" (20m), \"First\" (40m)");

  await page.click("button.chrona-link:has-text('Back to today')");
  await page.fill("#day-picker", yesterday());
  await expect(page.locator("#day-total")).toHaveText("2h");
  await expect(page.locator("#day-records .chrona-record__duration")).toHaveText(["20m", "40m", "30m", "30m"]);
});

test("adjacent activities merge from Today; others are refused with the reason", async ({ app: page }) => {
  await day(page);
  await record(page, "First").locator("input[type=checkbox]").check();
  await record(page, "Third").locator("input[type=checkbox]").check();
  await expect(page.locator("#merge")).toBeVisible();
  await page.click("#save-merge");
  await expect(page.locator("#merge-problems li")).toHaveText(["These cannot be merged: sources are not contiguous."]);

  await record(page, "Third").locator("input[type=checkbox]").uncheck();
  await record(page, "Second").locator("input[type=checkbox]").check();
  await page.fill("#merge-description", "First and second");
  await page.click("#save-merge");
  await expect(announcement(page)).toHaveText("Merged 2 activities into one. The originals are kept, superseded.");
  await expect(page.locator("#day-records .chrona-record__title")).toHaveText(["First and second", "Third"]);
  await expect(page.locator("#day-total")).toHaveText("2h");
});

test("a day is reviewed and attested; a later change is an obligation until reviewed again", async ({ app: page }) => {
  await day(page);
  await page.click("#open-review");
  await expect(page).toHaveURL(new RegExp(`#/review/${yesterday()}$`));
  await expect(page.locator("#review-checks li")).toHaveCount(6);
  await expect(page.locator("#review-checks li[data-tone=ok]")).toHaveCount(6);

  await page.click("#attest-day");
  await expect(page.locator("#attest-problems li")).toHaveText(["Add an attestation statement."]);
  await page.fill("#attest-statement", "Complete and accurate.");
  await page.click("#attest-day");
  await expect(page.locator("#attestations li").first()).toContainText("Complete and accurate.");

  await page.click("button.chrona-link:has-text('Back to today')");
  await page.fill("#day-picker", yesterday());
  await record(page, "Second").locator("button.chrona-link").click();
  await page.fill("#amend-description", "Second, fixed");
  await page.click("#save-amend");
  await page.click("button.chrona-link:has-text('Back to today')");
  await expect(page.locator("#obligations")).toContainText("changed after you attested it");
  await page.click("#obligations button");
  await expect(page.locator("#changed-since li")).toHaveText(["Second, fixed"]);
  await page.fill("#attest-statement", "Rechecked.");
  await page.click("#attest-day");
  await expect(page.locator("#attestations li")).toHaveCount(2);
  await page.click("button.chrona-link:has-text('Back to today')");
  await expect(page.locator("#obligations")).toHaveCount(0);
});

test("the month sums the effective records, by type and by day", async ({ app: page }) => {
  await day(page);
  await page.click(".chrona-nav__link:has-text('Month')");
  await page.fill("#month-picker", yesterday().slice(0, 7));
  await expect(page.locator("#month-total")).toHaveText("2h");
  await expect(page.locator("#month-by-type li")).toHaveCount(1);
  await expect(page.locator("#month-by-type meter")).toHaveJSProperty("value", 100);
  await expect(page.locator("#month-days li")).toHaveCount(1);
});

test("a link to an activity this session does not hold says so", async ({ app: page }) => {
  await page.goto("/web/index.html#/activity/ACT-unknown");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(page.locator("#activity-missing")).toContainText("That activity is not in this session.");
});

test("the timesheet period follows the organization's configuration", async ({ app: page }) => {
  await day(page);
  await expect(page.locator("#period-exact")).toHaveText("2h");
  await page.click(".chrona-nav__link:has-text('More')");
  await page.selectOption("#period-cadence", { label: "Monthly" });
  await page.click(".chrona-nav__link:has-text('Today')");
  await expect(page.locator("#period-label")).toHaveText(/^[A-Z][a-z]{2} 1 – [A-Z][a-z]{2} (28|29|30|31)$/);
});
