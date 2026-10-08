/// Rename and source compatibility (CHX-310) and the product identity
/// (CHX-011), per DF-CHRONA-2026-0002.
module Chrona.Tests.CompatibilityTests

open Xunit
open Chrona.Domain.Diagnostics
open Chrona.Domain.Compatibility
open Chrona.Tests.Support

[<Fact>]
let ``every historical product name resolves to Chrona`` () =
    for name in [ "time-tracking-application"; "Business Activity Ledger"; "echelon-business-activity-ledger"; "Echelon Ledger"; "Ledger"; "Chrona" ] do
        Assert.Equal(Ok Product, resolve name)

    Assert.Equal<string list>(
        [ "time-tracking-application"; "Business Activity Ledger"; "echelon-business-activity-ledger"; "Echelon Ledger"; "Ledger" ],
        productAliases
    )

[<Fact>]
let ``matching ignores surrounding whitespace and case, and nothing else`` () =
    Assert.Equal(Ok Product, resolve "  business activity LEDGER ")
    Assert.Equal(Error(UnknownLegacySource "BusinessActivityLedger"), resolve "BusinessActivityLedger")
    Assert.Equal(Error(UnknownLegacySource "time tracking application"), resolve "time tracking application")

[<Fact>]
let ``legacy assemblies name the Chrona assembly that replaces them`` () =
    Assert.Equal(Ok(Assembly "Chrona.Domain"), resolve "Ledger.Domain")
    Assert.Equal(Ok(Assembly "Chrona.Engine"), resolve "Ledger.Engine")
    Assert.Equal(Ok(Assembly "Chrona.Wasm"), resolve "Ledger.Wasm")

[<Fact>]
let ``legacy browser storage is recognised, never adopted`` () =
    Assert.Equal(Ok LegacyDocument, resolve "business-activity-ledger:v1")
    Assert.Equal(Ok LegacySyncSettings, resolve "business-activity-ledger:github-config:v1")
    // A later legacy document version is not silently treated as v1.
    Assert.Equal(Error(UnknownLegacySource "business-activity-ledger:v2"), resolve "business-activity-ledger:v2")

[<Fact>]
let ``legacy record formats match by exact version, and unknown formats are refused`` () =
    Assert.Equal(Ok LegacyActivity, format "business-activity" "1.0.0")
    Assert.Equal(Ok LegacyEvidenceLink, format "business-evidence-link" "1.0.0")
    Assert.Equal(Error(UnknownLegacyFormat("business-activity", "1.1.0")), format "business-activity" "1.1.0")
    Assert.Equal(Error(UnknownLegacyFormat("attestation", "1.0.0")), format "attestation" "1.0.0")

[<Fact>]
let ``unknown legacy names and formats have stable codes`` () =
    Assert.Equal("CHRONA.LEGACY.UNKNOWN_SOURCE", code (UnknownLegacySource "x"))
    Assert.Equal("CHRONA.LEGACY.UNKNOWN_FORMAT", code (UnknownLegacyFormat("x", "1")))

[<Fact>]
let ``the charter and current state describe the time-tracking product`` () =
    let charter = readRepoFile "PROJECT-CHARTER.md"
    Assert.Contains("Chrona records, corrects, reviews, reports and publishes business time", charter)
    Assert.Contains("status: accepted", charter)

    for generic in [ "Not yet established"; "communication problem"; "Not yet selected" ] do
        Assert.DoesNotContain(generic, charter)

    let state = readRepoFile "context/CURRENT-STATE.md"
    Assert.DoesNotContain("No application code exists yet", state)
    Assert.Contains("Chrona.Domain", state)
