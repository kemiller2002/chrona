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
    await page.goto("/web/index.html");
    await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
    await use(page);
  }
});

export { expect };
