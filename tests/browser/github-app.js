// What the GitHub end-to-end suites share (WI-0069): the deployment they open,
// the browser context's error and token guard, the fake GitHub, and how a
// tab signs in, records time and reads what this browser keeps.
import { test as base, expect } from "@playwright/test";
import { fakeDeployment, PAGE, signInAs, afternoon, ACCESS_TOKEN, REFRESH_TOKEN } from "./support.js";
import { serveGitHub, repository, headFiles, history } from "./github-fake.js";

export { expect };

export const configuration = {
  environment: "test",
  environmentName: "test",
  location: { owner: "acme", repository: "chrona-data", branch: "main", basePath: "deployments/test" },
  identity: { exchange: "https://fides.test", application: "chrona-test", provider: "github", clientId: "Iv23liBROWSER", redirectUri: PAGE },
  organizations: [{ id: "org_acme", displayName: "Acme Consulting", slug: "acme", timeZone: "America/New_York", administrators: ["583231"] }]
};

export const QUEUE = "arca.queue.chrona.org_acme";

export const test = base.extend({
  // Every page of the context fails the test on an error it reports, except
  // the browser's own note of a request that failed while offline.
  context: async ({ context }, use) => {
    // Time recorded "today" at fixed hours is in the past whenever this runs.
    await context.clock.setFixedTime(afternoon());
    const problems = [];
    // Everything any page wrote to its console, of every kind (42, WI-0040).
    const written = [];
    context.on("page", (page) => {
      page.on("console", (message) => {
        written.push(message.text());
        if (message.type() === "error" && !message.text().startsWith("Failed to load resource")) problems.push(message.text());
      });
      page.on("pageerror", (error) => problems.push(error.message));
    });
    await use(context);
    expect(problems, "a page reported errors").toEqual([]);
    // Diagnostics carry ids and measures only: no page ever wrote the GitHub
    // token or the refresh token to its console.
    expect(written.filter((text) => text.includes(ACCESS_TOKEN) || text.includes(REFRESH_TOKEN)), "a page logged a token").toEqual([]);
  },
  github: async ({ context }, use) => {
    await fakeDeployment(context, configuration);
    await use(await serveGitHub(context, repository({ owner: "acme", name: "chrona-data" })));
  }
});


// A tab of this browser, signed in, with the organization's records open.
export async function signedIn(context) {
  const page = await context.newPage();
  await page.goto("/web/index.html");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await page.click("#sign-in-button");
  await expect(page.locator(".chrona-shell")).toBeVisible({ timeout: 20_000 });
  return page;
}

// Reference data, added through More (each addition is one commit).
// On a phone, More is in the tab bar.
export async function references(page, navigation = ".chrona-nav__link") {
  await page.click(`${navigation}:has-text('More')`);
  await page.fill("#new-project-name", "HelixNote");
  await page.click("#add-project");
  await expect(page.locator("#projects .ef-checkbox__label")).toHaveText(["HelixNote"]);
  await page.fill("#new-activity-type-name", "Research");
  await page.click("#add-activity-type");
  await expect(page.locator("#activity-types .ef-checkbox__label")).toHaveText(["Research"]);
}

// Records today's time on Track, from `start` to `end`.
export async function record(page, start, end, description) {
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
export const stored = (github, description) =>
  Object.entries(headFiles(github.current())).filter(([path, content]) => path.includes("/records/chrona.activity/") && content.includes(`"${description}"`));

// The unsent entries of a serialized queue.
export const outstanding = (text) =>
  JSON.parse(text).entries.filter((entry) => !["synchronized", "abandoned"].includes(String(entry.state?.kind ?? entry.state).toLowerCase()));

// The unsent changes this browser keeps in localStorage (an older Chrona's
// queue, or where IndexedDB cannot be used).
export const legacy = (page) => page.evaluate((key) => localStorage.getItem(key), QUEUE).then((text) => (text === null ? [] : outstanding(text)));

// The unsent changes this browser keeps in IndexedDB: Arca's queue record,
// in Chrona's namespace of Limen's store pack.
export const inIndexedDb = (page) =>
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
export const kept = async (page) => [...(await inIndexedDb(page)), ...(await legacy(page))];

// The person comes back to a tab (Limen's lifecycle pack hears it).
export const returnTo = (page) => page.evaluate(() => document.dispatchEvent(new Event("visibilitychange")));

export const headline = (page) => page.locator(".chrona-sidebar__status strong");

// The browser loses, or finds, its network: GitHub with it.
export async function online(context, github, flag) {
  github.reachable(flag);
  await context.setOffline(!flag);
}
