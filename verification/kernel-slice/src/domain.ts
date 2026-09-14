// Tier 1/2 — Semantic model and legal transitions.
//
// Pure. Knows nothing about the DOM, fetch, localStorage, or the kernel's
// mechanism. Effects are returned as data; Tier 4 executes them.

import type {
  CorrelationId,
  EffectRequest,
  SemanticEvent,
  StorageOutcome,
} from "@echelon-foundry/typescript-wasm-kernel";

const STORAGE_KEY = "chrona.kernel-slice.label";
const MAX_LABEL = 24;

export type Label = string & { readonly __label: unique symbol };

/** The kernel's StorageOutcome failure reasons, named explicitly.
 *  Deriving this with a conditional type collapses to `never`, because the
 *  Success arm of the union has no `reason` field. */
export type StorageFailureReason = "unavailable" | "quota-exceeded";

/** Phase is the closed set of things that can be true. */
export type Phase =
  | { readonly kind: "Empty" }
  | { readonly kind: "Editing"; readonly draft: string }
  | { readonly kind: "Invalid"; readonly draft: string; readonly reason: string }
  | { readonly kind: "Saving"; readonly label: Label; readonly correlationId: CorrelationId }
  | { readonly kind: "Saved"; readonly label: Label }
  | { readonly kind: "Loading"; readonly correlationId: CorrelationId }
  | { readonly kind: "Loaded"; readonly label: Label }
  | { readonly kind: "Absent" }
  | { readonly kind: "StorageFailed"; readonly reason: StorageFailureReason };

export type LogEntry = { readonly id: string; readonly text: string };

export type Model = {
  readonly phase: Phase;
  readonly log: readonly LogEntry[];
  readonly sequence: number;
};

export type Command =
  | { readonly kind: "DraftChanged"; readonly value: string }
  | { readonly kind: "Save"; readonly correlationId: CorrelationId }
  | { readonly kind: "Load"; readonly correlationId: CorrelationId }
  | { readonly kind: "RecordStorage"; readonly correlationId: CorrelationId; readonly outcome: StorageOutcome };

export type TransitionError =
  | { readonly kind: "IllegalFromCurrentPhase" }
  | { readonly kind: "StaleEffectResult" }
  | { readonly kind: "InvalidLabel"; readonly reason: string };

export type TransitionResult =
  | { readonly accepted: true; readonly model: Model; readonly effects: readonly EffectRequest[] }
  | { readonly accepted: false; readonly model: Model; readonly error: TransitionError };

export const assertNever = (value: never): never => {
  throw new Error(`Unhandled variant: ${JSON.stringify(value)}`);
};

export const initialModel = (): Model => ({ phase: { kind: "Empty" }, log: [], sequence: 0 });

/** Guard: the only place a raw string becomes a Label. */
export const decodeLabel = (value: string): Label | { readonly reason: string } => {
  const trimmed = value.trim();
  if (trimmed.length === 0) return { reason: "A label is required." };
  if (trimmed.length > MAX_LABEL) return { reason: `A label may be at most ${MAX_LABEL} characters.` };
  return trimmed as Label;
};

const isLabel = (v: Label | { readonly reason: string }): v is Label => typeof v === "string";

const note = (model: Model, text: string): Model => ({
  ...model,
  sequence: model.sequence + 1,
  log: [...model.log, { id: `entry-${model.sequence + 1}`, text }],
});

/** The correlation id currently awaited, or null when nothing is in flight. */
const pending = (phase: Phase): CorrelationId | null =>
  phase.kind === "Saving" || phase.kind === "Loading" ? phase.correlationId : null;

export const eventToCommand = (event: SemanticEvent, correlationId: CorrelationId): Command => {
  switch (event.name) {
    case "draftChanged":
      return { kind: "DraftChanged", value: event.value ?? "" };
    case "save":
      return { kind: "Save", correlationId };
    case "load":
      return { kind: "Load", correlationId };
    default:
      throw new Error(`Unknown event name: ${event.name}`);
  }
};

const reject = (model: Model, error: TransitionError): TransitionResult => ({ accepted: false, model, error });

const accept = (model: Model, effects: readonly EffectRequest[] = []): TransitionResult => ({
  accepted: true,
  model,
  effects,
});

const outcomeText = (outcome: StorageOutcome): string =>
  outcome.kind === "Success"
    ? `Success (value=${outcome.value === null ? "null" : JSON.stringify(outcome.value)})`
    : `Failure (${outcome.reason})`;

export const transition = (model: Model, command: Command): TransitionResult => {
  switch (command.kind) {
    case "DraftChanged": {
      const decoded = decodeLabel(command.value);
      return accept({
        ...model,
        phase: isLabel(decoded)
          ? { kind: "Editing", draft: command.value }
          : { kind: "Invalid", draft: command.value, reason: decoded.reason },
      });
    }

    case "Save": {
      if (model.phase.kind !== "Editing") {
        return model.phase.kind === "Invalid"
          ? reject(model, { kind: "InvalidLabel", reason: model.phase.reason })
          : reject(model, { kind: "IllegalFromCurrentPhase" });
      }
      const decoded = decodeLabel(model.phase.draft);
      if (!isLabel(decoded)) return reject(model, { kind: "InvalidLabel", reason: decoded.reason });
      return accept(
        note({ ...model, phase: { kind: "Saving", label: decoded, correlationId: command.correlationId } }, "save requested"),
        [{ kind: "Storage", correlationId: command.correlationId, operation: "set", key: STORAGE_KEY, value: decoded }],
      );
    }

    case "Load":
      if (pending(model.phase) !== null) return reject(model, { kind: "IllegalFromCurrentPhase" });
      return accept(
        note({ ...model, phase: { kind: "Loading", correlationId: command.correlationId } }, "load requested"),
        [{ kind: "Storage", correlationId: command.correlationId, operation: "get", key: STORAGE_KEY }],
      );

    case "RecordStorage": {
      const awaited = pending(model.phase);
      if (awaited === null || awaited !== command.correlationId) {
        return reject(model, { kind: "StaleEffectResult" });
      }
      const logged = note(model, `${model.phase.kind}: ${outcomeText(command.outcome)}`);
      if (command.outcome.kind === "Failure") {
        return accept({ ...logged, phase: { kind: "StorageFailed", reason: command.outcome.reason } });
      }
      if (model.phase.kind === "Saving") {
        return accept({ ...logged, phase: { kind: "Saved", label: model.phase.label } });
      }
      const value = command.outcome.value;
      return accept({
        ...logged,
        phase: value === null ? { kind: "Absent" } : { kind: "Loaded", label: value as Label },
      });
    }

    default:
      return assertNever(command);
  }
};
