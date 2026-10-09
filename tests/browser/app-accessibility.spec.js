// Accessibility and mobile end to end (WI-0064, CHX-350, CHX-360;
// requirements 35 and 36): scenario 42 (the mobile timer workflow), 43 (a
// keyboard-only workflow) and 44 (screen-reader timer behaviour) in a real
// browser; reflow at 320 CSS pixels (the width of a 1280-pixel window at
// 400% zoom); reduced motion; and the weekly totals and a basic sync problem
// on a phone. iPhone VoiceOver needs a person with an iPhone: its checklist
// is docs/accessibility-voiceover-checklist.md.
import { test as base } from "@playwright/test";
import { test, expect, setUp, fakeDeployment, PAGE, afternoon } from "./support.js";

test.use({ timezoneId: "America/New_York" });

const announcement = (page) => page.locator("p[role=status][aria-live=polite]");

const sidewaysScroll = (page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

// Visible elements that reach past the right edge of the viewport: content
// a person could only reach by scrolling sideways, or could not reach at all.
const pastTheEdge = (page) =>
  page.evaluate(() =>
    [...document.querySelectorAll("body *")]
      .filter((element) => {
        const box = element.getBoundingClientRect();
        return box.width > 0 && box.right > window.innerWidth + 1 && getComputedStyle(element).visibility !== "hidden";
      })
      .map((element) => element.outerHTML.slice(0, 120))
  );

const heights = (page, selector) => page.locator(selector).evaluateAll((elements) => elements.map((e) => e.getBoundingClientRect().height));

// Presses Tab until the element is focused: reachable from the keyboard
// alone, in the page's own order.
async function tabTo(page, selector, limit = 120) {
  const target = page.locator(selector).first();
  for (let presses = 0; presses < limit; presses++) {
    if (await target.evaluate((element) => element === document.activeElement)) return;
    await page.keyboard.press("Tab");
  }
  throw new Error(`${selector} was not reached with Tab`);
}

const focused = (page) => page.evaluate(() => document.activeElement?.id || document.activeElement?.tagName);

test.describe("scenario 42: on a phone", () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });

  test("the timer is started, paused, resumed, stopped and completed by touch, and the week's totals are on Today", async ({ app: page }) => {
    test.setTimeout(90_000);
    await setUp(page);
    await page.tap(".chrona-tabbar__link:has-text('Track')");
    await expect(page.locator("#screen-track")).toBeVisible();
    await page.selectOption("#timer-activity-type", { label: "Research" });
    await page.selectOption("#timer-project", { label: "HelixNote" });
    await page.fill("#timer-description", "Phone work");

    await page.tap("#start-timer");
    await expect(page.locator("#timer-state")).toHaveText("Timing now");
    await page.tap("#pause-timer");
    await expect(page.locator("#timer-state")).toHaveText("Paused");
    await page.tap("#resume-timer");
    await expect(page.locator("#timer-state")).toHaveText("Timing now");
    for (const height of await heights(page, "#pause-timer, #stop-timer, .chrona-tabbar__link")) expect(height).toBeGreaterThanOrEqual(44);
    expect(await sidewaysScroll(page)).toBe(0);

    await expect(page.locator("#timer-display")).toHaveText("00:00:31", { timeout: 40_000 });
    await page.tap("#stop-timer");
    await expect(page.locator("#completion")).toBeVisible();
    await page.fill("#complete-purpose", "Ship the phone flow");
    await page.tap("#save-completion");
    await expect(announcement(page)).toHaveText("Saved 1m of Phone work.");

    // Today: the record, and this week's totals, with no sideways scrolling.
    await page.tap(".chrona-tabbar__link:has-text('Today')");
    await expect(page.locator("#day-records .chrona-record .chrona-record__title")).toHaveText("Phone work");
    await page.locator("#period").scrollIntoViewIfNeeded();
    await expect(page.locator("#period")).toBeInViewport();
    await expect(page.locator("#period-title")).toHaveText("This period");
    await expect(page.locator("#period-exact")).toHaveText("1m");
    expect(await sidewaysScroll(page)).toBe(0);
    expect(await pastTheEdge(page)).toEqual([]);
  });
});

