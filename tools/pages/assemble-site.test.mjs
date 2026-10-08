// node --test tools/pages/ : the page transforms of the Pages assembly (WI-0066).
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { publishPage, rootPage, forwardScript } from "./assemble-site.mjs";

const site = JSON.parse(readFileSync(new URL("../../deploy/pages/site.json", import.meta.url), "utf8"));
const page = readFileSync(new URL("../../web/index.html", import.meta.url), "utf8");
const deployment = JSON.parse(readFileSync(new URL(`../../${site.deployment}`, import.meta.url), "utf8"));

test("the policy comes first in <head>, before anything it governs", () => {
  const published = publishPage(page, site);
  const policy = published.indexOf('http-equiv="Content-Security-Policy"');
  assert.ok(policy > 0);
  assert.ok(policy < published.indexOf("<link"));
  assert.ok(policy < published.indexOf("<script"));
});

test("the policy allows WebAssembly and no inline script", () => {
  assert.match(site.contentSecurityPolicy, /script-src 'self' 'wasm-unsafe-eval'(;|$)/);
  assert.doesNotMatch(site.contentSecurityPolicy, /'unsafe-inline'|'unsafe-eval'/);
});

test("the published page has no inline script", () => {
  const scripts = [...publishPage(page, site).matchAll(/<script\b[^>]*>/g)].map((m) => m[0]);
  assert.ok(scripts.length > 0);
  scripts.forEach((tag) => assert.match(tag, /\bsrc=/));
});

test("the banner is the first thing in <body>, with its stylesheet", () => {
  const published = publishPage(page, site);
  assert.match(published, /<body[^>]*>\n<p class="pages-demo-banner" role="note" data-pages-banner>Demo/);
  assert.match(published, /<link rel="stylesheet" href="\.\/pages\.css" \/>/);
});

test("no banner is added when the site names none", () => {
  const published = publishPage(page, { ...site, banner: undefined });
  assert.doesNotMatch(published, /pages-demo-banner|pages\.css/);
});

test("a page that already carries a policy keeps its own", () => {
  const own = page.replace(/<meta charset="utf-8" \/>/, '$&\n<meta http-equiv="Content-Security-Policy" content="default-src \'none\'" />');
  const published = publishPage(own, site);
  assert.equal([...published.matchAll(/http-equiv="Content-Security-Policy"/g)].length, 1);
  assert.match(published, /default-src 'none'/);
});

test("the root page sends the browser to web/ by a relative address, keeping the fragment", () => {
  const root = rootPage(site);
  // A file, never inline script: the policy allows only 'self'.
  const scripts = [...root.matchAll(/<script\b[^>]*>/g)].map((m) => m[0]);
  assert.deepEqual(scripts, ['<script src="forward.js">']);
  assert.match(forwardScript, /location\.replace\("web\/" \+ location\.hash\)/);
  // Without script, a refresh still reaches the application.
  assert.match(root, /<noscript><meta http-equiv="refresh" content="0; url=web\/" \/><\/noscript>/);
  // The script comes after the policy that governs it.
  assert.ok(root.indexOf("Content-Security-Policy") < root.indexOf("<script"));
});

test("the Pages deployment is a local session: no sign-in, no data location", () => {
  assert.equal(deployment.environment, "local");
  assert.equal(deployment.identity, undefined);
  assert.equal(deployment.location, undefined);
});
