/// The legacy look and feel, expressed as Forma tokens (WI-0026): Chrona's
/// Brand Manifest carries the legacy palette, type and radii; the CSS the
/// page loads is Forma's compiler output for exactly that manifest; and the
/// colour pairs Chrona relies on meet WCAG 2.2 AA, including the pairs
/// Forma's compiler does not gate.
module Chrona.Tests.BrandTokenTests

open System
open System.Globalization
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Xunit
open Chrona.Tests.Support

let private manifest = lazy (JsonNode.Parse(readRepoFile "brand/chrona.brand.json"))

let private color (theme: string) (group: string) (role: string) =
    manifest.Value.["themes"].[theme].["color"].[group].[role].GetValue<string>()

/// WCAG 2.x relative luminance of a #rrggbb colour.
let private luminance (hex: string) =
    let channel (offset: int) =
        let value = float (Int32.Parse(hex.Substring(offset, 2), NumberStyles.HexNumber)) / 255.0
        if value <= 0.03928 then value / 12.92 else Math.Pow((value + 0.055) / 1.055, 2.4)

    0.2126 * channel 1 + 0.7152 * channel 3 + 0.0722 * channel 5

let private contrast (a: string) (b: string) =
    let la, lb = luminance a, luminance b
    (max la lb + 0.05) / (min la lb + 0.05)

let private themes = [ "light"; "dark" ]

[<Fact>]
let ``the manifest is a Forma 1.0 brand named chrona`` () =
    let m = manifest.Value
    Assert.Equal("1.0", m.["schemaVersion"].GetValue<string>())
    Assert.Equal("chrona", m.["id"].GetValue<string>())
    Assert.Equal("Chrona", m.["identity"].["name"].GetValue<string>())

[<Fact>]
let ``the manifest keeps the legacy palette, type and radii`` () =
    // time-tracking-application web/styles.css :root, light theme.
    for group, role, legacy in
        [ "surface", "primary", "#e8e4dc" // --canvas
          "surface", "secondary", "#f8f5ee" // --paper
          "surface", "elevated", "#fffdf8" // --paper-raised
          "surface", "inverse", "#23312e" // .sidebar
          "surface", "inverse-secondary", "#243a36" // .tracker
          "text", "primary", "#1f2826" // --ink
          "text", "secondary", "#53605c" // --ink-soft
          "border", "subtle", "#d2cdc2" // --line
          "accent", "primary", "#215d57" // --mineral
          "accent", "hover", "#17443f" // --mineral-dark
          "accent", "secondary", "#9a4d32" // --rust
          "focus", "ring", "#0b6fb8" // --focus
          "status", "danger", "#8c3529" ] do // --danger
        Assert.Equal(legacy, color "light" group role)

    let fonts = manifest.Value.["presentation"].["fontFamily"]
    Assert.Equal("Iowan Old Style", fonts.["display"].[0].GetValue<string>())
    Assert.Equal("Inter", fonts.["sans"].[0].GetValue<string>())
    Assert.Equal("SFMono-Regular", fonts.["mono"].[0].GetValue<string>())

[<Fact>]
let ``the page's brand CSS is the compiler's output for this manifest`` () =
    let css = readRepoFile "web/brand/chrona.css"
    Assert.StartsWith("/* Generated from chrona.brand.json. Do not edit directly. */", css)
    Assert.Contains("[data-ef-brand=\"chrona\"]", css)

    // Each theme's block declares every manifest colour under its Forma name.
    for theme in themes do
        let block =
            Regex.Match(css, $"\\[data-ef-brand=\"chrona\"\\]\\[data-ef-theme=\"{theme}\"\\] \\{{([^}}]*)\\}}").Groups[1].Value

        Assert.NotEmpty block
        let groups = manifest.Value.["themes"].[theme].["color"].AsObject()

        for group in groups do
            for role in group.Value.AsObject() do
                Assert.Contains($"--ef-color-{group.Key}-{role.Key}: {role.Value.GetValue<string>()};", block)

[<Fact>]
let ``the compile script pins the Forma release the page consumes`` () =
    let script = readRepoFile "tools/brand/compile.sh"
    let pinned = (JsonNode.Parse(readRepoFile "package.json")).["dependencies"].["@echelon-foundry/design-system"].GetValue<string>()
    let tag = Regex.Match(script, "FORMA_TAG=\"(v[0-9.]+)\"").Groups[1].Value
    Assert.Contains($"/download/{tag}/", pinned)
    Assert.Contains("compile.sh --check", readRepoFile ".github/workflows/build.yml")

[<Fact>]
let ``every colour pair Chrona relies on meets WCAG 2.2 AA in both themes`` () =
    for theme in themes do
        let c = color theme
        let surfaces = [ c "surface" "primary"; c "surface" "secondary" ]

        let pairs =
            [ for surface in surfaces do
                  // Text and status text on both surfaces (SC 1.4.3).
                  for group, role in
                      [ "text", "primary"
                        "text", "secondary"
                        "accent", "primary"
                        "status", "success"
                        "status", "warning"
                        "status", "danger"
                        "status", "info" ] do
                      $"{group}-{role} on {surface}", c group role, surface, 4.5
                  // Component boundaries and focus indicators (SC 1.4.11).
                  $"border-functional on {surface}", c "border" "functional", surface, 3.0
                  $"focus-ring on {surface}", c "focus" "ring", surface, 3.0
              // The filled primary action: surface colour on the accent.
              "primary action label", c "surface" "primary", c "accent" "primary", 4.5
              // The dark sidebar and the timer hero.
              "inverse text on inverse", c "text" "inverse", c "surface" "inverse", 4.5
              "inverse text on inverse-secondary", c "text" "inverse", c "surface" "inverse-secondary", 4.5 ]

        for name, foreground, background, minimum in pairs do
            let ratio = contrast foreground background
            Assert.True(ratio >= minimum, $"{theme}: {name} is {ratio:F2}:1, below {minimum}:1")

[<Fact>]
let ``the inventory shows every legacy screen and every screenshot it ships`` () =
    let inventory = readRepoFile "docs/legacy/look-and-feel-inventory.md"

    let screens =
        [ "index"; "start"; "stop"; "manual-entry"; "today"; "month"; "review"; "activity"; "correction"
          "evidence"; "split"; "merge"; "remove"; "restore"; "more"; "settings"; "sign-in"; "states" ]

    for screen in screens do
        Assert.Contains($"`{screen}.html`", inventory)
        Assert.Contains($"(screenshots/{screen}-desktop.jpg)", inventory)

    let linked =
        Regex.Matches(inventory, @"\(screenshots/([a-z-]+\.jpg)\)") |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq

    let shipped =
        Directory.GetFiles(repoFile "docs/legacy/screenshots") |> Seq.map Path.GetFileName |> Set.ofSeq

    Assert.Equal<Set<string>>(shipped, linked)
