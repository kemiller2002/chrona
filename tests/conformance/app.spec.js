// UI-LAYOUT-INTERACTION@1.0.0 conformance for the Chrona application
// (web/index.html), chrona#112 / GH-112. Each test cites the rules it
// observes; peers and actions are declared in chrona.contract.mjs, by
// accessible role and name. Reports are attached to each test and become
// the evidence document (tools/ui-conformance/evidence.mjs).
import {
  test, expect, setUp, yesterday, addEntry, STATUS,
  record, setWidth, open, expectOutcome, recordObserved, expectRowOrStack, expectNoOverflowRecorded, scaleDefaultText
} from "./support.mjs";
import { inventoryActions, measureLabelIntegrity, recordTabSequence, wasFocusedDuringSequence } from "./forma-conformance/index.mjs";
import { ALL_SURFACES, CONTRACT_WIDTHS, declaredActionFor } from "./chrona.contract.mjs";

test.use({ timezoneId: "America/New_York" });

const url = (pattern) => ({ observe: { kind: "url", expected: pattern } });
const shows = (role, name) => ({ observe: { kind: "visible", target: { role, name }, expected: true } });
const iso = (date) => new Intl.DateTimeFormat("en-CA", { timeZone: "America/New_York", year: "numeric", month: "2-digit", day: "2-digit" }).format(date);
const today = () => iso(new Date());
const dayBefore = (day) => iso(new Date(new Date(`${day}T12:00:00-05:00`).getTime() - 864e5));
const dayLabel = (day) => new Intl.DateTimeFormat("en-US", { timeZone: "America/New_York", weekday: "long", month: "long", day: "numeric" }).format(new Date(`${day}T12:00:00-05:00`));

// Two records yesterday, through the page: the seeded state.
async function seeded(page) {
  await open(page, "#/more", "#screen-more");
  await setUp(page);
  await open(page, "#/track", "#screen-track");
  await addEntry(page, { start: "09:00", end: "10:00", description: "First" });
  await addEntry(page, { start: "10:00", end: "10:30", description: "Second" });
}

// ---- ULI-A10 inventory: every enabled action shown is declared -------------

const EMPTY_ROUTES = ["#/", "#/week", "#/month", "#/periods", "#/projects", "#/track", "#/review", "#/reports", "#/candidates", "#/more", "#/nowhere"];

async function undeclared(page) {
  const controls = await inventoryActions(page);
  return controls.filter((control) => !declaredActionFor(ALL_SURFACES, control)).map((control) => `${control.role} "${control.name}"`);
}

