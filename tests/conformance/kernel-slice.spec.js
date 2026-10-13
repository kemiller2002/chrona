// UI-LAYOUT-INTERACTION@1.0.0 for the kernel verification slice
// (web/kernel-slice.html, published beside the application), GH-112. Its two
// actions are asserted by tests/browser/kernel-slice.spec.js (cited in
// chrona.contract.mjs); this measures its layout.
import { test, expect, setWidth, expectNoOverflowRecorded } from "./support.mjs";
import { CONTRACT_WIDTHS } from "./chrona.contract.mjs";

test("kernel-slice: no overflow or clipping at 320/390/768/1280 (ULI-L05, ULI-L09)", async ({ slice: page }) => {
  for (const width of CONTRACT_WIDTHS) {
    await setWidth(page, width);
    await expect(page.getByRole("button", { name: "Save" })).toBeVisible();
    await expectNoOverflowRecorded(page, "kernel-slice", width);
  }
});
