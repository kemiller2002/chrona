// The engine side of the protocol, as an EngineTransport.
//
// Built with a closure factory rather than a class: the fold is pure
// (`step`), and the only mutable cell is the current model, held in one
// place. Swapping in a WASM transport later replaces this file alone.

import {
  PROTOCOL_VERSION,
  type BrowserToEngineMessage,
  type EngineToBrowserMessage,
  type EngineTransport,
} from "@echelon-foundry/typescript-wasm-kernel";
import { eventToCommand, initialModel, transition, type Model } from "./domain.js";
import { project } from "./projection.js";

type Step = { readonly model: Model; readonly response: EngineToBrowserMessage };

const respond = (model: Model, effects: EngineToBrowserMessage["effects"] = []): Step => ({
  model,
  response: { view: project(model), effects, cancellations: [] },
});

/** Pure: (model, message) -> (model, response). */
export const step = (model: Model, message: BrowserToEngineMessage, correlationId: string): Step => {
  if (message.kind === "Initialize") {
    if (message.protocolVersion !== PROTOCOL_VERSION) throw new Error("Unsupported protocol version");
    return respond(model);
  }

  const command = message.kind === "Event"
    ? eventToCommand(message.event, correlationId as never)
    : message.result.kind === "StorageResult"
      ? ({ kind: "RecordStorage", correlationId: message.result.correlationId, outcome: message.result.outcome } as const)
      : (() => { throw new Error("This slice never requests an Http effect"); })();

  const result = transition(model, command);
  return respond(result.model, result.accepted ? result.effects : []);
};

export const createSliceTransport = (): EngineTransport & { readonly current: () => Model } => {
  let model = initialModel();
  let sequence = 0;
  const nextCorrelationId = () => `slice-${(sequence += 1)}`;

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
