/// Rename and source compatibility (requirements expansion 31,
/// DF-CHRONA-2026-0002 decision 5).
///
/// Historical data and integrations may name the product, its assemblies,
/// its record formats or its browser storage by their legacy names. Each
/// legacy name resolves to its Chrona meaning only through the explicit
/// tables below: matching ignores surrounding whitespace and letter case and
/// nothing else, and a name that is not listed is a diagnostic, never a
/// guess.
module Chrona.Domain.Compatibility

open System
open Chrona.Domain.Diagnostics

/// The canonical product name.
[<Literal>]
let ProductName = "Chrona"

/// What a legacy name stands for in Chrona.
type LegacyName =
    /// The product itself, under one of its earlier names.
    | Product
    /// A legacy assembly and the Chrona assembly that replaces it.
    | Assembly of replacedBy: string
    /// A legacy browser-storage key. Chrona never reads or writes these;
    /// an import names what it found.
    | LegacyDocument
    | LegacySyncSettings

/// A recognised legacy record format (time-tracking-data).
type LegacyFormat =
    | LegacyActivity
    | LegacyEvidenceLink

let private names =
    [ "time-tracking-application", Product
      "Business Activity Ledger", Product
      "echelon-business-activity-ledger", Product
      "Echelon Ledger", Product
      "Ledger", Product
      "Chrona", Product
      "Ledger.Domain", Assembly "Chrona.Domain"
      "Ledger.Engine", Assembly "Chrona.Engine"
      "Ledger.Wasm", Assembly "Chrona.Wasm"
      "business-activity-ledger:v1", LegacyDocument
      "business-activity-ledger:github-config:v1", LegacySyncSettings ]

let private formats =
    [ ("business-activity", "1.0.0"), LegacyActivity
      ("business-evidence-link", "1.0.0"), LegacyEvidenceLink ]

let private normalize (value: string) =
    value.Trim().ToLowerInvariant()

let private byName =
    names |> List.map (fun (name, meaning) -> normalize name, meaning) |> Map.ofList

/// The Chrona meaning of a legacy (or current) name.
let resolve (name: string) : Result<LegacyName, Diagnostic> =
    match byName.TryFind(normalize name) with
    | Some meaning -> Ok meaning
    | None -> Error(UnknownLegacySource name)

/// The legacy record format a `schema_name`/`schema_version` pair names.
/// Versions match exactly: a later legacy version is unknown until it is
/// listed here.
let format (schemaName: string) (schemaVersion: string) : Result<LegacyFormat, Diagnostic> =
    let key = normalize schemaName, schemaVersion.Trim()

    match formats |> List.tryFind (fun ((name, version), _) -> (name, version) = key) with
    | Some(_, legacy) -> Ok legacy
    | None -> Error(UnknownLegacyFormat(schemaName, schemaVersion))

/// Every legacy name that resolves to the product.
let productAliases =
    names |> List.choose (fun (name, meaning) -> if meaning = Product && name <> ProductName then Some name else None)
