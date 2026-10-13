/// The F# assemblies depend in one direction (0.4, WI-0040).
module Chrona.Tests.ArchitectureTests

open System.IO
open System.Text.RegularExpressions
open Xunit
open Chrona.Tests.Support

/// The Chrona projects a project references, by name.
let private references (project: string) =
    Regex.Matches(readRepoFile project, "ProjectReference Include=\"[^\"]*?([A-Za-z.]+)\\.(?:fs|cs)proj\"")
    |> Seq.map _.Groups[1].Value
    |> Set.ofSeq

/// The packages a project references.
let private packages (project: string) =
    Regex.Matches(readRepoFile project, "PackageReference Include=\"([^\"]+)\"") |> Seq.map _.Groups[1].Value |> Set.ofSeq

[<Fact>]
let ``the domain depends on no other Chrona assembly`` () =
    Assert.Empty(references "src/Chrona.Domain/Chrona.Domain.fsproj")

[<Fact>]
let ``the engine depends on the domain alone`` () =
    Assert.Equal<Set<string>>(set [ "Chrona.Domain" ], references "src/Chrona.Engine/Chrona.Engine.fsproj")

[<Fact>]
let ``the application sits on the engine and the receiver-owned integration contract`` () =
    Assert.Equal<Set<string>>(set [ "Chrona.Engine"; "Chrona.Integration" ], references "src/Chrona.Application/Chrona.Application.fsproj")

[<Fact>]
let ``the browser assembly hosts the application and nothing below it directly`` () =
    Assert.Equal<Set<string>>(set [ "Chrona.Application" ], references "src/Chrona.Wasm/Chrona.Wasm.csproj")

[<Fact>]
let ``the integration contract is transport-independent: no Chrona assembly and no package`` () =
    Assert.Empty(references "src/Chrona.Integration/Chrona.Integration.fsproj")
    Assert.Empty(packages "src/Chrona.Integration/Chrona.Integration.fsproj")

[<Fact>]
let ``the Summa mapping depends on the domain alone`` () =
    Assert.Equal<Set<string>>(set [ "Chrona.Domain" ], references "src/Chrona.Summa/Chrona.Summa.fsproj")

[<Fact>]
let ``every Chrona assembly is checked here`` () =
    let projects =
        Directory.GetDirectories(repoFile "src")
        |> Seq.map Path.GetFileName
        |> Seq.filter (fun name -> name.StartsWith "Chrona.")
        |> Set.ofSeq

    Assert.Equal<Set<string>>(
        set [ "Chrona.Application"; "Chrona.Domain"; "Chrona.Engine"; "Chrona.Integration"; "Chrona.Summa"; "Chrona.Wasm" ],
        projects
    )
