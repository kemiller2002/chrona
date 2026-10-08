// A fake of the GitHub REST API that Arca's GitHub provider uses, for the
// browser suite: the identity and repository checks, the branch ref, the
// contents API (a file or a folder at a commit), commit history and compare,
// and the Git Data API that writes one commit per operation (trees, commits,
// a fast-forward ref update).
//
// The core is pure: `respond(state, request)` returns the next state and the
// response, and changes nothing. A state is a plain value: the repository's
// commits, trees, the branch head and who is signed in. Every id is derived
// from content (a file's id is its git blob SHA-1, as Arca computes it, so its
// revisions agree). The only I/O is at the edge: `serveGitHub` routes the
// browser's requests to `respond` and keeps the one mutable cell, so every
// tab of a browser context shares one repository.
import { createHash } from "node:crypto";

const API = "https://api.github.com";

const sha1 = (text) => createHash("sha1").update(text).digest("hex");

// A file's id, as git and Arca compute it.
export const blobSha = (content) => {
  const bytes = Buffer.from(content, "utf8");
  return createHash("sha1").update(Buffer.concat([Buffer.from(`blob ${bytes.length}\0`, "utf8"), bytes])).digest("hex");
};

const sorted = (files) => Object.fromEntries(Object.entries(files).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)));
const treeSha = (files) => sha1(`tree ${JSON.stringify(sorted(files))}`);
const commitSha = (commit) => sha1(`commit ${JSON.stringify(commit)}`);

// A repository whose branch holds one commit with these files.
export function repository({ owner, name, branch = "main", visibility = "private", files = { "README.md": "Chrona data\n" }, user = { id: 583231, login: "octocat" } }) {
  const tree = treeSha(files);
  const root = { tree, parents: [], message: "Initial commit" };
  const head = commitSha(root);
  return {
    owner,
    name,
    branch,
    visibility,
    user,
    trees: { [tree]: sorted(files) },
    commits: { [head]: root },
    head
  };
}

// ---- reading the state ----------------------------------------------------

const filesAt = (state, commit) => state.trees[state.commits[commit].tree];

// A commit id, or the branch's name for its head.
const resolve = (state, reference) => (reference === state.branch ? state.head : state.commits[reference] ? reference : undefined);

// The commits reachable from `from`, newest first (first parents).
const lineage = (state, from) => {
  const found = [];
  for (let at = from; at !== undefined; at = state.commits[at].parents[0]) found.push(at);
  return found;
};

// The commits that changed this file, newest first.
const touching = (state, from, path) =>
  lineage(state, from).filter((sha) => {
    const parent = state.commits[sha].parents[0];
    const before = parent === undefined ? undefined : filesAt(state, parent)[path];
    return filesAt(state, sha)[path] !== before;
  });

// What lies at a path in a commit: a file, a folder's entries, or nothing.
const contentsAt = (files, path) => {
  if (path in files) {
    const content = files[path];
    return { kind: "file", content };
  }
  const prefix = path === "" ? "" : `${path}/`;
  const below = Object.entries(files).filter(([file]) => file.startsWith(prefix));
  if (below.length === 0) return { kind: "missing" };
  const entries = new Map();
  for (const [file, content] of below) {
    const [first, ...rest] = file.slice(prefix.length).split("/");
    if (rest.length === 0) entries.set(first, { name: first, type: "file", sha: blobSha(content), size: Buffer.byteLength(content, "utf8") });
    else if (!entries.has(first)) {
      const folder = Object.fromEntries(below.filter(([f]) => f.startsWith(`${prefix}${first}/`)));
      entries.set(first, { name: first, type: "dir", sha: treeSha(folder), size: 0 });
    }
  }
  return { kind: "folder", entries: [...entries.values()].sort((a, b) => (a.name < b.name ? -1 : 1)) };
};

// ---- responses --------------------------------------------------------------

const json = (status, body) => ({ status, body: JSON.stringify(body) });
const notFound = json(404, { message: "Not Found" });
const commitItem = (state, sha) => ({ sha, commit: { message: state.commits[sha].message } });

// Applies a tree request ({ base_tree, tree: [{ path, content } | { path, sha: null }] }).
const applyTree = (state, request) => {
  const base = state.trees[request.base_tree];
  if (base === undefined) return undefined;
  const files = { ...base };
  for (const entry of request.tree) {
    if (entry.sha === null) delete files[entry.path];
    else files[entry.path] = entry.content;
  }
  return sorted(files);
};

/**
 * The fake's whole behaviour: (state, request) -> { state, response }.
 * `request` is { method, url, body }; `response` is { status, body }.
 */
