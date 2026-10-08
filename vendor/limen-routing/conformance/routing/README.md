# Routing semantics

The language-neutral definition of routing for Limen engines
(kemiller2002/limen#20, LCP-005). [`routing.vectors.json`](routing.vectors.json)
is the same definition as data. A routing library in any language conforms
when it produces exactly those results from those tables.

Routing is **application meaning**, so it lives in the engine. The browser
side stays mechanism: the kernel reports locations (`Initialize.location`,
`LocationChanged`) and performs `Navigation` effects (`push`, `replace`,
`back`, `forward`). It never parses a route. Nothing here is a Limen protocol
type, and no library's API shape is canonical. The F# reference library
([`libraries/fsharp/Limen.Routing`](../../libraries/fsharp/Limen.Routing)) is
one conforming implementation.

## Route tables

```json
{ "name": "invoice", "path": "{id:int}", "requires": ["invoice"],
  "query": [ { "name": "tab", "type": "string", "required": false } ],
  "children": [ { "name": "summary", "path": "" }, { "name": "history", "path": "history" } ] }
```

| Field | Meaning |
| --- | --- |
| `name` | unique among siblings. A route's full name joins its chain with `.`, e.g. `invoices.invoice.history`. |
| `path` | segments separated by `/`, relative to the parent. `""` is the parent's own location (an index). |
| segment `literal` | matches exactly, case-sensitively |
| segment `{name:string}` / `{name:int}` | matches any one segment, then converts it (see below) |
| segment `{*name}` | last segment only; captures the rest, zero or more segments, joined by `/` |
| `query` | declared query parameters: `name`, `type` (`string` or `int`), `required` |
| `children` | nested routes. A route with children is matched only through one of them, and is not itself a destination. |
| `redirect` | `{ "to": fullName, "params": { name: "{sourceParam}" or literal } }` |
| `guard` | the name of an engine-supplied decision (below) |
| `requires` | resource keys the engine should have loaded before rendering. Returned with the match; the router loads nothing. |

## Resolving a location

A location is a path and a query string (`?` optional). Resolution runs these
steps in order, and the first failure is the result:

1. **Decode the path.** Split on `/`, and drop empty segments, so trailing and
   repeated slashes are ignored. Percent-decode each segment as UTF-8. An
   invalid escape or invalid UTF-8 gives `MalformedPath`. An encoded `%2F`
   stays inside its segment.
2. **Decode the query.** Split on `&`, then on the first `=`. A key without
   `=` has the value `""`. `+` is a space. Percent-decode as UTF-8; failure
   gives `MalformedQuery`.
3. **Match structurally.** Try routes in declaration order, depth first. A
   route matches when its literal segments match and every parameter segment
   has a segment to take. It must end on a destination with no segment left.
   The **first** structural match decides. None gives `NotFound`, which a
   trailing `{*rest}` route turns into an ordinary match.
4. **Convert path parameters.**
   - `int` is a canonical decimal integer: `0` or `-?[1-9][0-9]*`, within
     ±(2⁵³−1).
   - `string` is any decoded segment.
   - A failure gives `Invalid { route, parameter, value, expected }` for the
     matched route. Resolution does **not** fall through to a later route or
     the wildcard, because a malformed identifier is an error, not a
     different page.
5. **Follow redirects.** `params` values of the form `{x}` take source
   parameter `x`; anything else is literal. The query carries over. Visiting
   a route a second time gives `RedirectLoop { chain }`.
6. **Convert query parameters**, declared along the whole chain, parent
   first. Undeclared keys are ignored.
   - A declared key given twice gives `Invalid`, with `value` the values
     joined by `,` and `expected` `"a single value"`.
   - A missing required key gives `Invalid`, with `value` `""` and
     `expected` `"<type> (required)"`.
   - A type failure gives `Invalid`, as for path parameters.
7. **Consult guards**, outermost first along the chain. Each is the engine's
   decision: `allow`, `deny` (gives `Denied { route }`), or
   `redirect { to, params, query }`. A redirect continues at step 5 with the
   guard's own query, under the same loop rule.

A success is `Matched { route, chain, query, requires, redirectedFrom }`:

| Field | Contents |
| --- | --- |
| `route` | the destination's full name |
| `chain` | each level's local name and typed parameters |
| `query` | the typed declared values; absent optional parameters are omitted |
| `requires` | the union along the chain |
| `redirectedFrom` | the full names that redirected, in order |

**Guards are not security.** A guard decides what the *interface* shows. The
authority that protects data is the server, which must refuse whatever this
user may not do, however the request arrives.

## Building a location

`build(route, params, query)` gives the canonical location, or an error.

- **Path.** The chain's segments, with each parameter percent-encoded. A
  wildcard is encoded segment by segment. The root is `/`, and there is never
  a trailing slash.
- **Query.** Declared parameters only, parent first, in declaration order.
  Absent optional parameters are omitted. `int` values are written in
  decimal.
- **Encoding.** Every UTF-8 byte except `A–Z a–z 0–9 - . _ ~` becomes `%XX`
  with upper-case hex. A space is `%20`, never `+`.
- **Errors:**
  - `UnknownRoute`: not a destination, including a route with children;
  - `MissingParameter`: a path parameter or required query parameter is
    absent;
  - `InvalidParameter`: the wrong type, or an `int` that is not 53-bit safe.

## Adopting locations and navigating

The engine keeps the current canonical location as state. Three rules follow.
Together they make `LocationChanged` never cause a redundant push.

- **Adopt** a location the browser reports, from `Initialize` (a deep link)
  or `LocationChanged` (Back, Forward): resolve it.
  - If it matched and its canonical form differs, because of a redirect, a
    trailing slash or undeclared query keys, the effect is
    `replace(canonical)`. The browser's entry is corrected in place.
  - Otherwise there is **no effect**. An adopted location is never answered
    with a push.
- **Navigate** in-app to `(route, params, query)`: build it. If it equals the
  current location there is no effect; otherwise `push(built)`.
- A location that does not match (`Invalid`, `NotFound`, `Malformed…`) is
  adopted as-is and renders the engine's own error. It produces no effect.

# URL state (LCP-088..112)

[`url-state.vectors.json`](url-state.vectors.json) extends these semantics so
that an application can keep all of its navigable state in the URL and a
copied URL opens the same view
([requirements](https://github.com/kemiller2002/limen/blob/main/docs/requirements/LIMEN-URL-STATE-REQUIREMENTS.md),
[DF-LIMEN-2026-0006](https://github.com/kemiller2002/limen/blob/main/research/decisions/DF-LIMEN-2026-0006--url-state-hash-routing-and-route-inventory.md)).
Everything above still holds, and `routing.vectors.json` still passes
unchanged. A conforming library runs both files.

## Parameter types

| Type | Canonical text | Path | Query | Invalid when |
| --- | --- | --- | --- | --- |
| `string` | the text | yes | yes | never |
| `int` | as above | yes | yes | as above |
| `bool` | `true` or `false` | no | yes | anything else |
| `date` | `YYYY-MM-DD`, ASCII digits, a real calendar date | yes | yes | `2026-02-29`, `2026-2-01`, non-ASCII digits |
| `month` | `YYYY-MM`, year 0001–9999, month 01–12 | yes | yes | `2026-13` |
| `enum` | one of `values`, case-sensitive | yes (`{v:enum:a\|b}`) | yes | any other text |
| `set` | members joined by `,` | no | yes | an empty member, or a member outside a non-empty `values` |

A **set** is one key. Its raw value is split on `,` before each member is
percent-decoded, so an encoded comma (`%2C`) stays inside its member. Members
are deduplicated and sorted by UTF-16 code unit. An empty `values` list
allows any non-empty member. The expected text of an `Invalid` result is
`one of a|b` for an enum, `a set of a|b` or `a set of non-empty values` for a
set, and the type name otherwise.

## Defaults and the canonical form

A query parameter may declare a `default` (not with `required`).

- **Resolving:** an absent key resolves to its default, so the match carries
  the full view state. A given key is converted as usual.
- **Building:** a parameter equal to its default (same canonical text) is
  omitted, and so is an empty set. Everything else is as above.

So one view has one location, and adopting a location that spells out a
default replaces it with the shorter canonical form.

## Definitions are values

`define(routes, legacy, roles)` returns the table or every problem, in this
order: per route, depth first (its segments, repeated parameters, reserved
names, path types, query parameters, its redirect), then legacy entries,
then roles.

| Error | When |
| --- | --- |
| `InvalidSegment { route, segment }` | a path segment does not parse (including `{x:bool}` and `{x:set}`), or a wildcard is not the last segment of a destination |
| `DuplicateName { route }` | two siblings share a name |
| `DuplicateParameter { route, parameter }` | a name is declared twice along one chain, path or query |
| `ReservedName { route, parameter }` | the name, lower-cased without `-` and `_`, is `token`, `accesstoken`, `idtoken`, `refreshtoken`, `password`, `passwd`, `secret`, `clientsecret`, `apikey`, `key`, `session`, `sessionid`, `auth`, `authorization`, `code`, `credential` or `credentials` (LCP-109) |
| `InvalidValues { route, parameter }` | an enum with no values, or an enum or set with an empty or repeated value |
| `InvalidDefault { route, parameter }` | a default that is not a value of its type |
| `RequiredWithDefault { route, parameter }` | a required parameter with a default |
| `UnknownTarget { route, target }` | a redirect or legacy entry names no destination |
| `UnknownParameter { route, parameter }` | a redirect template copies a parameter its pattern does not have |
| `UnknownRole { role, route }` | `home`, `signIn` or `notFound` names no destination |

**Roles** name the home route (required), and optionally the sign-in and
not-found routes.

**Legacy entries** `{ path, to, params }` become redirect routes named
`legacy-1`, `legacy-2`, …, matched after every current route and before the
first top-level wildcard, so a current route always wins. They follow the
redirect rules above. An application's typed `format` never produces them.

Locations longer than **8,192** characters (path and query together) are
`TooLong` before anything is decoded.

## Outcomes

A resolution maps onto a closed set of route outcomes, which an engine renders
each in its own way:

| Resolution | Outcome |
| --- | --- |
| `Matched` of the `notFound` role, or `NotFound` | `NotFound` |
| `Denied { route }` | `NotPermitted { route }` |
| `Invalid { … }` | `Invalid { … }` |
| `MalformedPath`, `MalformedQuery`, `TooLong` | `Malformed { part: "path" \| "query" \| "length" }` |
| `RedirectLoop { chain }` | `RedirectLoop { chain }` |
| any other `Matched` | the match, or `Unmapped` when the application's typed mapping refuses it |

## Navigate, refine, replace

Beside **adopt** and **navigate** (push):

- **refine** builds the location and answers `replace(built)`, or nothing
  when it is already current. Use it for in-place changes of the same view
  (filters, sort, page, tab, date), so Back steps between places.
- **replace** answers `replace(location)` for a location the engine decided
  on itself, such as the result of `resume` after sign-in.

The session vectors model the browser's history: a push truncates forward
entries and appends, a replace rewrites the current entry, and Back or
Forward adopt the entry they land on (or leave the application at either
end).

## Return targets

- **capture(location)** gives the canonical location to return to after
  sign-in, or nothing. It must be a single-slash relative location (no `//`,
  no `\`, no control character, at most 8,192 characters) that matches,
  guards **not** consulted, a route that is not the sign-in or not-found
  route and whose `returnTarget` is not `false`.
- **signIn(target)** builds the sign-in route with the target as its
  `returnTo` query parameter.
- **resume(target)** gives the target's canonical location when it is still
  eligible **and its guards allow it now**, and otherwise the home route's
  location. The engine replaces to it, so Back does not return to sign-in.

## Locations and links

The routed location is a path and query, `/invoices/42?tab=history`.

| Mode | From the browser's `{ origin, path, query, hash }` | href | share |
| --- | --- | --- | --- |
| `hash` (default) | the fragment without `#`; empty is `/`, and a fragment without a leading `/` gets one | `#` + location | origin + path + query + `#` + location |
| `path` | path (or `/`) + query | the location | origin + location |

## Route inventory

`inventory(mode, table)` renders `echelon.routes/v1` as JSON with keys sorted
by UTF-16 code unit, two-space indentation, and a final newline. It is
byte-identical in every conforming library (the vectors compare the text).

- **Top level:** `schema`, `mode`, `home`, `signIn`, `notFound` (or
  `null`), `routes` and `legacy`.
- **`routes`:** every destination that is not a redirect, in table order,
  with:
  - `name`;
  - `pattern`: `/` + segments, with `{name:type}` and `{*name}`;
  - `params`: path parameters, then query parameters, parent first, each
    with `name`, `in`, `type`, `required`, `default` (JSON-typed, or
    `null`) and `values` (enum and set values, or `null`);
  - `guards` along the chain;
  - `requires`;
  - `returnTarget`.
- **`legacy`:** every redirect route, including legacy entries, with
  `name`, `pattern`, `to` and `params`.
