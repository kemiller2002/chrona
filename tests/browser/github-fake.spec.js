// The GitHub fake's pure core (WI-0069): respond(state, request) -> { state,
// response }, checked directly, with no browser.
import { test, expect } from "@playwright/test";
import { respond, repository, blobSha, headFiles, history } from "./github-fake.js";

const API = "https://api.github.com/repos/acme/data";

// Freezes a state all the way down, so any change to it throws.
const frozen = (value) => {
  if (value !== null && typeof value === "object") {
    Object.values(value).forEach(frozen);
    Object.freeze(value);
  }
  return value;
};

const call = (state, method, path, body) => {
  const { state: next, response } = respond(state, { method, url: `${API}${path}`, body: body === undefined ? "" : JSON.stringify(body) });
  return { state: next, status: response.status, body: JSON.parse(response.body) };
};

// One commit on the branch head that applies these changes.
const commit = (state, changes, message = "change") => {
  const head = call(state, "GET", "/git/ref/heads/main").body.object.sha;
  const tree = call(state, "GET", `/git/commits/${head}`).body.tree.sha;
  const made = call(state, "POST", "/git/trees", { base_tree: tree, tree: changes });
  const committed = call(made.state, "POST", "/git/commits", { message, tree: made.body.sha, parents: [head] });
  const published = call(committed.state, "PATCH", "/git/refs/heads/main", { sha: committed.body.sha, force: false });
  return { ...published, sha: committed.body.sha, unpublished: committed.state };
};

const start = () => frozen(repository({ owner: "acme", name: "data", files: { "a/one.json": "1\n" } }));

test("a file's id is its git blob SHA-1, as Arca computes it", () => {
  expect(blobSha("hello\n")).toBe("ce013625030ba8dba906f756967f9e9ca394464a");
});

test("the contents API answers a file, a folder or nothing, at the head or a commit", () => {
  const state = start();
  const file = call(state, "GET", "/contents/a/one.json?ref=main").body;
  expect(file).toMatchObject({ type: "file", sha: blobSha("1\n"), size: 2, encoding: "base64" });
  expect(Buffer.from(file.content, "base64").toString("utf8")).toBe("1\n");
  expect(call(state, "GET", "/contents/a?ref=main").body).toEqual([{ name: "one.json", type: "file", sha: blobSha("1\n"), size: 2, path: "a/one.json" }]);
  expect(call(state, "GET", "/contents/b?ref=main").status).toBe(404);
});

test("a commit is a tree, a commit and a fast-forward ref update; the state given is never changed", () => {
  const state = start();
  const { state: next, status } = commit(state, [{ path: "a/two.json", mode: "100644", type: "blob", content: "2\n" }, { path: "a/one.json", mode: "100644", type: "blob", sha: null }]);
  expect(status).toBe(200);
  expect(headFiles(next)).toEqual({ "a/two.json": "2\n" });
  expect(headFiles(state)).toEqual({ "a/one.json": "1\n" });
  expect(history(next)).toHaveLength(2);
});

test("a ref update that is not a fast forward is refused, as GitHub refuses it", () => {
  const state = start();
  const first = commit(state, [{ path: "x", content: "x" }]);
  // A commit built on the old head, published after the first one landed.
  const late = commit(state, [{ path: "y", content: "y" }]);
  const raced = call({ ...first.state, commits: { ...first.state.commits, ...late.unpublished.commits }, trees: { ...first.state.trees, ...late.unpublished.trees } }, "PATCH", "/git/refs/heads/main", { sha: late.sha, force: false });
  expect(raced.status).toBe(422);
  expect(raced.body.message).toContain("fast forward");
});

test("history lists the commits that touched a path, and compare says how two commits relate", () => {
  const state = start();
  const one = commit(state, [{ path: "a/one.json", content: "one\n" }], "edit one");
  const two = commit(one.state, [{ path: "b.json", content: "b\n" }], "add b");
  expect(call(two.state, "GET", "/commits?sha=main&path=a/one.json&per_page=100").body.map((c) => c.commit.message)).toEqual(["edit one", "Initial commit"]);
  expect(call(two.state, "GET", "/commits?sha=main&per_page=100").body).toHaveLength(3);
  expect(call(two.state, "GET", `/compare/${one.sha}...${two.sha}`).body.status).toBe("ahead");
  expect(call(two.state, "GET", `/compare/${two.sha}...${two.sha}`).body.status).toBe("identical");
  expect(call(two.state, "GET", `/compare/${two.sha}...${one.sha}`).body.status).toBe("behind");
});