// A basic sync problem on a phone: the deployment keeps records on GitHub,
// which cannot be reached. The page that says so, and its way out, fit the
// screen. Failed requests are logged by the browser itself, so this test
// keeps its own guard that allows only those.
base.describe("on a phone, when GitHub cannot be reached", () => {
  base.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true, timezoneId: "America/New_York" });

  base("the problem, and trying again, are on the screen", async ({ page }) => {
    const problems = [];
    page.on("console", (message) => {
      if (message.type() === "error" && !message.text().startsWith("Failed to load resource")) problems.push(message.text());
    });
    page.on("pageerror", (error) => problems.push(error.message));

    await fakeDeployment(page, {
      environment: "test",
      environmentName: "test",
      location: { owner: "acme", repository: "chrona-data", branch: "main", basePath: "deployments/test" },
      identity: { exchange: "https://fides.test", application: "chrona-test", provider: "github", clientId: "Iv23liBROWSER", redirectUri: PAGE },
      organizations: [{ id: "org_acme", displayName: "Acme Consulting", slug: "acme", timeZone: "America/New_York", administrators: ["583231"] }]
    });
    const asked = [];
    await page.route("https://api.github.com/**", (route) => {
      asked.push(route.request().url());
      return route.abort("internetdisconnected");
    });

    await page.goto("/web/index.html");
    await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
    await page.tap("#sign-in-button");

    await expect(page.locator("#store-failed h1")).toHaveText("Your records could not be opened.");
    await expect(page.locator("#store-failure")).toHaveAttribute("role", "alert");
    await expect(page.locator("#store-failure")).not.toHaveText("");
    expect(asked.length).toBeGreaterThan(0);
    await expect(page.locator("#retry-store")).toBeInViewport();
    for (const height of await heights(page, "#retry-store")) expect(height).toBeGreaterThanOrEqual(44);
    expect(await sidewaysScroll(page)).toBe(0);
    expect(await pastTheEdge(page)).toEqual([]);

    const before = asked.length;
    await page.tap("#retry-store");
    await expect.poll(() => asked.length).toBeGreaterThan(before);
    await expect(page.locator("#store-failed h1")).toHaveText("Your records could not be opened.");
    expect(problems, "the page reported errors").toEqual([]);
  });
});

test("scenario 43: the timer, from the keyboard alone, keeps focus on the control that took the pressed one's place", async ({ app: page }) => {
  // Reference data, typed and added from the keyboard.
  await tabTo(page, "#new-project-name");
  await page.keyboard.type("HelixNote");
  await tabTo(page, "#add-project");
  await page.keyboard.press("Enter");
  await tabTo(page, "#new-activity-type-name");
  await page.keyboard.type("Research");
  await tabTo(page, "#add-activity-type");
  await page.keyboard.press("Enter");
  await expect(page.locator("#projects .ef-checkbox__label")).toHaveText(["HelixNote"]);

  await tabTo(page, ".chrona-nav__link:has-text('Track')");
  await page.keyboard.press("Enter");
  await expect(page.locator("#screen-track")).toBeVisible();

  await tabTo(page, "#timer-activity-type");
  await page.keyboard.press("ArrowDown");
  await tabTo(page, "#timer-project");
  await page.keyboard.press("ArrowDown");
  await tabTo(page, "#timer-description");
  await page.keyboard.type("Keyboard work");

  await tabTo(page, "#start-timer");
  await page.keyboard.press("Enter");
  await expect(page.locator("#timer-state")).toHaveText("Timing now");
  // Start gave way to Pause: focus is on Pause, not lost to the document.
  await expect(page.locator("#pause-timer")).toBeFocused();
  await expect(page.locator("#pause-timer")).not.toHaveCSS("box-shadow", "none");

  await page.keyboard.press("Space");
  await expect(page.locator("#timer-state")).toHaveText("Paused");
  await expect(page.locator("#resume-timer")).toBeFocused();

  await page.keyboard.press("Enter");
  await expect(page.locator("#timer-state")).toHaveText("Timing now");
  await expect(page.locator("#pause-timer")).toBeFocused();

  await page.keyboard.press("Tab");
  await expect(page.locator("#stop-timer")).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(announcement(page)).toHaveText("Timer stopped after less than thirty seconds. Nothing was recorded.");
  await expect(page.locator("#start-timer")).toBeFocused();
  expect(await focused(page)).toBe("start-timer");
});

