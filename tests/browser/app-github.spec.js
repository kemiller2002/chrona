// Chrona against GitHub, end to end in Chromium (WI-0069): records stored
// through Arca's GitHub provider in the WASM engine, over the browser's own
// fetch, against the fake GitHub API in ./github-fake.js. Sign-in is Fides'
// fake (./support.js). Then two tabs of one browser and the queue of unsent
// changes they share (WI-0067, WI-0059): only the tab that holds it keeps it,
// in IndexedDB, and sends it; "use this tab instead" takes it over at once;
// the other tab holds it when the holder closes; nothing is sent twice. And
// the move from the localStorage queue an older Chrona kept, including one
// interrupted after the copy.
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

// The unsent entries of a serialized queue.
const outstanding = (text) =>
  JSON.parse(text).entries.filter((entry) => !["synchronized", "abandoned"].includes(String(entry.state?.kind ?? entry.state).toLowerCase()));

// The unsent changes this browser keeps in localStorage (an older Chrona's
// queue, or where IndexedDB cannot be used).
const legacy = (page) => page.evaluate((key) => localStorage.getItem(key), QUEUE).then((text) => (text === null ? [] : outstanding(text)));

// The unsent changes this browser keeps in IndexedDB: Arca's queue record,
// in Chrona's namespace of Limen's store pack.
const inIndexedDb = (page) =>
  page
    .evaluate(async () => {
      const read = (request) => new Promise((resolve, reject) => ((request.onsuccess = () => resolve(request.result)), (request.onerror = () => reject(request.error))));
      const texts = [];
      for (const { name } of await indexedDB.databases()) {
        if (!name.startsWith("chrona/")) continue;
        const database = await read(indexedDB.open(name));
        for (const store of database.objectStoreNames) {
          for (const value of await read(database.transaction(store).objectStore(store).getAll())) {
            const record = typeof value === "string" ? JSON.parse(value) : value;
            if (record && typeof record.queue === "string") texts.push(record.queue);
          }
        }
        database.close();
      }
      return texts;
    })
    .then((texts) => texts.flatMap(outstanding));

// Every unsent change this browser keeps.
const kept = async (page) => [...(await inIndexedDb(page)), ...(await legacy(page))];

// The person comes back to a tab (Limen's lifecycle pack hears it).
const returnTo = (page) => page.evaluate(() => document.dispatchEvent(new Event("visibilitychange")));

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

