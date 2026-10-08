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
