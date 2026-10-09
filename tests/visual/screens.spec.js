// Visual regression (WI-0054, CHX-005): each of Chrona's screens, in light
// and dark, at the legacy captures' widths (1440 and 390 CSS pixels), held as
// baseline images so an unintended visual change fails the build. How each
// screen keeps the legacy look is reviewed in docs/legacy/visual-comparison.md.
//
// The page's clock is fixed (dates, the day's title and the timer read it),
// and the records are entered through the page, so every render is the same.
import { test, expect, setUp, fakeDeployment, signInConfiguration, PAGE } from "../browser/support.js";
import { serveGitHub, repository, headFiles } from "../browser/github-fake.js";
import { test as base } from "@playwright/test";

// Against GitHub, a folder not made yet is a 404 the browser logs itself:
// these pages fail on any other error.
const onGitHub = base.extend({
  page: async ({ page }, use) => {
    const problems = [];
    page.on("console", (message) => {
      if (message.type() === "error" && !message.text().startsWith("Failed to load resource")) problems.push(message.text());
    });
    page.on("pageerror", (error) => problems.push(error.message));
    await use(page);
    expect(problems, "the page reported errors").toEqual([]);
  }
});

const morning = new Date("2026-09-10T14:41:00-04:00");
const date = "2026-09-10";

const schemes = ["light", "dark"];

async function open(page) {
  await page.clock.setFixedTime(morning);
  await page.goto("/web/index.html#/more");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(page.locator("#zone")).toHaveText("America/New_York");
}

async function go(page, hash, ready) {
  await page.evaluate((target) => (window.location.hash = target), hash);
  await expect(page.locator(ready)).toBeVisible();
}

async function manual(page, start, end, description, purpose) {
  await page.selectOption("#manual-activity-type", { label: "Research" });
  await page.selectOption("#manual-project", { label: "HelixNote" });
  await page.fill("#manual-start-date", date);
  await page.fill("#manual-start-time", start);
  await page.fill("#manual-end-time", end);
  await page.fill("#manual-description", description);
  await page.fill("#manual-purpose", purpose);
  await page.click("#save-manual");
  await expect(page.locator("p[role=status][aria-live=polite]")).toHaveText(/^Saved /);
}

// Each screen in both schemes. The page is scrolled to the top and the
// pointer parked, so neither a scroll position nor a hover is captured.
async function capture(page, name, width) {
  await page.mouse.move(0, 0);
  for (const scheme of schemes) {
    await page.emulateMedia({ colorScheme: scheme });
    await page.evaluate(() => window.scrollTo(0, 0));
    await expect(page).toHaveScreenshot(`${name}-${width}-${scheme}.png`, { fullPage: true });
  }
}

