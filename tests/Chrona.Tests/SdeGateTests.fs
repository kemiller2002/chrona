/// SDE/Ordo validation in CI (WI-0079): `ordo verify --strict` runs on
/// exactly the commit, before anything is built, with the Ordo release
/// Conditor resolved.
module Chrona.Tests.SdeGateTests

open System.Text.Json
open System.Text.RegularExpressions
open Xunit
open Chrona.Tests.Support

let private workflow = lazy (readRepoFile ".github/workflows/sde-verify.yml")

/// The workflow's step names, in order.
let private steps () =
    Regex.Matches(workflow.Value, @"(?m)^      - name: (.+)$") |> Seq.map (fun m -> m.Groups[1].Value.Trim()) |> List.ofSeq

[<Fact>]
let ``CI verifies the SDE installation and structure strictly, on every push and pull request`` () =
    let text = workflow.Value
    Assert.Contains("verify --strict", text)
    Assert.Matches(@"(?m)^  push:", text)
    Assert.Matches(@"(?m)^  pull_request:", text)

[<Fact>]
let ``strict verification runs on a tree that is exactly the commit, before anything is built`` () =
    let text = workflow.Value
    let names = steps ()
    let clean = names |> List.findIndex (fun name -> name = "The working tree is exactly the commit")
    let verify = names |> List.findIndex (fun name -> name.StartsWith "Verify the SDE installation")
    Assert.True(clean < verify)
    Assert.Contains("git status --porcelain --ignored", text)
    // Nothing that builds or installs dependencies runs in this job.
    for tool in [ "dotnet "; "npm "; "npx "; "build:wasm"; "setup-dotnet"; "setup-node" ] do
        Assert.DoesNotContain(tool, text)

[<Fact>]
let ``the Ordo release is the one Conditor resolved, checked by its digest`` () =
    let text = workflow.Value
    Assert.Contains(".conditor/authority/resolved-release-set.json", text)
    Assert.Contains("sha256sum -c", text)
    // No version of Ordo is written into the workflow by hand.
    Assert.DoesNotMatch(@"ordo@?\s*v?\d+\.\d+\.\d+", text)

    use set = JsonDocument.Parse(readRepoFile ".conditor/authority/resolved-release-set.json")

    let ordo =
        set.RootElement.GetProperty("components").EnumerateArray()
        |> Seq.find (fun entry -> entry.GetProperty("systemId").GetString() = "ordo")

    use toolchain = JsonDocument.Parse(readRepoFile ".echelon/toolchain.json")
    Assert.Equal(toolchain.RootElement.GetProperty("ordo").GetString(), ordo.GetProperty("version").GetString())