test("scenario 44: a screen reader hears each timer transition once, and never the ticks", async ({ app: page }) => {
  await setUp(page);
  await page.click(".chrona-nav__link:has-text('Track')");
  await page.selectOption("#timer-activity-type", { label: "Research" });
  await page.selectOption("#timer-project", { label: "HelixNote" });

  // Every text the one polite live region takes, in order.
  await page.evaluate(() => {
    const region = document.querySelector("p[role=status][aria-live=polite]");
    window.__heard = [];
    new MutationObserver(() => {
      const text = region.textContent;
      if (text && window.__heard[window.__heard.length - 1] !== text) window.__heard.push(text);
    }).observe(region, { childList: true, characterData: true, subtree: true });
  });

  await page.getByRole("button", { name: "Start timer" }).click();
  await expect(page.getByRole("timer")).toHaveText("00:00:03", { timeout: 6000 });
  // The timer is exposed as a timer, named in words, and not live: its
  // ticks change its name but are never announced.
  await expect(page.getByRole("timer")).toHaveAttribute("aria-label", /second/);
  await expect(page.getByRole("timer")).not.toHaveAttribute("aria-live", /.+/);
  await expect(page.locator("[aria-live=assertive]")).toHaveCount(0);
  await expect(page.locator("[aria-live=polite]")).toHaveCount(1);

  await page.getByRole("button", { name: "Pause" }).click();
  await expect(page.getByRole("button", { name: "Resume" })).toBeVisible();
  await page.getByRole("button", { name: "Resume" }).click();
  await expect(page.getByRole("timer")).toHaveText("00:00:05", { timeout: 6000 });
  await page.getByRole("button", { name: "Stop activity" }).click();
  await expect(page.getByRole("button", { name: "Start timer" })).toBeVisible();

  expect(await page.evaluate(() => window.__heard)).toEqual([
    "Timer started.",
    "Timer paused.",
    "Timer resumed.",
    "Timer stopped after less than thirty seconds. Nothing was recorded."
  ]);
});

test.describe("at 320 CSS pixels wide (a 1280-pixel window at 400% zoom)", () => {
  test.use({ viewport: { width: 320, height: 640 } });

  test("every screen reflows into one column: nothing scrolls sideways or is cut off", async ({ app: page }) => {
    // 00:10 today is in the past whenever this runs.
    await page.clock.setFixedTime(afternoon());
    await setUp(page);
    await page.click(".chrona-tabbar__link:has-text('Track')");
    await page.selectOption("#manual-activity-type", { label: "Research" });
    await page.selectOption("#manual-project", { label: "HelixNote" });
    const today = await page.evaluate(() => new Date().toLocaleDateString("en-CA"));
    await page.fill("#manual-start-date", today);
    await page.fill("#manual-start-time", "00:10");
    await page.fill("#manual-end-time", "00:40");
    await page.fill("#manual-description", "Reflow check");
    await page.fill("#manual-purpose", "Readable at any zoom");
    await page.click("#save-manual");
    await expect(announcement(page)).toHaveText(/^Saved 30m on /);

    const screens = [
      ["Today", "#screen-today"],
      ["Track", "#screen-track"],
      ["Month", "#screen-month"],
      ["More", "#screen-more"]
    ];
    for (const [name, screen] of screens) {
      await page.click(`.chrona-tabbar__link:has-text('${name}')`);
      await expect(page.locator(screen)).toBeVisible();
      expect(await sidewaysScroll(page), name).toBe(0);
      expect(await pastTheEdge(page), name).toEqual([]);
    }

    await page.click("#go-reports");
    await expect(page.locator("#screen-reports")).toBeVisible();
    expect(await sidewaysScroll(page), "Reports").toBe(0);
    expect(await pastTheEdge(page), "Reports").toEqual([]);

    // An activity, opened from Today.
    await page.click(".chrona-tabbar__link:has-text('Today')");
    await page.click("#day-records .chrona-record__title a");
    await expect(page.locator("#screen-activity")).toBeVisible();
    expect(await sidewaysScroll(page), "Activity").toBe(0);
    expect(await pastTheEdge(page), "Activity").toEqual([]);
  });
});

test("with reduced motion asked for, nothing on the page moves", async ({ app: page }) => {
  await setUp(page);
  const moving = (limit) =>
    page.evaluate(
      (seconds) =>
        [...document.querySelectorAll("*")]
          .filter((element) => {
            const style = getComputedStyle(element);
            const longest = (list) => Math.max(...list.split(",").map((value) => parseFloat(value) * (value.trim().endsWith("ms") ? 0.001 : 1)));
            return (style.animationName !== "none" && longest(style.animationDuration) > seconds) || longest(style.transitionDuration) > seconds;
          })
          .map((element) => element.tagName + "." + element.className),
      limit
    );

  // Controls ease their colours by default ...
  expect((await moving(0.01)).length).toBeGreaterThan(0);
  // ... and not at all when the person asks for reduced motion.
  await page.emulateMedia({ reducedMotion: "reduce" });
  expect(await moving(0.01)).toEqual([]);
});
