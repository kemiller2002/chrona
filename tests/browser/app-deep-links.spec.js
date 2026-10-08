// Deep links, end to end in Chromium (CHX-460, WI-0071): every place opens
// cold from its address in a fresh page, an old or non-canonical address is
// corrected in place without a history entry, Back and Forward retrace the
// places, "Copy link" copies an absolute link that opens the same view, an
// address that names nothing shows the not-found page, the skip link leaves
// the address alone, and an address that needs sign-in survives the GitHub
// round trip. The deployment is the repository's own: local, in memory.
import { test, expect, fakeDeployment, PAGE, signInConfiguration, setUp } from "./support.js";

test.use({ timezoneId: "America/New_York" });

// History entries at the moment the document started, before the engine ran.
const startedLength = (page) => page.addInitScript(() => (window.__started = history.length));

// Opens an address cold, as a new document (never a same-document fragment
// change), and waits for the engine to run.
async function open(page, fragment) {
  await startedLength(page);
  await page.goto("about:blank");
  await page.goto(`/web/index.html${fragment}`);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(page.locator(".chrona-shell")).toBeVisible();
}

// Opening an address never adds a history entry.
const noEntryAdded = async (page) => expect(await page.evaluate(() => history.length - window.__started)).toBe(0);

const today = () =>
  new Intl.DateTimeFormat("en-CA", { timeZone: "America/New_York", year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date());

test("every place opens cold from its address", async ({ page }) => {
  const places = [
    ["#/", "#screen-today", "#today-title", null],
    ["#/day/2026-10-01", "#screen-today", "#today-title", "Thursday, October 1"],
    ["#/track", "#screen-track", null, null],
    ["#/month/2026-09", "#screen-month", "#month-title", "September 2026"],
    ["#/review/2026-10-01", "#screen-review", null, null],
    ["#/more", "#screen-more", null, null],
    ["#/more/references", "#screen-more", null, null],
    ["#/more/people", "#screen-more", null, null],
    ["#/week/2026-09-28", "#screen-week", "#week-title", "Sep 28 – Oct 4"],
    ["#/periods/2026-09-28", "#screen-period", "#period-page-title", "Sep 28 – Oct 4"],
    ["#/projects", "#screen-projects", "#projects-title", "Projects"]
  ];

  for (const [fragment, screen, heading, text] of places) {
    await open(page, fragment);
    await expect(page.locator(screen), fragment).toBeVisible();
    await expect(page.locator("#screen-problem"), fragment).toHaveCount(0);
    if (heading && text) await expect(page.locator(heading)).toHaveText(text);
    await expect(page).toHaveURL(`${PAGE}${fragment}`);
    await noEntryAdded(page);
  }
});

test("a report's filters open cold from its address, and changing one replaces it", async ({ page }) => {
  await open(page, "#/reports?from=2026-09-01&to=2026-09-30&method=timer&q=pairing%20review&removed=true&group=day&format=json");
  await expect(page.locator("#screen-reports")).toBeVisible();
  await expect(page.locator("#report-from")).toHaveValue("2026-09-01");
  await expect(page.locator("#report-to")).toHaveValue("2026-09-30");
  await expect(page.locator("#report-method")).toHaveValue("timer");
  await expect(page.locator("#report-text")).toHaveValue("pairing review");
  await expect(page.locator("#report-include-removed")).toBeChecked();
  await expect(page.locator("#report-grouping")).toHaveValue("day");
  await expect(page.locator("#report-format")).toHaveValue("json");
  await noEntryAdded(page);

  // A filter is a refinement: the address follows, replacing the entry.
  await page.selectOption("#report-method", "manual");
  await expect(page).toHaveURL(`${PAGE}#/reports?from=2026-09-01&to=2026-09-30&method=manual&q=pairing%20review&removed=true&group=day&format=json`);
  await page.fill("#report-text", "triage");
  await page.press("#report-text", "Enter");
  await expect(page).toHaveURL(/q=triage&/);
  await noEntryAdded(page);
});

test("an old or non-canonical address opens its place and is corrected without a history entry", async ({ page }) => {
  await open(page, "#/today/2026-10-01");
  await expect(page).toHaveURL(`${PAGE}#/day/2026-10-01`);
  await expect(page.locator("#today-title")).toHaveText("Thursday, October 1");
  await noEntryAdded(page);

  await open(page, "#/reports?format=csv&utm_source=mail&q=a%2fb");
  await expect(page).toHaveURL(`${PAGE}#/reports?q=a%2Fb`);
  await expect(page.locator("#report-text")).toHaveValue("a/b");
  await noEntryAdded(page);
});

test("an address that names nothing shows the not-found page, and its way home", async ({ page }) => {
  for (const [fragment, title] of [
    ["#/nowhere", "Page not found"],
    ["#/day/2026-02-30", "Page not found"],
    ["#/more/billing", "Page not found"],
    ["#/entries/ACT-none", "Not found"],
    ["#/day/2026-10-01?project=PRJ-none", "Not found"],
    ["#/projects/PRJ-none", "Not found"],
    ["#/week/2026-09-28?project=PRJ-none", "Not found"]
  ]) {
    await open(page, fragment);
    await expect(page.locator("#screen-problem"), fragment).toBeVisible();
    await expect(page.locator("#problem-title")).toHaveText(title);
    await expect(page.locator(".chrona-screen")).toHaveCount(1);
    await expect(page.locator("#copy-link")).toHaveCount(0);
    // The address is left as it was, to be corrected by hand.
    await expect(page).toHaveURL(`${PAGE}${fragment}`);
  }

  await page.click("#problem-home");
  await expect(page.locator("#screen-today")).toBeVisible();
  await expect(page).toHaveURL(`${PAGE}#/`);
});

test("Back and Forward retrace places; refining a day replaces the entry", async ({ page }) => {
  await open(page, "#/track");
  await page.click(".chrona-nav__link:has-text('Month')");
  await expect(page).toHaveURL(`${PAGE}#/month`);
  await expect(page.locator("#screen-month")).toBeVisible();
  await page.click(".chrona-nav__link:has-text('Today')");
  await expect(page.locator("#screen-today")).toBeVisible();
  // Two days back: refinements, not new entries.
  await page.click("button[aria-label='Previous day']");
  await page.click("button[aria-label='Previous day']");
  await expect(page).toHaveURL(/#\/day\/\d{4}-\d{2}-\d{2}$/);

  await page.goBack();
  await expect(page).toHaveURL(`${PAGE}#/month`);
  await expect(page.locator("#screen-month")).toBeVisible();
  await page.goBack();
  await expect(page).toHaveURL(`${PAGE}#/track`);
  await expect(page.locator("#screen-track")).toBeVisible();
  await page.goForward();
  await expect(page.locator("#screen-month")).toBeVisible();
});

test("Copy link copies an absolute link that opens the same view, today made explicit", async ({ page, context }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await open(page, "#/");
  await page.click("#copy-link");
  await expect(page.locator("#link-status")).toHaveText("Link copied.");
  const link = await page.evaluate(() => navigator.clipboard.readText());
  expect(link).toBe(`${PAGE}#/day/${today()}`);

  await open(page, "#/reports?method=manual&group=tag");
  await page.click("#copy-link");
  await expect(page.locator("#link-status")).toHaveText("Link copied.");
  const report = await page.evaluate(() => navigator.clipboard.readText());
  const [year, month] = today().split("-").map(Number);
  const last = new Date(Date.UTC(year, month, 0)).getUTCDate();
  const mm = String(month).padStart(2, "0");
  expect(report).toBe(`${PAGE}#/reports?from=${year}-${mm}-01&to=${year}-${mm}-${last}&method=manual&group=tag`);

  // The link opens the same view in a new tab.
  const other = await context.newPage();
  await other.goto(report);
  await expect(other.locator("#screen-reports")).toBeVisible();
  await expect(other.locator("#report-method")).toHaveValue("manual");
  await expect(other.locator("#report-grouping")).toHaveValue("tag");
});

test("Copy link shows the link to select when the browser will not copy it", async ({ page }) => {
  await open(page, "#/track");
  // No clipboard permission in a headless page without a user grant: refused.
  await page.evaluate(() => {
    Object.defineProperty(navigator, "clipboard", { value: { writeText: () => Promise.reject(new Error("denied")) } });
  });
  await page.click("#copy-link");
  await expect(page.locator("#link-text")).toHaveValue(`${PAGE}#/track`);
});

test("the skip link moves focus to the content and leaves the address alone", async ({ page }) => {
  await open(page, "#/track");
  await page.keyboard.press("Tab");
  await expect(page.locator(".chrona-skip-link")).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(page.locator("#main")).toBeFocused();
  await expect(page).toHaveURL(`${PAGE}#/track`);
  await expect(page.locator("#screen-track")).toBeVisible();
});

test("an address that needs sign-in survives the GitHub round trip, and no address carries the code", async ({ page }) => {
  await fakeDeployment(page, signInConfiguration);
  const target = "#/reports?q=pairing&group=day";
  await page.goto(`/web/index.html${target}`);
  await expect(page.locator("#sign-in")).toBeVisible();
  await expect(page).toHaveURL(`${PAGE}#/sign-in?returnTo=%2Freports%3Fq%3Dpairing%26group%3Dday`);

  await page.click("#sign-in-button");
  await expect(page.locator(".chrona-shell")).toBeVisible();
  await expect(page).toHaveURL(`${PAGE}${target}`);
  await expect(page.locator("#screen-reports")).toBeVisible();
  await expect(page.locator("#report-text")).toHaveValue("pairing");
  await expect(page.locator("#report-grouping")).toHaveValue("day");
  expect(page.url()).not.toMatch(/code=|state=/);
  // Read once, then forgotten from the tab.
  expect(await page.evaluate(() => sessionStorage.getItem("chrona.returnTo"))).toBeNull();
});

test("a week or period named by another of its days is corrected to its first day", async ({ page }) => {
  await open(page, "#/week/2026-10-01");
  await expect(page).toHaveURL(`${PAGE}#/week/2026-09-28`);
  await expect(page.locator("#week-title")).toHaveText("Sep 28 – Oct 4");
  await noEntryAdded(page);
  await open(page, "#/periods/2026-10-01");
  await expect(page).toHaveURL(`${PAGE}#/periods/2026-09-28`);
  await noEntryAdded(page);
});

test("a project's page is reached from More and links to its day, week and report", async ({ page }) => {
  await open(page, "#/more");
  await setUp(page);
  await page.click("#all-projects");
  await expect(page).toHaveURL(`${PAGE}#/projects`);
  await page.click("#project-list a:has-text('HelixNote')");
  await expect(page.locator("#project-title")).toHaveText("HelixNote");
  const id = page.url().split("#/projects/")[1];
  await expect(page.locator("#project-report")).toHaveAttribute("href", `#/reports?project=${id}`);
  await page.click("#project-week");
  await expect(page.locator("#screen-week")).toBeVisible();
  await expect(page.locator("#week-project")).toHaveValue(id);
});
