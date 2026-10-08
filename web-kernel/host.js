// chrona.host: what Fides' sign-in client needs from the browser beyond
// Limen's core effects, on Limen's kernel side (defineCapability). Browser
// interop only; every decision is the engine's (src/Chrona.Application/
// Identity.fs).
//
// Requests ({ operation, ... }, every argument a string):
//   tabGet { key }            -> { kind: "Value", value }   (sessionStorage; value omitted when absent)
//   tabSet { key, value }     -> { kind: "Done" }
//   tabRemove { key }         -> { kind: "Done" }
//   leave { url }             -> { kind: "Done" }  sends the page to the identity provider (https only)
//   replaceAddress { url }    -> { kind: "Done" }  same-origin history.replaceState, no navigation
//   broadcast { message }     -> { kind: "Done" }  to the origin's other tabs (BroadcastChannel)
// Facts: { kind: "Broadcast", message } for a message another tab sent.
//
// Messages carry no token (Fides says only what happened). Session storage
// failures read as absent and writes are best effort: Fides keeps working in
// memory, which is its default retention anyway.
import { defineCapability } from "../node_modules/@echelon-foundry/limen/dist/kernel/capabilities.js";

export const HOST_CAPABILITY = {
  id: "chrona.host",
  version: 1,
  fingerprint: "chrona.host/1: tab storage, leave, replace address, broadcast"
};

const CHANNEL = "chrona.host";

const shapes = {
  tabGet: ["key"],
  tabSet: ["key", "value"],
  tabRemove: ["key"],
  leave: ["url"],
  replaceAddress: ["url"],
  broadcast: ["message"]
};

const decodeRequest = (value, path = "$") => {
  const fail = (expected) => ({ ok: false, error: { path, expected, found: JSON.stringify(value) } });
  if (typeof value !== "object" || value === null) return fail("an object");
  const fields = shapes[value.operation];
  if (fields === undefined) return fail(`an operation of ${Object.keys(shapes).join(", ")}`);
  for (const field of fields) {
    if (typeof value[field] !== "string") return fail(`'${field}' as a string`);
  }
  return { ok: true, value };
};

const session = (document, use) => {
  try {
    return use(document.defaultView.sessionStorage);
  } catch {
    return undefined;
  }
};

let channel = null;

export const hostCapability = () =>
  defineCapability({
    offer: HOST_CAPABILITY,
    decodeRequest,
    activate(host) {
      if (typeof BroadcastChannel === "function") {
        channel = new BroadcastChannel(CHANNEL);
        channel.onmessage = (event) => {
          if (typeof event.data === "string") host.emitFact({ kind: "Broadcast", message: event.data });
        };
      }
    },
    async execute(request, context) {
      const document = context.document;
      switch (request.operation) {
        case "tabGet": {
          const value = session(document, (storage) => storage.getItem(request.key));
          return typeof value === "string" ? { kind: "Value", value } : { kind: "Value" };
        }
        case "tabSet":
          session(document, (storage) => storage.setItem(request.key, request.value));
          return { kind: "Done" };
        case "tabRemove":
          session(document, (storage) => storage.removeItem(request.key));
          return { kind: "Done" };
        case "leave": {
          const target = new URL(request.url);
          if (target.protocol !== "https:") throw new Error("chrona.host leaves only for an https address");
          document.defaultView.location.assign(target.href);
          return { kind: "Done" };
        }
        case "replaceAddress": {
          const target = new URL(request.url, document.baseURI);
          if (target.origin === document.defaultView.location.origin) {
            document.defaultView.history.replaceState(null, "", target.href);
          }
          return { kind: "Done" };
        }
        case "broadcast":
          channel?.postMessage(request.message);
          return { kind: "Done" };
      }
      throw new Error(`chrona.host: unknown operation ${request.operation}`);
    }
  });