for (const width of [1280, 390]) {
  test(`every enabled action on every screen is declared, empty and with records, at ${width}px (ULI-A01, ULI-A09, ULI-A10)`, async ({ app: page }) => {
    await setWidth(page, width);
    const missing = [];
    for (const hash of EMPTY_ROUTES) {
      await open(page, hash);
      missing.push(...(await undeclared(page)).map((item) => `${hash}: ${item}`));
    }
    await seeded(page);
    const day = yesterday();
    for (const hash of ["#/track", `#/day/${day}`, `#/week/${day}`, `#/periods/${day}`, "#/projects", `#/review/${day}`, "#/reports", "#/more"]) {
      await open(page, hash);
      missing.push(...(await undeclared(page)).map((item) => `${hash}: ${item}`));
    }
    await open(page, `#/day/${day}`);
    await page.getByRole("link", { name: "First", exact: true }).click();
    await expect(page.getByRole("heading", { level: 1, name: "First" })).toBeVisible();
    missing.push(...(await undeclared(page)).map((item) => `activity: ${item}`));
    await open(page, "#/projects");
    await page.getByRole("link", { name: "HelixNote", exact: true }).click();
    await expect(page).toHaveURL(/#\/projects\/PRJ-/);
    missing.push(...(await undeclared(page)).map((item) => `project: ${item}`));

    await record({ kind: "inventory", id: `inventory@${width}` }, {
      ruleId: "ULI-A10",
      status: missing.length === 0 ? STATUS.PASS : STATUS.FAIL,
      reason: missing.length === 0 ? "every enabled, visible action is declared in chrona.contract.mjs" : `undeclared: ${missing.join("; ")}`
    });
    expect(missing, "enabled actions not declared in tests/conformance/chrona.contract.mjs").toEqual([]);
  });
}

// ---- Shell: navigation, copy link, skip link -------------------------------

for (const width of [1280, 390]) {
  test(`primary navigation reaches each place by its link at ${width}px (ULI-A01, ULI-A02, ULI-A06)`, async ({ app: page }) => {
    await setWidth(page, width);
    const scope = { role: "navigation", name: "Primary" };
    await expectOutcome(page, "shell.nav-track", { scope, ...url(/#\/track$/) });
    await expect(page.locator("#screen-track")).toBeVisible();
    await expectOutcome(page, "shell.nav-month", { scope, ...url(/#\/month$/) });
    await expect(page.locator("#screen-month")).toBeVisible();
    await expectOutcome(page, "shell.nav-today", { scope, ...url(/#\/$/) });
    await expect(page.locator("#screen-today")).toBeVisible();
    await expectOutcome(page, "shell.nav-more", { scope, ...url(/#\/more$/) });
    await expect(page.locator("#screen-more")).toBeVisible();
  });
}

test("Copy link says it copied, and the clipboard holds the view's address (ULI-A02, ULI-A07)", async ({ app: page, context, baseURL }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"], { origin: new URL(baseURL).origin });
  await open(page, "#/month");
  await expectOutcome(page, "shell.copy-link", shows("text", "Link copied."));
  expect(await page.evaluate(() => navigator.clipboard.readText())).toMatch(/\/web\/index\.html#\/month\/\d{4}-\d{2}$/);
});

test("the navigation's labels stay whole at every width and at 200% text (ULI-L06, ULI-L09)", async ({ app: page }) => {
  for (const scale of [1, 2]) {
    if (scale === 2) await scaleDefaultText(page, 2);
    for (const width of CONTRACT_WIDTHS) {
      await setWidth(page, width);
      await open(page, "#/");
      const nav = page.getByRole("navigation", { name: "Primary" });
      await expect(nav).toHaveCount(1);
      for (const name of ["Today", "Track", "Month", "More"]) {
        const report = await measureLabelIntegrity(nav.getByRole("link", { name, exact: true }));
        await record({ kind: "surface", id: "app-shell", check: `nav label ${name}`, width, textScale: scale }, report);
        expect(report.status, `${name}@${width}x${scale}: ${report.reason}`).toBe(STATUS.PASS);
      }
    }
  }
});

test("the keyboard reaches the skip link, the navigation and the screen's primary action (ULI-A08)", async ({ app: page }) => {
  await open(page, "#/");
  await page.locator("body").focus();
  await recordTabSequence(page, 14);
  const reached = {};
  for (const [label, locator] of [
    ["Skip to content", page.getByRole("button", { name: "Skip to content" })],
    ["Today", page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: "Today", exact: true })],
    ["More", page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: "More", exact: true })],
    ["Previous day", page.getByRole("button", { name: "Previous day" })]
  ]) reached[label] = await wasFocusedDuringSequence(locator);
  const missed = Object.entries(reached).filter(([, focused]) => !focused).map(([label]) => label);
  await record({ kind: "surface", id: "app-shell", check: "tab sequence" }, { ruleId: "ULI-A08", status: missed.length ? STATUS.FAIL : STATUS.PASS, reason: missed.length ? `not reached: ${missed.join(", ")}` : "each reached by Tab" });
  expect(missed).toEqual([]);
});

// ---- Today -----------------------------------------------------------------

test("Today's day navigation and its ways onward change the place (ULI-A02, ULI-A05)", async ({ app: page }) => {
  await open(page, "#/", "#screen-today");
  const now = today();
  const before = dayBefore(now);
  await expectOutcome(page, "today.previous-day", url(new RegExp(`#/day/${before}$`)));
  await expectOutcome(page, "today.next-day", url(new RegExp(`#/(day/${now})?$`)));
  await open(page, `#/day/${before}`, "#screen-today");
  await expectOutcome(page, "today.today", url(/#\/$/));
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(new Intl.DateTimeFormat("en-US", { timeZone: "America/New_York", weekday: "long", month: "long", day: "numeric" }).format(new Date()));
  await expectOutcome(page, "today.week", url(/#\/week\/\d{4}-\d{2}-\d{2}$/));
  await open(page, "#/", "#screen-today");
  await expectOutcome(page, "today.start-tracking", url(/#\/track$/));
  await open(page, "#/", "#screen-today");
  await expectOutcome(page, "today.open-period", url(/#\/periods\/\d{4}-\d{2}-\d{2}$/));
  await open(page, "#/", "#screen-today");
  await expectOutcome(page, "today.review", url(new RegExp(`#/review(/${now})?$`)));
  await open(page, "#/", "#screen-today");
  await expectOutcome(page, "today.add-time", url(/#\/track$/));
});

// ---- Track: failure and recovery, and the timer --------------------------------

test("a manual entry with problems is refused beside its form, then recorded once corrected (ULI-A07, ULI-A10)", async ({ app: page }) => {
  await setUp(page);
  await open(page, "#/track", "#screen-track");
  const form = page.getByRole("form", { name: "Add time manually" });
  await recordObserved("track.save-manual", "ULI-A07", async () => {
    // Before: no problems listed, nothing announced.
    await expect(page.locator("#manual-problems")).toHaveCount(0);
    await form.getByRole("button", { name: "Save activity" }).click();
    // Refused: the problems are listed and nothing claims to be saved.
    await expect(page.locator("#manual-problems li").first()).toBeVisible();
    await expect(page.locator("p[role=status][aria-live=polite]")).not.toHaveText(/^Saved /);
    // Recovery: corrected, it is saved once, and the form is cleared.
    await addEntry(page, { start: "09:00", end: "09:45", description: "Recovered" });
    await expect(page.locator("#manual-problems")).toHaveCount(0);
    await expect(form.getByRole("textbox", { name: "What did you do?" })).toHaveValue("");
  });
  await open(page, `#/day/${yesterday()}`, "#screen-today");
  await expect(page.locator("#day-total")).toHaveText("45m");
});

test("the empty Track screen sends the person to set up, and the timer refuses to start without a type and project (ULI-A02, ULI-A07)", async ({ app: page }) => {
  await open(page, "#/track", "#screen-track");
  await recordObserved("track.start-timer", "ULI-A07", async () => {
    await expect(page.getByRole("button", { name: "Pause" })).toHaveCount(0);
    await page.getByRole("button", { name: "Start timer" }).click();
    await expect(page.locator("#timer-problems li").first()).toBeVisible();
    await expect(page.getByRole("button", { name: "Pause" })).toHaveCount(0);
  });
  await expectOutcome(page, "track.set-up", url(/#\/more$/));
});

test("the timer runs, pauses, and its chip leads back to it from elsewhere (ULI-A02, ULI-L10)", async ({ app: page }) => {
  await setUp(page);
  await open(page, "#/track", "#screen-track");
  const timer = page.getByRole("region", { name: "Ready when you are" });
  await timer.getByRole("combobox", { name: "Activity type" }).selectOption({ label: "Research" });
  await timer.getByRole("combobox", { name: "Project" }).selectOption({ label: "HelixNote" });
  await expectOutcome(page, "track.start-timer", shows("button", "Pause"));
  for (const width of CONTRACT_WIDTHS) {
    await setWidth(page, width);
    await expectRowOrStack(page, "track-running-actions", width);
  }
  await setWidth(page, 1280);
  await open(page, "#/", "#screen-today");
  await expectOutcome(page, "shell.timer-chip", { name: /\d{2}:\d{2}:\d{2}$/, ...url(/#\/track$/) });
  await expectOutcome(page, "track.pause", shows("button", "Resume"));
});

// ---- Activity, review, week, month, period, projects ----------------------------

test("from records: an activity opens and leads back; review refuses then accepts an attestation (ULI-A02, ULI-A07)", async ({ app: page }) => {
  await seeded(page);
  const day = yesterday();
  await open(page, `#/day/${day}`, "#screen-today");
  await expectOutcome(page, "today.record", { name: "First", ...url(/#\/entries\/ACT-[0-9a-f]+(\?on=\d{4}-\d{2}-\d{2})?$/) });
  await expect(page.getByRole("heading", { level: 1, name: "First" })).toBeVisible();
  await expectOutcome(page, "activity.back", url(/#\/$/));

  await open(page, `#/review/${day}`, "#screen-review");
  await recordObserved("review.attest", "ULI-A07", async () => {
    await expect(page.locator("#attestations li")).toHaveCount(0);
    await page.getByRole("button", { name: "Attest to this day" }).click();
    await expect(page.locator("#attest-problems li")).toHaveText(["Add an attestation statement."]);
    await expect(page.locator("#attestations li")).toHaveCount(0);
    await page.locator("#attest-statement").fill("Complete and accurate.");
    await page.getByRole("button", { name: "Attest to this day" }).click();
    await expect(page.locator("#attestations li")).toHaveCount(1);
    await expect(page.locator("#attest-problems")).toHaveCount(0);
  });
  await expectOutcome(page, "review.back", url(/#\/$/));
});

test("week, month and period navigate, open their days, and a period is submitted and reopened (ULI-A02, ULI-A07)", async ({ app: page }) => {
  await seeded(page);
  const day = yesterday();
  await open(page, `#/week/${day}`, "#screen-week");
  const week = page.url().match(/#\/week\/(\d{4}-\d{2}-\d{2})/)[1];
  await expectOutcome(page, "week.previous", url(new RegExp(`#/week/(?!${week})\\d{4}-\\d{2}-\\d{2}$`)));
  await expectOutcome(page, "week.next", url(new RegExp(`#/week/${week}$`)));
  await expectOutcome(page, "week.day", { name: dayLabel(day), ...url(new RegExp(`#/day/${day}$`)) });

  await open(page, `#/month/${day.slice(0, 7)}`, "#screen-month");
  await expectOutcome(page, "month.previous", url(new RegExp(`#/month/(?!${day.slice(0, 7)})\\d{4}-\\d{2}$`)));
  await expectOutcome(page, "month.next", url(new RegExp(`#/month/${day.slice(0, 7)}$`)));
  await expectOutcome(page, "month.report", url(new RegExp(`#/reports\\?from=${day.slice(0, 7)}-01&to=${day.slice(0, 7)}-\\d{2}`)));

  await open(page, `#/periods/${day}`, "#screen-period");
  const period = page.url().match(/#\/periods\/(\d{4}-\d{2}-\d{2})/)[1];
  await expectOutcome(page, "period.previous", url(new RegExp(`#/periods/(?!${period})\\d{4}-\\d{2}-\\d{2}$`)));
  await expectOutcome(page, "period.next", url(new RegExp(`#/periods/${period}$`)));
  await expectOutcome(page, "period.submit", shows("button", "Reopen this period"));
  await recordObserved("period.reopen", "ULI-A07", async () => {
    await page.getByRole("button", { name: "Reopen this period" }).click();
    await expect(page.locator("#review-problems li").first()).toBeVisible();
    await expect(page.getByRole("button", { name: "Submit this period" })).toHaveCount(0);
    await page.getByRole("textbox", { name: "Why reopen it" }).fill("A correction is needed");
    await page.getByRole("button", { name: "Reopen this period" }).click();
    await expect(page.getByRole("button", { name: "Submit this period" })).toBeVisible();
  });
  await expectOutcome(page, "period.day", { name: dayLabel(day), ...url(new RegExp(`#/day/${day}$`)) });
});

test("projects lead to a project, its records, its day, week and report (ULI-A02)", async ({ app: page }) => {
  await seeded(page);
  await open(page, "#/projects", "#screen-projects");
  await expectOutcome(page, "projects.project", { name: "HelixNote", ...url(/#\/projects\/PRJ-[0-9a-f]+$/) });
  const project = page.url().split("#")[1];
  await expectOutcome(page, "project.today", url(/#\/day\/\d{4}-\d{2}-\d{2}\?project=PRJ-[0-9a-f]+$/));
  await open(page, project, "#screen-project");
  await expectOutcome(page, "project.week", url(/#\/week\/\d{4}-\d{2}-\d{2}\?project=PRJ-[0-9a-f]+$/));
  await open(page, project, "#screen-project");
  await expectOutcome(page, "project.report", url(/#\/reports\?project=PRJ-[0-9a-f]+$/));
  await open(page, project, "#screen-project");
  await expectOutcome(page, "project.record", { name: "Second", ...url(/#\/entries\/ACT-[0-9a-f]+/) });
  await open(page, project, "#screen-project");
  await expectOutcome(page, "project.all", url(/#\/projects$/));
});

test("candidate filters change the address and the current filter (ULI-A02, ULI-A06)", async ({ app: page }) => {
  await open(page, "#/candidates", "#screen-candidates");
  const filters = { role: "navigation", name: "Which candidates" };
  await expectOutcome(page, "candidates.status", { name: "Decided", scope: filters, observe: { kind: "attribute", target: { role: "link", name: "Decided", scope: page.getByRole("navigation", { name: "Which candidates" }) }, attribute: "aria-current", expected: "page" } });
  await expect(page).toHaveURL(/#\/candidates\?status=decided$/);
  await expectOutcome(page, "candidates.status", { name: "All", scope: filters, observe: { kind: "attribute", target: { role: "link", name: "All", scope: page.getByRole("navigation", { name: "Which candidates" }) }, attribute: "aria-current", expected: "page" } });
  await expectOutcome(page, "candidates.status", { name: "Awaiting a decision", scope: filters, observe: { kind: "attribute", target: { role: "link", name: "Awaiting a decision", scope: page.getByRole("navigation", { name: "Which candidates" }) }, attribute: "aria-current", expected: "page" } });
});

// ---- More ------------------------------------------------------------------------

test("More adds reference data, saves the period and leads to projects and reports (ULI-A02, ULI-A07)", async ({ app: page }) => {
  await open(page, "#/more", "#screen-more");
  await page.getByRole("textbox", { name: "New project" }).fill("HelixNote");
  await expectOutcome(page, "more.add-project", { observe: { kind: "text", target: { role: "group", name: "Projects" }, expected: /HelixNote/ } });
  await page.getByRole("textbox", { name: "New activity type" }).fill("Research");
  await expectOutcome(page, "more.add-type", { observe: { kind: "text", target: { role: "group", name: "Activity types" }, expected: /Research/ } });
  await page.getByRole("textbox", { name: "New tag" }).fill("Backend");
  await expectOutcome(page, "more.add-tag", { observe: { kind: "text", target: { role: "group", name: "Tags" }, expected: /Backend/ } });

  await recordObserved("more.save-period", "ULI-A02", async () => {
    await open(page, "#/", "#screen-today");
    const weekly = await page.locator("#period-label").textContent();
    await open(page, "#/more", "#screen-more");
    await page.getByRole("combobox", { name: "Period" }).selectOption({ label: "Monthly" });
    await page.getByRole("button", { name: "Save period settings" }).click();
    await open(page, "#/", "#screen-today");
    await expect(page.locator("#period-label")).not.toHaveText(weekly);
    await expect(page.locator("#period-label")).toHaveText(/^[A-Z][a-z]{2} 1 – [A-Z][a-z]{2} (28|29|30|31)$/);
  });

  await open(page, "#/more", "#screen-more");
  await expectOutcome(page, "more.all-projects", url(/#\/projects$/));
  await open(page, "#/more", "#screen-more");
  await expectOutcome(page, "more.reports", url(/#\/reports$/));
});

test("a page that names nothing leads home (ULI-A02)", async ({ app: page }) => {
  await open(page, "#/nowhere", "#screen-problem");
  await expectOutcome(page, "problem.home", url(/#\/$/));
  await expect(page.locator("#screen-today")).toBeVisible();
});

// ---- Layout: overflow, text scale, declared groups --------------------------------

const LAYOUT = [
  ["app-today", "#/", "#screen-today", ["today-day-nav", "today-day-nav-heights"]],
  ["app-track", "#/track", "#screen-track", ["track-timer-fields", "track-manual-start", "track-manual-end"]],
  ["app-week", "#/week", "#screen-week", ["week-nav"]],
  ["app-month", "#/month", "#screen-month", ["month-nav"]],
  ["app-period", "#/periods", "#screen-period", ["period-nav"]],
  ["app-projects", "#/projects", "#screen-projects", []],
  ["app-review", "#/review", "#screen-review", []],
  ["app-reports", "#/reports", "#screen-reports", ["reports-dates"]],
  ["app-candidates", "#/candidates", "#screen-candidates", []],
  ["app-more", "#/more", "#screen-more", ["more-add-project", "more-add-type", "more-add-tag"]],
  ["app-not-found", "#/nowhere", "#screen-problem", []]
];

for (const [surface, hash, ready, groups] of LAYOUT) {
  test(`${surface}: no overflow or clipping at 320/390/768/1280, declared groups align or stack in order (ULI-L01, ULI-L05, ULI-L09, ULI-L10)`, async ({ app: page }) => {
    for (const width of CONTRACT_WIDTHS) {
      await setWidth(page, width);
      await open(page, hash, ready);
      await expectNoOverflowRecorded(page, surface, width);
      for (const group of groups) await expectRowOrStack(page, group, width);
      if (surface === "app-today") await expectRowOrStack(page, "topbar-actions", width);
    }
  });
}

// 200% default text (a reader's font-size setting), each width on a fresh
// page so the larger text applies from the first layout.
const TEXT_SCALE_KNOWN = {
  // Forma 0.6.1 `.ef-facts__item > dd` cannot break an unbreakable value;
  // recorded upstream (docs/ui-conformance/upstream-defects.md, F-1).
  "app-more@320": "Forma-owned: an ef-facts value (time zone \"America/New_York\") cannot wrap"
};

for (const [surface, hash, ready] of LAYOUT) {
  test(`${surface}: at 200% text, nothing overflows or clips at 320/390/768/1280 (ULI-L05, ULI-L09)`, async ({ browser, baseURL }) => {
    for (const width of CONTRACT_WIDTHS) {
      const context = await browser.newContext({ baseURL, viewport: { width, height: 900 }, timezoneId: "America/New_York" });
      const page = await context.newPage();
      try {
        await page.goto(`/web/index.html${hash}`);
        await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
        await expect(page.locator(ready)).toBeVisible();
        await scaleDefaultText(page, 2);
        const known = TEXT_SCALE_KNOWN[`${surface}@${width}`];
        if (known) {
          // A known upstream FAIL stays visible as FAIL in the evidence; it
          // must still be failing, or this entry is stale and must go.
          const { measureOverflow } = await import("./forma-conformance/index.mjs");
          const report = await measureOverflow(page);
          await record({ kind: "surface", id: surface, check: "overflow", width, textScale: 2, upstream: known }, { ...report, detail: { escapes: report.escapes } });
          expect(report.status, `${surface}@${width}: the known upstream defect no longer reproduces; remove it from TEXT_SCALE_KNOWN`).toBe(STATUS.FAIL);
        } else {
          await expectNoOverflowRecorded(page, surface, width, 2);
        }
      } finally {
        await context.close();
      }
    }
  });
}
