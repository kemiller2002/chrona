// chrona.print: asks the browser to print the page, on Limen's kernel side
// (defineCapability). Browser interop only.
//
// What prints is the page's Folio document (<ef-print-document>, styled by
// Folio's print.css and chrona.css's print rules); this pack only opens the
// browser's print dialog, from which the person prints or saves a PDF. The
// renderer is whichever browser they use: Folio's portable profile, not a
// controlled PDF pipeline.
//
// Requests: { action: "print" }
// Results:  { kind: "Printed" }   (the dialog was opened and has closed)
import { defineCapability } from "../node_modules/@echelon-foundry/limen/dist/kernel/capabilities.js";

export const PRINT_CAPABILITY = {
  id: "chrona.print",
  version: 1,
  fingerprint: "chrona.print/1: print"
};

const decodeRequest = (value, path = "$") =>
  typeof value === "object" && value !== null && value.action === "print"
    ? { ok: true, value: { action: "print" } }
    : { ok: false, error: { path, expected: "{action:'print'}", found: JSON.stringify(value) } };

export const printCapability = () =>
  defineCapability({
    offer: PRINT_CAPABILITY,
    decodeRequest,
    async execute(_request, context) {
      context.document.defaultView?.print();
      return { kind: "Printed" };
    }
  });