for (const [width, viewport] of [
  ["desktop", { width: 1440, height: 900 }],
  ["phone", { width: 390, height: 844 }]
]) {
  test.describe(`at ${width} width`, () => {
    test.use({ viewport });

    test("every screen keeps its look", async ({ page }) => {
      await open(page);
      await setUp(page);
      await go(page, "#/track", "#screen-track");
      await manual(page, "08:10", "09:02", "Visual engineering research", "Define the ledger's mobile verification patterns");
      await manual(page, "09:18", "11:04", "HelixNote development", "Migrate the repository boundary");

      await go(page, "#/today", "#screen-today");
      await capture(page, "today", width);
      await go(page, `#/review/${date}`, "#screen-review");
      await capture(page, "review", width);
      await go(page, "#/month", "#screen-month");
      await capture(page, "month", width);
      await go(page, "#/today", "#screen-today");
      await page.click("#day-records .chrona-record__title a >> nth=0");
      await expect(page.locator("#screen-activity")).toBeVisible();
      await capture(page, "activity", width);
      await go(page, "#/more", "#screen-more");
      await capture(page, "more", width);
      await go(page, "#/reports", "#screen-reports");
      await capture(page, "reports", width);
      // The week, the period and the projects (CHX-460).
      await go(page, `#/week/${date}`, "#screen-week");
      await capture(page, "week", width);
      await go(page, `#/periods/${date}`, "#screen-period");
      await capture(page, "period", width);
      await go(page, "#/projects", "#screen-projects");
      await capture(page, "projects", width);
      await page.click("#project-list a >> nth=0");
      await expect(page.locator("#screen-project")).toBeVisible();
      await capture(page, "project", width);
      // An address that names nothing (CHX-460).
      await go(page, "#/nowhere", "#screen-problem");
      await capture(page, "not-found", width);
      await go(page, "#/track", "#screen-track");
      await capture(page, "track", width);

      // The timer running, then stopped and held for completion.
      await page.selectOption("#timer-activity-type", { label: "Research" });
      await page.selectOption("#timer-project", { label: "HelixNote" });
      await page.fill("#timer-description", "Product research");
      await page.click("#start-timer");
      await expect(page.locator("#timer-state")).toHaveText("Timing now");
      await capture(page, "timer", width);
      await page.clock.setFixedTime(new Date(morning.getTime() + 32 * 60_000 + 18_000));
      await expect(page.locator("#timer-display")).toHaveText("00:32:18");
      await page.click("#stop-timer");
      await expect(page.locator("#completion")).toBeVisible();
      await capture(page, "completion", width);
    });

    // Observations' candidates (WI-0038): they come from a producer's inbox
    // on GitHub, so this deployment keeps its records there (the fake).
    onGitHub("the candidates keep their look", async ({ page }) => {
      const dataset = "deployments/test/chrona/datasets/org_acme";
      await fakeDeployment(page, {
        environment: "test",
        environmentName: "test",
        location: { owner: "acme", repository: "chrona-data", branch: "main", basePath: "deployments/test" },
        identity: { exchange: "https://fides.test", application: "chrona-test", provider: "github", clientId: "Iv23liBROWSER", redirectUri: PAGE },
        organizations: [{ id: "org_acme", displayName: "Acme Consulting", slug: "acme", timeZone: "America/New_York", administrators: ["583231"] }]
      });
      const github = await serveGitHub(page.context(), repository({ owner: "acme", name: "chrona-data" }));
      await page.clock.setFixedTime(morning);
      await page.goto("/web/index.html#/more");
      await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
      await page.click("#sign-in-button");
      await expect(page.locator("#screen-more")).toBeVisible({ timeout: 20_000 });
      await setUp(page);
      const projectFile = () => Object.keys(headFiles(github.current())).find((path) => /\/records\/[^/]+\/project\/[^/]+\.json$/.test(path));
      await expect.poll(projectFile).toBeTruthy();
      const projectId = projectFile().split("/").pop().replace(/\.json$/, "");
      const observed = (id, start, minutes, description) =>
        JSON.stringify({
          contract: "chrona.time-observation",
          version: 1,
          observationId: id,
          sourceSystem: id.startsWith("jira") ? "jira" : "github",
          organizationId: "org_acme",
          projectId,
          actorId: "github:583231",
          workItemId: "PR-7",
          externalUrl: "https://example.test/pull/7",
          timing: { kind: "interval", start, finish: new Date(new Date(start).getTime() + minutes * 60_000).toISOString() },
          description,
          evidence: [{ kind: "pull-request", reference: "https://example.test/pull/7" }],
          observedAt: "2026-09-10T13:00:00.000Z"
        });
      github.commit({
        [`${dataset}/inbox/github/obs-1.json`]: observed("obs-1", "2026-09-10T12:15:00.000Z", 45, "Reviewed the ledger pull request"),
        [`${dataset}/inbox/jira/jira-2.json`]: observed("jira-2", "2026-09-10T13:30:00.000Z", 30, "Triaged the import backlog"),
        [`${dataset}/inbox/github/broken.json`]: "{}"
      });
      await page.click("#read-inboxes");
      await expect(page.locator("#inbox-invalid")).toBeVisible();
      await capture(page, "more-inbox", width);
      await go(page, "#/candidates", "#screen-candidates");
      await expect(page.locator("#candidate-list li")).toHaveCount(2);
      await capture(page, "candidates", width);
      await go(page, "#/candidates/CAND-github-obs-1", "#screen-candidate");
      await expect(page.locator("#accept-candidate")).toBeVisible();
      await capture(page, "candidate", width);
    });

    test("the sign-in page keeps its look", async ({ page }) => {
      await fakeDeployment(page, signInConfiguration);
      await page.clock.setFixedTime(morning);
      await page.goto("/web/index.html");
      await expect(page.locator("#sign-in")).toBeVisible();
      await capture(page, "sign-in", width);
    });
  });
}
