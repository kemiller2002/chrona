// node --test tools/routes/inventory.test.mjs : the route inventory (CHX-460) is a valid
// echelon.routes/v1 document under the schema Limen 0.9.0 publishes
// (contract/routes.schema.json in @echelon-foundry/limen). That it is the
// route table's own rendering, byte for byte, is PlacesTests' check.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Ajv2020 from "ajv/dist/2020.js";

const read = (path) => JSON.parse(readFileSync(new URL(path, import.meta.url), "utf8"));
const schema = read("../../node_modules/@echelon-foundry/limen/contract/routes.schema.json");
const inventory = read("../../.echelon/routes.json");

const validate = new Ajv2020({ allErrors: true, strict: false }).compile(schema);

test("the route inventory is a valid echelon.routes/v1 document", () => {
  const valid = validate(inventory);
  assert.ok(valid, JSON.stringify(validate.errors, null, 2));
});

test("the schema refuses what an inventory must not be", () => {
  // Proves the validator is live: a document missing its routes, or naming
  // an unknown mode, is refused.
  const { routes, ...withoutRoutes } = inventory;
  assert.equal(validate(withoutRoutes), false);
  assert.equal(validate({ ...inventory, mode: "query" }), false);
  assert.equal(validate({ ...inventory, schema: "echelon.routes/v2" }), false);
});
