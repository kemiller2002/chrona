// Search, reports and export (WI-0049) end to end: filters and search over
// the session's records, totals that keep exact and billable time apart, a
// deterministic export copied through Limen's clipboard effect, and the
// Folio print document opened through Chrona's print pack.
import { test, expect, setUp, yesterday, addEntry } from "./support.js";

test.use({ timezoneId: "America/New_York", permissions: ["clipboard-read", "clipboard-write"] });

async function reportOnYesterday(page) {
  await setUp(page);
  await page.click(".chrona-nav__link:has-text('Track')");
  await addEntry(page, { start: "09:00", end: "09:37", description: "Export work" });
  await addEntry(page, { start: "10:00", end: "10:30", description: "Inbox, triage" });
  await page.click(".chrona-nav__link:has-text('More')");
  await page.click("#go-reports");
  await expect(page).toHaveURL(/#\/reports$/);
  await page.fill("#report-from", yesterday());
  await page.fill("#report-to", yesterday());
  await expect(page.locator("#report-count")).toHaveText("2 activities");
}

test("filters and search narrow the report; exact and billable time stay apart", async ({ app: page }) => {
  await reportOnYesterday(page);
  await expect(page.locator("#report-exact")).toHaveText("1h 07m");
  // Six-minute up per activity: 37 -> 42, 30 -> 30.
  await expect(page.locator("#report-billable")).toHaveText("1h 12m");
  await expect(page.locator("#report-table tbody tr")).toHaveCount(2);

  await page.fill("#report-text", "TRIAGE");
  await expect(page.locator("#report-count")).toHaveText("1 activity");
  await expect(page.locator("#report-table tbody tr strong")).toHaveText(["Inbox, triage"]);
  await page.fill("#report-text", "nothing like this");
  await expect(page.locator("#report-empty")).toBeVisible();
});

test("the export is deterministic CSV or JSON and is copied to the clipboard", async ({ app: page }) => {
  await reportOnYesterday(page);
  const csv = await page.locator("#export-text").inputValue();
  expect(csv.split("\n")[0]).toBe("# schema: chrona.time-report/1");
  expect(csv).toContain(`# filter.from: ${yesterday()}`);
  expect(csv).toContain("\"Inbox, triage\"");

  await page.click("#copy-export");
  await expect(page.locator("#copy-status")).toHaveText("Copied to the clipboard.");
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(csv);

  await page.selectOption("#report-format", { label: "JSON" });
  const json = JSON.parse(await page.locator("#export-text").inputValue());
  expect(json.schema).toBe("chrona.time-report/1");
  expect(json.totals.exactMinutes).toBe(67);
  expect(json.totals.billableMinutes).toBe(72);
  expect(json.activities.map((a) => a.description)).toEqual(["Export work", "Inbox, triage"]);
});

test("printing opens the browser's dialog for Folio's document of the report", async ({ page }) => {
  await page.addInitScript(() => {
    window.__printed = 0;
    window.print = () => { window.__printed += 1; };
  });
  await page.goto("/web/index.html#/more");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(page.locator("html")).toHaveAttribute("data-capabilities", "limen.schedule limen.environment chrona.print");
  await expect(page.locator("#zone")).not.toHaveText("Not yet known");
  await reportOnYesterday(page);

  await page.click("#print-report");
  await expect.poll(() => page.evaluate(() => window.__printed)).toBe(1);

  // On paper only Folio's document shows, with the same report.
  await page.emulateMedia({ media: "print" });
  await expect(page.locator(".chrona-shell")).toBeHidden();
  await expect(page.locator("ef-print-document")).toBeVisible();
  await expect(page.locator("ef-print-document tbody tr").last()).toContainText("Inbox, triage");
  expect(await page.evaluate(() => customElements.get("ef-print-document") !== undefined)).toBe(true);
});
