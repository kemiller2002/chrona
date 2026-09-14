// Tier 3 — projection only. Turns authoritative state into a flat ViewState.
// No DOM operations; the kernel decides how a ViewState reaches the page.

import type { ViewState } from "@echelon-foundry/typescript-wasm-kernel";
import { assertNever, type Model, type Phase } from "./domain.js";

const statusText = (phase: Phase): string => {
  switch (phase.kind) {
    case "Empty": return "Type a label, then save it.";
    case "Editing": return "Ready to save.";
    case "Invalid": return phase.reason;
    case "Saving": return "Saving…";
    case "Saved": return `Saved "${phase.label}".`;
    case "Loading": return "Loading…";
    case "Loaded": return `Loaded "${phase.label}".`;
    case "Absent": return "Nothing stored yet.";
    case "StorageFailed": return `Storage failed (${phase.reason}).`;
    default: return assertNever(phase);
  }
};

const draftOf = (phase: Phase): string =>
  phase.kind === "Editing" || phase.kind === "Invalid" ? phase.draft
  : phase.kind === "Saved" || phase.kind === "Loaded" ? phase.label
  : "";

const busy = (phase: Phase): boolean => phase.kind === "Saving" || phase.kind === "Loading";

export const project = (model: Model): ViewState => ({
  statusText: statusText(model.phase),
  phaseKind: model.phase.kind,
  draft: draftOf(model.phase),
  saveDisabled: busy(model.phase) || model.phase.kind === "Invalid" || model.phase.kind === "Empty",
  loadDisabled: busy(model.phase),
  hasProblem: model.phase.kind === "Invalid" || model.phase.kind === "StorageFailed",
  problemText: model.phase.kind === "Invalid" ? model.phase.reason
    : model.phase.kind === "StorageFailed" ? `Storage failed (${model.phase.reason}).`
    : "",
  log: model.log.map((entry) => ({ id: entry.id, text: entry.text })),
});
