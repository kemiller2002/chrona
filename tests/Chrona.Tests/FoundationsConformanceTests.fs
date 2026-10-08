/// Conformance: Chrona's web surface is built on the Echelon foundations the
/// way the other Echelon applications are, and the repository says so
/// truthfully. Each test fails if a foundation stops being consumed: pinned
/// to the release the `echelon-current` registry channel selects, installed,
/// and actually used (not merely listed), with no local copy standing in.
module Chrona.Tests.FoundationsConformanceTests

open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Xunit
open Chrona.Tests.Support

let private json (relative: string) = JsonNode.Parse(readRepoFile relative)

let private str (node: JsonNode) = node.GetValue<string>()

let private formaRelease =
    "https://github.com/kemiller2002/forma/releases/download/v0.4.1/echelon-foundry-design-system-0.4.1.tgz"

let private folioRelease =
    "https://github.com/kemiller2002/folio/releases/download/v0.3.0/echelon-foundry-print-components-0.3.0.tgz"

[<Fact>]
let ``foundations.json requires every foundation, at the echelon-current versions`` () =
    let capabilities = (json ".echelon/foundations.json").["capabilities"]

    for name, version in [ "aegis", "1.0.0"; "forma", "0.4.1"; "folio", "0.3.0"; "limen", "0.7.1" ] do
        Assert.True(capabilities.[name].["required"].GetValue<bool>(), $"{name} must be required")
        Assert.Equal(version, str capabilities.[name].["version"])

    Assert.Equal("aegis-boundaries.json", str capabilities.["aegis"].["boundaryManifest"])

[<Fact>]
let ``the npm foundations are pinned to immutable releases and locked`` () =
    let dependencies = (json "package.json").["dependencies"]
    Assert.Equal(formaRelease, str dependencies.["@echelon-foundry/design-system"])
    Assert.Equal(folioRelease, str dependencies.["@echelon-foundry/print-components"])
    Assert.Equal("0.7.1", str dependencies.["@echelon-foundry/limen"])

    let packages = (json "package-lock.json").["packages"]

    for name, version in
        [ "@echelon-foundry/design-system", "0.4.1"
          "@echelon-foundry/print-components", "0.3.0"
          "@echelon-foundry/limen", "0.7.1" ] do
        let locked = packages.[$"node_modules/{name}"]
        Assert.Equal(version, str locked.["version"])
        Assert.StartsWith("sha512-", str locked.["integrity"])
        // What is installed is what was pinned.
        Assert.Equal(version, str (json $"node_modules/{name}/package.json").["version"])

