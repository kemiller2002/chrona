// The engine side of the protocol, as an EngineTransport.
//
// Built with a closure factory rather than a class: the fold is pure
// (`step`), and the only mutable cell is the current model, held in one
// place. Swapping in a WASM transport later replaces this file alone.

import {
  answerHandshake,
  CORE_CONTRACT_IDENTITY,
  PROTOCOL_MINOR,
  PROTOCOL_VERSION,
  type BrowserToEngineMessage,
  type CorrelationId,
  type EffectResult,
  type EngineHandshake,
  type EngineRequirements,
  type EngineToBrowserMessage,
  type EngineTransport,
} from "@echelon-foundry/limen";
import { assertNever, eventToCommand, initialModel, transition, type Command, type Model } from "./domain.js";
import { project } from "./projection.js";

type Step = { readonly model: Model; readonly response: EngineToBrowserMessage };

/** What this engine asks of the host in the handshake (protocol 1.1+): the
 *  core contract it was compiled against, at the revision it understands, and
 *  no optional capability packs. Storage is a built-in effect, announced in
 *  Initialize.capabilities, not a negotiated pack. */
export const REQUIREMENTS: EngineRequirements = {
  protocol: { major: PROTOCOL_VERSION, minor: PROTOCOL_MINOR },
  contract: {
    unit: CORE_CONTRACT_IDENTITY.unit,
    version: CORE_CONTRACT_IDENTITY.version,
    fingerprint: CORE_CONTRACT_IDENTITY.fingerprint,
  },
  required: [],
  optional: [],
};

const respond = (
  model: Model,
  effects: EngineToBrowserMessage["effects"] = [],
  handshake?: EngineHandshake,
): Step => ({
  model,
  response: handshake === undefined
    ? { view: project(model), effects, cancellations: [] }
    : { view: project(model), effects, cancellations: [], handshake },
});

/** The command an effect result becomes. This slice only ever requests
 *  Storage effects, so any other result has no request behind it: that is a
 *  contract violation, not a domain outcome to represent as state. */
const resultToCommand = (result: EffectResult): Command => {
  switch (result.kind) {
    case "StorageResult":
      return { kind: "RecordStorage", correlationId: result.correlationId, outcome: result.outcome };
    case "HttpResult":
    case "ClipboardResult":
    case "NavigationResult":
    case "CapabilityResult":
      throw new Error(`Unexpected ${result.kind} (${result.correlationId}): this slice only requests Storage effects`);
    default:
      return assertNever(result);
  }
};

/** Pure: (model, message) -> (model, response). Throwing leaves the model
 *  unchanged; the kernel reports the throw as a dispatch BridgeError. */
export const step = (model: Model, message: BrowserToEngineMessage, correlationId: CorrelationId): Step => {
  switch (message.kind) {
    case "Initialize": {
      if (message.protocolVersion !== PROTOCOL_VERSION) throw new Error("Unsupported protocol version");
      if (!message.capabilities.includes("Storage")) {
        throw new Error("The kernel does not offer the Storage effect this slice requires");
      }
      // The kernel applies this view only after it verifies the answer. A
      // Rejected answer (for example HandshakeMissing from a pre-1.1 kernel)
      // leaves the kernel Incompatible and the page unbound.
      return respond(model, [], answerHandshake(message.handshake, REQUIREMENTS));
    }

    // The slice has no URL-driven state, so a browser-originated navigation is
    // not evidence about anything it owns. Re-projecting unchanged is what
    // "nothing happened here" looks like.
    case "LocationChanged":
      return respond(model);

    // The handshake selects no optional capability, so the kernel activates
    // none and routes no facts here. A fact arriving anyway is a contract
    // violation.
    case "CapabilityFact":
      throw new Error(`Unexpected CapabilityFact from ${message.capability}: this slice negotiated no capabilities`);

    case "Event":
    case "EffectResult": {
      const command = message.kind === "Event"
        ? eventToCommand(message.event, correlationId)
        : resultToCommand(message.result);
      const result = transition(model, command);
      return respond(result.model, result.accepted ? result.effects : []);
    }

    default:
      return assertNever(message);
  }
};

export const createSliceTransport = (): EngineTransport & { readonly current: () => Model } => {
  let model = initialModel();
  let sequence = 0;
  // The one place a raw string becomes a CorrelationId: the engine mints them.
  const nextCorrelationId = (): CorrelationId => `slice-${(sequence += 1)}` as CorrelationId;

  return {
    current: () => model,
    start: async () => undefined,
    dispatch: async (message) => {
      const next = step(model, message, nextCorrelationId());
      model = next.model;
      return next.response;
    },
  };
};