export function respond(state, request) {
  const url = new URL(request.url);
  const path = url.pathname;
  const unchanged = (response) => ({ state, response });

  if (path === "/user" && request.method === "GET") return unchanged(json(200, { id: state.user.id, login: state.user.login, type: "User" }));

  const repo = `/repos/${state.owner}/${state.name}`;
  if (!path.startsWith(repo)) return unchanged(notFound);
  const rest = path.slice(repo.length);
  const branchRef = `/git/ref/heads/${state.branch}`;

  if (rest === "" && request.method === "GET")
    return unchanged(
      json(200, {
        id: 4242,
        name: state.name,
        owner: { login: state.owner },
        visibility: state.visibility,
        private: state.visibility !== "public",
        archived: false,
        permissions: { pull: true, push: true }
      })
    );
  if (rest === `/branches/${state.branch}` && request.method === "GET") return unchanged(json(200, { name: state.branch, commit: { sha: state.head } }));
  if (rest === `/rules/branches/${state.branch}` && request.method === "GET") return unchanged(json(200, []));
  if (rest === branchRef && request.method === "GET") return unchanged(json(200, { ref: `refs/heads/${state.branch}`, object: { sha: state.head, type: "commit" } }));

  if (rest.startsWith("/contents/") && request.method === "GET") {
    const commit = resolve(state, url.searchParams.get("ref") ?? state.branch);
    if (commit === undefined) return unchanged(notFound);
    const target = rest.slice("/contents/".length).split("/").map(decodeURIComponent).join("/");
    const found = contentsAt(filesAt(state, commit), target);
    if (found.kind === "missing") return unchanged(notFound);
    if (found.kind === "folder") return unchanged(json(200, found.entries.map((entry) => ({ ...entry, path: `${target}/${entry.name}` }))));
    const content = found.content;
    return unchanged(
      json(200, {
        type: "file",
        name: target.split("/").at(-1),
        path: target,
        sha: blobSha(content),
        size: Buffer.byteLength(content, "utf8"),
        encoding: "base64",
        content: Buffer.from(content, "utf8").toString("base64")
      })
    );
  }

  if (rest === "/commits" && request.method === "GET") {
    const from = resolve(state, url.searchParams.get("sha") ?? state.branch);
    if (from === undefined) return unchanged(notFound);
    const file = url.searchParams.get("path");
    const shas = file === null ? lineage(state, from) : touching(state, from, file);
    return unchanged(json(200, shas.slice(0, 100).map((sha) => commitItem(state, sha))));
  }

  if (rest.startsWith("/compare/") && request.method === "GET") {
    const [base, head] = rest.slice("/compare/".length).split("...");
    if (!state.commits[base] || !state.commits[head]) return unchanged(notFound);
    const status = base === head ? "identical" : lineage(state, head).includes(base) ? "ahead" : lineage(state, base).includes(head) ? "behind" : "diverged";
    return unchanged(json(200, { status }));
  }

  if (rest.startsWith("/git/commits/") && request.method === "GET") {
    const sha = rest.slice("/git/commits/".length);
    const commit = state.commits[sha];
    return unchanged(commit === undefined ? notFound : json(200, { sha, tree: { sha: commit.tree }, parents: commit.parents.map((p) => ({ sha: p })), message: commit.message }));
  }

  if (rest === "/git/trees" && request.method === "POST") {
    const files = applyTree(state, JSON.parse(request.body));
    if (files === undefined) return unchanged(json(422, { message: "base_tree is not a tree" }));
    const sha = treeSha(files);
    return { state: { ...state, trees: { ...state.trees, [sha]: files } }, response: json(201, { sha }) };
  }

  if (rest === "/git/commits" && request.method === "POST") {
    const body = JSON.parse(request.body);
    if (!state.trees[body.tree] || !body.parents.every((p) => state.commits[p])) return unchanged(json(422, { message: "Invalid tree or parent" }));
    const commit = { tree: body.tree, parents: body.parents, message: body.message };
    const sha = commitSha(commit);
    return { state: { ...state, commits: { ...state.commits, [sha]: commit } }, response: json(201, { sha }) };
  }

  if (rest === `/git/refs/heads/${state.branch}` && request.method === "PATCH") {
    const { sha } = JSON.parse(request.body);
    if (!state.commits[sha]) return unchanged(json(422, { message: "Object does not exist" }));
    if (!lineage(state, sha).includes(state.head)) return unchanged(json(422, { message: "Update is not a fast forward" }));
    return { state: { ...state, head: sha }, response: json(200, { ref: `refs/heads/${state.branch}`, object: { sha, type: "commit" } }) };
  }

  return unchanged(notFound);
}

// ---- queries for tests ------------------------------------------------------

// The files at the branch head.
export const headFiles = (state) => filesAt(state, state.head);

// The commits on the branch, newest first, with their messages.
export const history = (state) => lineage(state, state.head).map((sha) => ({ sha, message: state.commits[sha].message }));

// ---- the edge -----------------------------------------------------------------

const cors = (origin, request) => ({
  "access-control-allow-origin": origin,
  "access-control-allow-methods": "GET, POST, PATCH, OPTIONS",
  "access-control-allow-headers": request.headers()["access-control-request-headers"] ?? "authorization, content-type, accept",
  "access-control-expose-headers": "etag, x-ratelimit-limit, x-ratelimit-remaining, x-ratelimit-reset, x-ratelimit-resource, retry-after",
  vary: "Origin"
});

/**
 * Serves `initial` to every page of this browser context at api.github.com.
 * Returns the cell's reader, `current()` (the repository now), and
 * `reachable(flag)`: while GitHub cannot be reached, every request fails as
 * it does without a network (a routed request is answered even when the
 * context is offline, so this is the fake's to say).
 */
export async function serveGitHub(context, initial, origin = "http://127.0.0.1:4321") {
  let state = initial;
  let online = true;
  await context.route(`${API}/**`, async (route) => {
    const request = route.request();
    if (!online) return route.abort("internetdisconnected");
    if (request.method() === "OPTIONS") return route.fulfill({ status: 204, headers: cors(origin, request) });
    const next = respond(state, { method: request.method(), url: request.url(), body: request.postData() ?? "" });
    state = next.state;
    return route.fulfill({ status: next.response.status, headers: { ...cors(origin, request), "content-type": "application/json" }, body: next.response.body });
  });
  return {
    current: () => state,
    reachable: (flag) => {
      online = flag;
    }
  };
}
