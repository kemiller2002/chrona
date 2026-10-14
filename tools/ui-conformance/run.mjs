// `npm run ui:conformance` (GH-112): every project in playwright.config.js
// (the browser suite, whose tests the contract cites, and the conformance
// specs), then the evidence document built from that one run. The evidence
// is written even when a test fails, so a failure's evidence says FAIL; the
// exit status fails if either the run or the evidence does.
import { spawnSync } from "node:child_process";

const results = "test-results/ui-conformance/results.json";
const evidence = "test-results/ui-conformance/chrona.ui-conformance.json";

const run = (command, args, env = {}) =>
  spawnSync(command, args, { stdio: "inherit", env: { ...process.env, ...env } }).status ?? 1;

const tests = run("npx", ["playwright", "test", "--reporter=list,json", ...process.argv.slice(2)], { PLAYWRIGHT_JSON_OUTPUT_NAME: results });
const built = run(process.execPath, ["tools/ui-conformance/evidence.mjs", results, evidence]);

process.exit(tests !== 0 ? tests : built);