test("with two tabs open, only the tab holding the unsent changes keeps them, in IndexedDB, and each change is sent once", async ({ context, github }) => {
  const { first, second } = await twoTabs(context);
  await second.click(".chrona-nav__link:has-text('More')");
  await expect(second.locator("#queue-mode")).toHaveText("Held by another Chrona tab");
  await first.click(".chrona-nav__link:has-text('More')");
  await expect(first.locator("#queue-mode")).toHaveText("Kept in this browser (IndexedDB) by this tab");
  const before = history(github.current()).length;

  await online(context, github, false);
  await record(first, "09:00", "10:00", "Pairing");
  await record(second, "11:00", "12:00", "Review");
  await expect(headline(first)).toHaveText("Offline: 1 change waits to be sent");
  await expect(headline(second)).toHaveText("Offline: 1 change waits to be sent");
  // Only the holder's change is kept in the browser, and in IndexedDB.
  const queued = await inIndexedDb(first);
  expect(queued).toHaveLength(1);
  expect(JSON.stringify(queued)).toContain("Pairing");
  // Read from GitHub, so conditioned on the repository state it was read at.
  expect(queued[0].operation.expectedChangeToken).toEqual(expect.any(String));
  expect(await legacy(first)).toEqual([]);
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

test("\"use this tab instead\" takes the unsent changes over at once; the other tab says so, and every change is sent once", async ({ context, github }) => {
  const { first, second } = await twoTabs(context);
  const before = history(github.current()).length;

  await online(context, github, false);
  await record(first, "09:00", "10:00", "Pairing");
  await record(second, "11:00", "12:00", "Review");
  await second.click("#take-over-queue");

  // The second tab holds both now; the first hears it lost them.
  await expect(second.locator("#queue-elsewhere")).toBeHidden();
  await expect(headline(second)).toHaveText("Offline: 2 changes wait to be sent");
  await expect(first.locator("#queue-elsewhere")).toBeVisible();
  expect(await inIndexedDb(second)).toHaveLength(2);

  // The first tab goes on, its new change in its page only.
  await record(first, "13:00", "14:00", "Planning");
  await expect(headline(first)).toHaveText("Offline: 1 change waits to be sent");
  expect(await inIndexedDb(second)).toHaveLength(2);

  await online(context, github, true);
  await expect(headline(second)).toHaveText("All changes saved", { timeout: 20_000 });
  await expect(headline(first)).toHaveText("All changes saved", { timeout: 20_000 });
  expect(history(github.current()).length).toBe(before + 3);
  for (const description of ["Pairing", "Review", "Planning"]) expect(stored(github, description)).toHaveLength(1);
  expect(await kept(second)).toEqual([]);
});

test("when the holding tab closes, the other tab holds both tabs' changes once the person returns to it, and sends each once", async ({ context, github }) => {
  const { first, second } = await twoTabs(context);
  const before = history(github.current()).length;

  await online(context, github, false);
  await record(first, "09:00", "10:00", "Pairing");
  await record(second, "11:00", "12:00", "Review");

  // The first tab closes with its change unsent; the person returns to the
  // second, which asks for the queue again and takes the change over.
  await first.close();
  // The browser lets go of the closed tab's lock once the tab is gone.
  await expect.poll(() => second.evaluate(async () => (await navigator.locks.query()).held.filter((lock) => lock.name.startsWith("arca.queue")).length)).toBe(0);
  await returnTo(second);
  await expect(second.locator("#queue-elsewhere")).toBeHidden();
  await expect(headline(second)).toHaveText("Offline: 2 changes wait to be sent");
  expect(await inIndexedDb(second)).toHaveLength(2);

  await online(context, github, true);
  await expect(headline(second)).toHaveText("All changes saved", { timeout: 20_000 });
  expect(history(github.current()).length).toBe(before + 2);
  expect(stored(github, "Pairing")).toHaveLength(1);
  expect(stored(github, "Review")).toHaveLength(1);
  expect(await kept(second)).toEqual([]);
  await second.click(".chrona-nav__link:has-text('Today')");
  await expect(second.locator("#day-records .chrona-record__title")).toHaveText(["Pairing", "Review"]);
});

// ---- the move from localStorage to IndexedDB (WI-0059) ----------------------

// A page as an older Chrona ran: no IndexedDB, so Arca keeps the queue in
// localStorage, under the key earlier releases used.
async function withoutIndexedDb(context) {
  const page = await context.newPage();
  await page.addInitScript(() => Object.defineProperty(window, "indexedDB", { value: undefined, configurable: true }));
  await page.goto("/web/index.html");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await page.click("#sign-in-button");
  await expect(page.locator(".chrona-shell")).toBeVisible({ timeout: 20_000 });
  return page;
}

// A tab signed in while GitHub cannot be reached: the records do not open.
async function unopened(context) {
  const page = await context.newPage();
  await page.goto("/web/index.html");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await page.click("#sign-in-button");
  await expect(page.locator("#store-failed")).toBeVisible({ timeout: 20_000 });
  return page;
}

// One change recorded offline where an older Chrona kept it: localStorage.
async function olderQueue(context, github) {
  const older = await withoutIndexedDb(context);
  await references(older);
  await expect(headline(older)).toHaveText("All changes saved");
  await older.click(".chrona-nav__link:has-text('More')");
  await expect(older.locator("#queue-mode")).toHaveText("Kept in this browser (localStorage) by this tab");
  await online(context, github, false);
  await record(older, "09:00", "10:00", "Pairing");
  await expect(headline(older)).toHaveText("Offline: 1 change waits to be sent");
  const text = await older.evaluate((key) => localStorage.getItem(key), QUEUE);
  expect(outstanding(text)).toHaveLength(1);
  await older.close();
  return text;
}

test("unsent changes an older Chrona kept in localStorage move to IndexedDB on first load, unchanged, are said, and are sent once", async ({ context, github }) => {
  const text = await olderQueue(context, github);
  const before = history(github.current()).length;

  // The page loads again with GitHub still out of reach: the records cannot
  // open, but the queue is read, moved and counted, and the move is said.
  await context.setOffline(false);
  const page = await unopened(context);
  await expect(page.locator("#offline-waiting")).toHaveText("1 change waits in this browser and is sent when your records open.");
  await expect(page.locator("#store-failed-queue-notice")).toHaveText("1 unsent change kept in this browser's older storage moved to IndexedDB, unchanged.");
  expect(await legacy(page)).toEqual([]);
  expect((await inIndexedDb(page)).map((entry) => JSON.stringify(entry))).toEqual(outstanding(text).map((entry) => JSON.stringify(entry)));
  await page.close();

  // With GitHub back, the records open and the moved change is sent once.
  github.reachable(true);
  const back = await signedIn(context);
  await expect(headline(back)).toHaveText("All changes saved", { timeout: 20_000 });
  expect(history(github.current()).length).toBe(before + 1);
  expect(stored(github, "Pairing")).toHaveLength(1);
  expect(await kept(back)).toEqual([]);
});

test("a move to IndexedDB interrupted after the copy, before localStorage was cleared, finishes on the next load and sends nothing twice", async ({ context, github }) => {
  const text = await olderQueue(context, github);
  const before = history(github.current()).length;

  // The copy is made and verified (GitHub out of reach, so nothing is
  // sent); the old queue is then put back, as if the page closed before
  // removing it.
  await context.setOffline(false);
  const interrupted = await unopened(context);
  await expect.poll(() => inIndexedDb(interrupted)).toHaveLength(1);
  await interrupted.evaluate(([key, value]) => localStorage.setItem(key, value), [QUEUE, text]);
  expect(await legacy(interrupted)).toHaveLength(1);
  await interrupted.close();

  github.reachable(true);
  const page = await signedIn(context);
  await expect(headline(page)).toHaveText("All changes saved", { timeout: 20_000 });
  expect(history(github.current()).length).toBe(before + 1);
  expect(stored(github, "Pairing")).toHaveLength(1);
  expect(await kept(page)).toEqual([]);
});

test("an older localStorage queue that cannot be read is left exactly as it is, and the person is told", async ({ context, github }) => {
  const first = await signedIn(context);
  await first.evaluate((key) => localStorage.setItem(key, "{not a queue"), QUEUE);
  await first.close();

  const page = await signedIn(context);
  await expect(page.locator("#queue-notice")).toContainText("Unsent changes kept in this browser's older storage cannot be read. They were left exactly as they are.");
  expect(await page.evaluate((key) => localStorage.getItem(key), QUEUE)).toBe("{not a queue");
  await expect(headline(page)).toHaveText("All changes saved");
});

// ---- opening offline from the read cache (WI-0057) --------------------------

test("with GitHub out of reach, a new tab opens the records from this browser's read cache, and a change made there is sent once GitHub is back", async ({ context, github }) => {
  const first = await signedIn(context);
  await references(first);
  await record(first, "08:10", "09:02", "Visual engineering research");
  await expect(headline(first)).toHaveText("All changes saved");
  const before = history(github.current()).length;
  await first.close();

  // GitHub cannot be reached; the page itself still loads.
  github.reachable(false);
  const page = await context.newPage();
  await page.goto("/web/index.html");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await page.click("#sign-in-button");
  await expect(page.locator(".chrona-shell")).toBeVisible({ timeout: 20_000 });
  await expect(headline(page)).toHaveText(/^Offline: your records as of /);
  await page.click(".chrona-nav__link:has-text('Today')");
  await expect(page.locator("#day-records .chrona-record__title")).toHaveText(["Visual engineering research"]);

  // A change made from the cache waits, conditioned on nothing cached.
  await record(page, "09:18", "11:04", "HelixNote development");
  expect(history(github.current()).length).toBe(before);
  const waiting = await inIndexedDb(page);
  expect(waiting).toHaveLength(1);
  expect(waiting[0].operation.expectedChangeToken ?? null).toBeNull();

  // GitHub back: the records are read from it, the change decided on them
  // and sent once.
  github.reachable(true);
  await expect(headline(page)).toHaveText("All changes saved", { timeout: 30_000 });
  expect(history(github.current()).length).toBe(before + 1);
  expect(stored(github, "HelixNote development")).toHaveLength(1);
  expect(await kept(page)).toEqual([]);
});

test("signing out clears this account's read cache, so the next start with GitHub out of reach cannot open the records", async ({ context, github }) => {
  const first = await signedIn(context);
  await references(first);
  await expect(headline(first)).toHaveText("All changes saved");
  await first.click(".chrona-nav__link:has-text('More')");
  await first.click("#sign-out");
  await expect(first.locator("#sign-in")).toBeVisible();
  await first.close();

  github.reachable(false);
  const page = await unopened(context);
  await expect(page.locator("#store-failure")).toHaveText("GitHub could not be reached.");
});

test("clearing this device removes what it keeps to open offline, then signs out", async ({ context, github }) => {
  const first = await signedIn(context);
  await references(first);
  await expect(headline(first)).toHaveText("All changes saved");
  await first.click(".chrona-nav__link:has-text('More')");
  await first.click("#clear-device");
  await expect(first.locator("#clear-device-confirm")).toBeVisible();
  await first.click("#clear-device-confirmed");
  await expect(first.locator("#sign-in")).toBeVisible({ timeout: 20_000 });
  const databases = await first.evaluate(async () => (await indexedDB.databases()).map((database) => database.name).filter((name) => name.startsWith("chrona/")));
  expect(databases).toEqual([]);
  await first.close();

  github.reachable(false);
  const page = await unopened(context);
  await expect(page.locator("#store-failure")).toHaveText("GitHub could not be reached.");
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
