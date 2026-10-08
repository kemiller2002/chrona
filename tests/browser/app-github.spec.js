// Chrona against GitHub, end to end in Chromium (WI-0069): records stored
// through Arca's GitHub provider in the WASM engine, over the browser's own
// fetch, against the fake GitHub API in ./github-fake.js. Sign-in is Fides'
// fake (./support.js). Then two tabs of one browser and the queue of unsent
// changes they share (WI-0067): only the tab that holds it sends it, a tab
// waiting to take over does when the holder closes, and nothing is sent twice.
//
// Going offline is the browser's own (context.setOffline), so requests fail
// as they would without a network and the browser logs each one; this suite
// therefore keeps its own guard, which allows only those.
import { test as base, expect } from "@playwright/test";
import { fakeDeployment, PAGE, signInAs } from "./support.js";
import { serveGitHub, repository, headFiles, history } from "./github-fake.js";

const configuration = {
  environment: "test",
  environmentName: "test",
  location: { owner: "acme", repository: "chrona-data", branch: "main", basePath: "deployments/test" },
  identity: { exchange: "https://fides.test", application: "chrona-test", provider: "github", clientId: "Iv23liBROWSER", redirectUri: PAGE },
  organizations: [{ id: "org_acme", displayName: "Acme Consulting", slug: "acme", timeZone: "America/New_York", administrators: ["583231"] }]
};

const QUEUE = "arca.queue.chrona.org_acme";

const test = base.extend({
  // Every page of the context fails the test on an error it reports, except
  // the browser's own note of a request that failed while offline.
  context: async ({ context }, use) => {
    const problems = [];
    context.on("page", (page) => {
      page.on("console", (message) => {
        if (message.type() === "error" && !message.text().startsWith("Failed to load resource")) problems.push(message.text());
      });
      page.on("pageerror", (error) => problems.push(error.message));
    });
    await use(context);
    expect(problems, "a page reported errors").toEqual([]);
  },
  github: async ({ context }, use) => {
    await fakeDeployment(context, configuration);
    await use(await serveGitHub(context, repository({ owner: "acme", name: "chrona-data" })));
  }
});

test.use({ timezoneId: "America/New_York" });

// A tab of this browser, signed in, with the organization's records open.
async function signedIn(context) {
  const page = await context.newPage();
  await page.goto("/web/index.html");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await page.click("#sign-in-button");
  await expect(page.locator(".chrona-shell")).toBeVisible({ timeout: 20_000 });
  return page;
}

// Reference data, added through More (each addition is one commit).
async function references(page) {
  await page.click(".chrona-nav__link:has-text('More')");
  await page.fill("#new-project-name", "HelixNote");
  await page.click("#add-project");
  await expect(page.locator("#projects .ef-checkbox__label")).toHaveText(["HelixNote"]);
  await page.fill("#new-activity-type-name", "Research");
  await page.click("#add-activity-type");
  await expect(page.locator("#activity-types .ef-checkbox__label")).toHaveText(["Research"]);
}

// Records today's time on Track, from `start` to `end`.
async function record(page, start, end, description) {
  await page.click(".chrona-nav__link:has-text('Track')");
  await page.selectOption("#manual-activity-type", { label: "Research" });
  await page.selectOption("#manual-project", { label: "HelixNote" });
  const today = await page.evaluate(() => new Date().toLocaleDateString("en-CA"));
  await page.fill("#manual-start-date", today);
  await page.fill("#manual-start-time", start);
  await page.fill("#manual-end-time", end);
  await page.fill("#manual-description", description);
  await page.fill("#manual-purpose", "Ship the slice");
  await page.click("#save-manual");
  await expect(page.locator("p[role=status][aria-live=polite]")).toHaveText(/^Saved /);
}

// The stored activity records that hold this description.
const stored = (github, description) =>
  Object.entries(headFiles(github.current())).filter(([path, content]) => path.includes("/records/chrona.activity/") && content.includes(`"${description}"`));

const kept = (page) =>
  page.evaluate((key) => {
    const text = localStorage.getItem(key);
    return text === null ? [] : JSON.parse(text).entries.filter((entry) => !["synchronized", "abandoned"].includes(String(entry.state?.kind ?? entry.state).toLowerCase()));
  }, QUEUE);

const headline = (page) => page.locator(".chrona-sidebar__status strong");

// The browser loses, or finds, its network: GitHub with it.
async function online(context, github, flag) {
  github.reachable(flag);
  await context.setOffline(!flag);
}

