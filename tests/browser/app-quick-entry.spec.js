// Quick entry (WI-0062, requirement 33): recent work, common durations and
// copying an entry as a draft, in a real browser. Convenience never changes
// exact time silently.
import { test, expect, setUp, yesterday, addEntry } from "./support.js";

test.use({ timezoneId: "America/New_York" });

const announcement = (page) => page.locator("p[role=status][aria-live=polite]");

test("recent work fills the forms, a common duration sets a visible end, and an entry is copied as a draft", async ({ app: page }) => {
  await setUp(page);
  await page.click(".chrona-nav__link:has-text('Track')");
  await addEntry(page, { start: "09:00", end: "10:00", description: "Pairing" });

  // The timer and the manual form offer the recent combination.
  await expect(page.locator("#manual-recent .chrona-chip")).toHaveText(["Research · HelixNote · Pairing"]);
  await page.locator("#manual-recent .chrona-chip").first().click();
  await expect(page.locator("#manual-description")).toHaveValue("Pairing");
  await expect(page.locator("#manual-start-time")).toHaveValue("");

  // Durations need a start; then one writes the end into its field.
  await expect(page.locator("#manual-durations .chrona-chip").first()).toBeDisabled();
  await page.fill("#manual-start-date", yesterday());
  await page.fill("#manual-start-time", "13:00");
  await page.locator("#manual-durations .chrona-chip", { hasText: /^1h 30m$/ }).click();
  await expect(page.locator("#manual-end-time")).toHaveValue("14:30");
  await expect(announcement(page)).toHaveText("End set to 2:30 PM, 1h 30m after the start.");
  await page.fill("#manual-purpose", "Delivery");
  await page.fill("#manual-reason", "Entered from notes");
  await page.click("#save-manual");
  await expect(announcement(page)).toHaveText(/^Saved 1h 30m/);

  // An entry is copied as a new draft for today, its time left blank.
  await page.click(".chrona-nav__link:has-text('Today')");
  await page.fill("#day-picker", yesterday());
  await page.locator("#day-records .chrona-record a.chrona-link").first().click();
  await expect(page).toHaveURL(/#\/entries\//);
  await page.click("#copy-activity");
  await expect(page).toHaveURL(/#\/track$/);
  await expect(page.locator("#manual-description")).toHaveValue("Pairing");
  await expect(page.locator("#manual-start-time")).toHaveValue("");
  await expect(announcement(page)).toHaveText("Copied Pairing into a new entry. Set its time, then save it.");
});
