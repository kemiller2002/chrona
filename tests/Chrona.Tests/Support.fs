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