let private imports (relative: string) =
    Regex.Matches(readRepoFile relative, "@import \"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Seq.toList

[<Fact>]
let ``both pages take Forma and Folio from the installed packages`` () =
    // The kernel verification slice.
    Assert.Equal<string list>(
        [ "../node_modules/@echelon-foundry/design-system/dist/all.css"
          "../node_modules/@echelon-foundry/print-components/src/styles/print.css"
          "../web-kernel/page.css" ],
        imports "web/kernel-slice.css"
    )

    // The application: Forma, then the Chrona brand (Forma's compiler output),
    // then Folio, then Chrona's compositions.
    Assert.Equal<string list>(
        [ "../node_modules/@echelon-foundry/design-system/dist/all.css"
          "./brand/chrona.css"
          "../node_modules/@echelon-foundry/print-components/src/styles/print.css"
          "./chrona.css" ],
        imports "web/styles.css"
    )

    for css in [ "web/kernel-slice.css"; "web/styles.css" ] do
        // Folio's print stylesheet applies to print only.
        Assert.Contains("print.css\" print;", readRepoFile css)

        for imported in imports css do
            Assert.True(File.Exists(Path.GetFullPath(Path.Combine(repositoryRoot, "web", imported))), $"{imported} is not installed")

    let slice = readRepoFile "web/kernel-slice.html"
    Assert.Contains("<link rel=\"stylesheet\" href=\"./kernel-slice.css\" />", slice)
    Assert.Contains("<script type=\"module\" src=\"./kernel-slice.js\"></script>", slice)
    Assert.Contains("from \"../web-kernel/limen-wasm.js\"", readRepoFile "web/kernel-slice.js")

    let app = readRepoFile "web/index.html"
    Assert.Contains("<html lang=\"en\" data-ef-brand=\"chrona\">", app)
    Assert.Contains("<link rel=\"stylesheet\" href=\"./styles.css\" />", app)
    Assert.Contains("<script type=\"module\" src=\"./app.js\"></script>", app)
    Assert.Contains("from \"../web-kernel/limen-wasm.js\"", readRepoFile "web/app.js")

[<Fact>]
let ``the kernel registers Folio and starts Limen and its packs from the installed packages`` () =
    let kernel = readRepoFile "web-kernel/limen-wasm.js"

    for specifier in
        [ "../node_modules/@echelon-foundry/print-components/src/components/register.js"
          "../node_modules/@echelon-foundry/limen/dist/kernel/browser-kernel.js"
          "../node_modules/@echelon-foundry/limen/dist/capabilities/schedule/index.js"
          "../node_modules/@echelon-foundry/limen/dist/capabilities/environment/index.js" ] do
        Assert.Contains($"\"{specifier}\"", kernel)
        Assert.True(File.Exists(Path.GetFullPath(Path.Combine(repositoryRoot, "web-kernel", specifier))), specifier)

    // Chrona's own print pack is built on Limen's capability definition.
    let print = readRepoFile "web-kernel/print.js"
    let definition = "../node_modules/@echelon-foundry/limen/dist/kernel/capabilities.js"
    Assert.Contains($"\"{definition}\"", print)
    Assert.True(File.Exists(Path.GetFullPath(Path.Combine(repositoryRoot, "web-kernel", definition))), definition)
    Assert.Contains("import { printCapability } from \"./print.js\";", kernel)
    Assert.Contains($"\"{Chrona.Application.AppProtocol.print.Fingerprint}\"", print)

[<Fact>]
let ``the slice page composes Forma's components and Folio's document primitives`` () =
    let html = readRepoFile "web/kernel-slice.html"

    for marker in
        [ "<ef-fault-inline class=\"ef-component-tag\">"
          "class=\"ef-fault ef-fault--inline"
          "<ef-alert class=\"ef-component-tag\">"
          "class=\"ef-record-header"
          "class=\"ef-field\""
          "class=\"ef-status-lozenge\""
          "<ef-print-document"
          "<ef-print-table>"
          "<ef-print-page-number>" ] do
        Assert.True(html.Contains marker, $"web/kernel-slice.html lacks {marker}")

[<Fact>]
let ``the application page composes Forma's components`` () =
    let html = readRepoFile "web/index.html"

    for marker in
        [ "<ef-fault-inline class=\"ef-component-tag\">"
          "class=\"ef-fault ef-fault--inline"
          "class=\"ef-alert"
          "class=\"ef-surface"
          "class=\"ef-field\""
          "class=\"ef-select-field\""
          "class=\"ef-checkbox\""
          "class=\"ef-facts"
          "class=\"ef-status-lozenge\""
          "class=\"ef-empty-state"
          "class=\"ef-visually-hidden\""
          "class=\"ef-metric-card\""
          "<ef-print-document"
          "<ef-print-table>"
          "<ef-print-page-number>" ] do
        Assert.True(html.Contains marker, $"web/index.html lacks {marker}")

[<Fact>]
let ``nothing forks Forma or bypasses Limen's binding rules`` () =
    let sources =
        [ "web/index.html"; "web/styles.css"; "web/chrona.css"; "web/app.js"
          "web/kernel-slice.html"; "web/kernel-slice.css"; "web/kernel-slice.js"
          "web-kernel/page.css"; "web-kernel/limen-wasm.js"; "web-kernel/print.js" ]

    for source in sources do
        let text = readRepoFile source
        // No local design tokens and no restyled Forma components.
        Assert.False(Regex.IsMatch(text, @"--ef-[\w-]+\s*:"), $"{source} defines a Forma token")
        Assert.False(Regex.IsMatch(text, @"(^|[\s,}])\.ef-[\w-]+[^{;]*\{", RegexOptions.Multiline), $"{source} restyles a Forma component")
        // Forma's wrappers are inert; only Folio registers elements.
        Assert.DoesNotContain("customElements.define", text)
        // State cues are data-* attributes plus CSS: never inline or bound styles.
        Assert.DoesNotContain("data-bind-style", text)
        Assert.False(Regex.IsMatch(text, @"\sstyle="""), $"{source} has an inline style")

[<Fact>]
let ``Chrona's compositions use Forma's tokens, never literal colours`` () =
    let css = readRepoFile "web/chrona.css"
    // Comments may name colours; rules may not.
    let rules = Regex.Replace(css, @"/\*[\s\S]*?\*/", "")
    Assert.False(Regex.IsMatch(rules, @"#[0-9a-fA-F]{3,8}\b"), "web/chrona.css has a hex colour")
    Assert.False(Regex.IsMatch(rules, @"\b(rgb|rgba|hsl|hsla|hwb|lab|lch|oklab|oklch)\("), "web/chrona.css has a literal colour function")
    Assert.Contains("var(--ef-color-", rules)

[<Fact>]
let ``Aegis is referenced, pinned and its boundary codes are declared`` () =
    let project = readRepoFile "src/Chrona.Application/Chrona.Application.fsproj"
    Assert.Contains("<PackageReference Include=\"EchelonFoundry.Aegis.Core\" />", project)
    Assert.Contains("<PackageVersion Include=\"EchelonFoundry.Aegis.Core\" Version=\"1.0.0\" />", readRepoFile "Directory.Packages.props")

    let manifest = json "aegis-boundaries.json"
    Assert.Equal("aegis/boundaries/v1", str manifest.["schema"])
    Assert.Equal("Chrona", str manifest.["application"])

    let declared =
        manifest.["boundaries"].AsArray()
        |> Seq.collect (fun boundary -> boundary.["codes"].AsArray() |> Seq.map str)
        |> Set.ofSeq

    let used =
        Regex.Matches(readRepoFile "src/Chrona.Application/Boundary.fs", "\"(CHRONA\\.[A-Z_.]+)\"")
        |> Seq.map (fun m -> m.Groups[1].Value)
        |> Set.ofSeq

    Assert.NotEmpty used
    Assert.Equal<Set<string>>(used, declared)

[<Fact>]
let ``the Limen boundary names the F# engine and the browser kernel`` () =
    let boundary = (json "limen.config.json").["boundary"]
    let paths (name: string) = boundary.[name].AsArray() |> Seq.map str |> Seq.toList

    Assert.Null(boundary.["notApplicable"])
    Assert.Equal<string list>([ "src/Chrona.Engine"; "src/Chrona.Application" ], paths "engine")
    Assert.Equal<string list>([ "src/Chrona.Wasm"; "web"; "web-kernel" ], paths "kernel")

    for path in paths "engine" @ paths "kernel" do
        Assert.True(Directory.Exists(repoFile path), path)

[<Fact>]
let ``the engine answers the Limen contract the installed kernel offers`` () =
    let text = readRepoFile "node_modules/@echelon-foundry/limen/dist/generated/core.js"
    Assert.Contains($"\"{Chrona.Application.Limen.core.Id}\"", text)
    Assert.Contains($"\"{Chrona.Application.Limen.core.Fingerprint}\"", text)

[<Fact>]
let ``the TypeScript engine it replaces is gone, not kept beside it`` () =
    for retired in [ "verification/kernel-slice/src"; "verification/kernel-slice/test" ] do
        Assert.False(Directory.Exists(repoFile retired), $"{retired} still exists")

    let dependencies = (json "package.json").["devDependencies"]
    Assert.Null(dependencies.["typescript"])