test("a signed-in person's organization is set up on GitHub and what they record is stored there", async ({ context, github }) => {
  const page = await signedIn(context);
  await expect(headline(page)).toHaveText("All changes saved");
  // The organization was founded by its listed administrator, in Chrona's folder.
  expect(Object.keys(headFiles(github.current())).some((path) => path.startsWith("deployments/test/chrona/"))).toBe(true);

  await references(page);
  await expect(headline(page)).toHaveText("All changes saved");
  const before = history(github.current()).length;
  await record(page, "08:10", "09:02", "Visual engineering research");
  await expect(headline(page)).toHaveText("All changes saved");
  expect(history(github.current()).length).toBe(before + 1);
  expect(stored(github, "Visual engineering research")).toHaveLength(1);

  // Another tab reads it back from GitHub.
  const other = await signedIn(context);
  await other.click(".chrona-nav__link:has-text('Today')");
  await expect(other.locator("#day-records .chrona-record__title")).toHaveText(["Visual engineering research"]);
});

// ---- two tabs, one queue of unsent changes (WI-0067) ------------------------

// Two signed-in tabs of one browser, the organization set up with its
// reference data; the first tab holds the queue.
async function twoTabs(context) {
  const first = await signedIn(context);
  await references(first);
  await expect(headline(first)).toHaveText("All changes saved");
  const second = await signedIn(context);
  await expect(second.locator("#queue-elsewhere")).toBeVisible();
  await expect(first.locator("#queue-elsewhere")).toHaveCount(0);
  return { first, second };
}

test("with two tabs open, only the tab holding the unsent changes keeps them, and each change is sent once", async ({ context, github }) => {
  const { first, second } = await twoTabs(context);
  await second.click(".chrona-nav__link:has-text('More')");
  await expect(second.locator("#queue-mode")).toHaveText("Held by another Chrona tab");
  await first.click(".chrona-nav__link:has-text('More')");
  await expect(first.locator("#queue-mode")).toHaveText("Kept in this browser by this tab");
  const before = history(github.current()).length;

  await online(context, github, false);
  await record(first, "09:00", "10:00", "Pairing");
  await record(second, "11:00", "12:00", "Review");
  await expect(headline(first)).toHaveText("Offline: 1 change waits to be sent");
  await expect(headline(second)).toHaveText("Offline: 1 change waits to be sent");
  // Only the holder's change is kept in the browser.
  const queued = await kept(first);
  expect(queued).toHaveLength(1);
  expect(JSON.stringify(queued)).toContain("Pairing");
  expect(history(github.current()).length).toBe(before);

  // Back online, each tab sends its own change, once.
  await online(context, github, true);
  await expect(headline(first)).toHaveText("All changes saved", { timeout: 20_000 });
  await expect(headline(second)).toHaveText("All changes saved", { timeout: 20_000 });
  expect(history(github.current()).length).toBe(before + 2);
  expect(stored(github, "Pairing")).toHaveLength(1);
  expect(stored(github, "Review")).toHaveLength(1);
  expect(await kept(first)).toEqual([]);
});

test("when the holding tab closes, the tab waiting to take over sends both tabs' changes, each once", async ({ context, github }) => {
  const { first, second } = await twoTabs(context);
  const before = history(github.current()).length;

  await online(context, github, false);
  await record(first, "09:00", "10:00", "Pairing");
  await record(second, "11:00", "12:00", "Review");
  await second.click("#take-over-queue");
  await expect(second.locator("#queue-waiting")).toBeVisible();

  // The first tab closes with its change unsent: the browser passes the
  // lock to the second, which takes the change over.
  await first.close();
  await expect(second.locator("#queue-elsewhere")).toHaveCount(0);
  await expect(headline(second)).toHaveText("Offline: 2 changes wait to be sent");
  expect(await kept(second)).toHaveLength(2);

  await online(context, github, true);
  await expect(headline(second)).toHaveText("All changes saved", { timeout: 20_000 });
  expect(history(github.current()).length).toBe(before + 2);
  expect(stored(github, "Pairing")).toHaveLength(1);
  expect(stored(github, "Review")).toHaveLength(1);
  expect(await kept(second)).toEqual([]);
  await second.click(".chrona-nav__link:has-text('Today')");
  await expect(second.locator("#day-records .chrona-record__title")).toHaveText(["Pairing", "Review"]);
});

// ---- deep links to stored records (CHX-460, WI-0071) -------------------------

