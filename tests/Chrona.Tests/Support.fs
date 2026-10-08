module Chrona.Tests.Support

open System.IO

/// The repository root: the directory holding Chrona.sln.
let repositoryRoot =
    let rec up (directory: DirectoryInfo) =
        match directory with
        | null -> failwith "Chrona.sln not found above the test assembly"
        | d when File.Exists(Path.Combine(d.FullName, "Chrona.sln")) -> d.FullName
        | d -> up d.Parent

    up (DirectoryInfo(System.AppContext.BaseDirectory))

let repoFile (relative: string) = Path.Combine(repositoryRoot, relative)

let readRepoFile (relative: string) = File.ReadAllText(repoFile relative)

/// The reference data the domain tests classify against: every id they
/// assign, active.
let references =
    let open' = Chrona.Domain.Reference.empty "ORG-1"

    [ Chrona.Domain.Reference.Project, "PRJ-1"
      Chrona.Domain.Reference.Project, "PRJ-2"
      Chrona.Domain.Reference.Project, "PRJ-X"
      Chrona.Domain.Reference.ActivityType, "ACT-DEV"
      Chrona.Domain.Reference.Client, "CLI-1"
      Chrona.Domain.Reference.Tag, "backend" ]
    |> List.fold
        (fun catalogue (kind, id) ->
            match Chrona.Domain.Reference.execute catalogue (Chrona.Domain.Reference.Add(kind, id, id)) with
            | Ok next -> next
            | Error problems -> failwith $"{problems}")
        open'