test("an activity's link opens it cold in a new tab, through sign-in, with its month read from GitHub", async ({ context, github }) => {
  const page = await signedIn(context);
  await references(page);
  await record(page, "08:10", "09:02", "Visual engineering research");
  await expect(headline(page)).toHaveText("All changes saved");
  await page.click(".chrona-nav__link:has-text('Today')");
  const href = await page.locator("#day-records .chrona-record__title a").getAttribute("href");
  expect(href).toMatch(/^#\/entries\/[^?]+\?on=\d{4}-\d{2}-\d{2}$/);
  expect(stored(github, "Visual engineering research")).toHaveLength(1);

  // A new tab: no session yet, so sign-in first, keeping the target.
  const other = await context.newPage();
  await other.goto(`/web/index.html${href}`);
  await expect(other.locator("#sign-in")).toBeVisible();
  await expect(other).toHaveURL(/#\/sign-in\?returnTo=%2Fentries%2F/);
  await other.click("#sign-in-button");
  await expect(other.locator(".chrona-shell")).toBeVisible({ timeout: 20_000 });
  await expect(other).toHaveURL(`${PAGE}${href}`);
  await expect(other.locator("#activity-title")).toHaveText("Visual engineering research");

  // An activity the records do not hold is the not-found page, once they are read.
  const missing = await context.newPage();
  await missing.goto(`/web/index.html#/entries/ACT-none?on=${href.slice(-10)}`);
  await missing.click("#sign-in-button");
  await expect(missing.locator(".chrona-shell")).toBeVisible({ timeout: 20_000 });
  await expect(missing.locator("#problem-title")).toHaveText("Not found");
});

test("someone who is not an administrator is refused the administrators' parts of More", async ({ context, github }) => {
  // The administrator adds a member who records only their own time.
  const page = await signedIn(context);
  await page.click(".chrona-nav__link:has-text('More')");
  await page.fill("#member-id", "42");
  await page.fill("#member-name", "Hubot");
  await page.selectOption("#member-access", "ownTime");
  await page.click("#admit");
  await expect(page.locator("#people")).toContainText("Hubot");
  await expect(headline(page)).toHaveText("All changes saved");

  // That member opens the people page from a link.
  signInAs("42", "hubot");
  const member = await context.newPage();
  await member.goto("/web/index.html#/more/people");
  await member.click("#sign-in-button");
  await expect(member.locator(".chrona-shell")).toBeVisible({ timeout: 20_000 });
  await expect(member).toHaveURL(`${PAGE}#/more/people`);
  await expect(member.locator("#problem-title")).toHaveText("Not permitted");
  await expect(member.locator("#problem-detail")).toContainText("administrators");
  await expect(member.locator("#screen-more")).toHaveCount(0);

  // The parts of More every member works in open.
  await member.goto("/web/index.html#/more/references");
  await expect(member.locator("#screen-more")).toBeVisible();
  await expect(member.locator("#screen-problem")).toHaveCount(0);
});

// ---- the organization in the address (CHX-460, WI-0071) -----------------------

test.describe("a deployment of two organizations", () => {
  const two = {
    ...configuration,
    organizations: [
      ...configuration.organizations,
      { id: "org_eu", displayName: "Acme Europe", slug: "acme-eu", timeZone: "Europe/Berlin", administrators: ["583231"] }
    ]
  };

  test("a link names its organization, and opens it cold through sign-in", async ({ context }) => {
    await fakeDeployment(context, two);
    await serveGitHub(context, repository({ owner: "acme", name: "chrona-data" }));

    // Signing in at home names the first organization in the address.
    const page = await signedIn(context);
    await expect(page).toHaveURL(`${PAGE}#/?org=org_acme`);
    await page.click(".chrona-nav__link:has-text('More')");
    await expect(page).toHaveURL(`${PAGE}#/more?org=org_acme`);

    // A link into the other organization, opened in a new tab.
    const other = await context.newPage();
    await other.goto("/web/index.html#/more?org=org_eu");
    await other.click("#sign-in-button");
    await expect(other.locator(".chrona-shell")).toBeVisible({ timeout: 20_000 });
    await expect(other).toHaveURL(`${PAGE}#/more?org=org_eu`);
    await expect(other.locator("#organization")).toHaveValue("org_eu");

    // An organization the deployment does not serve is not found.
    await other.goto("/web/index.html#/more?org=org_none");
    await expect(other.locator("#problem-title")).toHaveText("Not found");
  });
});
