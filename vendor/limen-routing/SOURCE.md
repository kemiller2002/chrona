# Limen.Routing — interim copy

These files are Limen's own, copied verbatim from
[kemiller2002/limen](https://github.com/kemiller2002/limen) at commit
`e935da7d3160bc0c5f402b6c0e034771159aa363` (branch
`urlstate/wi-0168-semantics-fsharp`, Limen WI-0168). They give Chrona Limen's
URL-state semantics before Limen 0.9.0 releases them (DF-CHRONA-2026-0007).

| Path | What it is |
|---|---|
| `libraries/fsharp/Limen.Routing/` | the F# routing library Chrona's engine references |
| `conformance/routing/` | Limen's language-neutral vectors, their prose, and the F# runner |
| `LICENSE` | Limen's MIT licence |

Do not edit them. `limen-routing.lock` records each file's SHA-256, and
`PlacesTests` fails when a file differs. `Directory.Build.props` here builds
them as Limen does, without Chrona's repository-wide nullable and
documentation settings, so the files themselves stay unchanged.

CI runs the conformance runner:

```bash
dotnet run --project vendor/limen-routing/conformance/routing/fsharp/Limen.Routing.Conformance/Limen.Routing.Conformance.fsproj -c Release
```

## Replacing it with Limen 0.9.0

When Limen 0.9.0 is released and installed through Conditor:

1. Replace the `ProjectReference` to `Limen.Routing.fsproj` in
   `src/Chrona.Engine/Chrona.Engine.fsproj` with the released package.
2. Delete this directory, its two entries in `Chrona.sln`, and the
   conformance step in `.github/workflows/build.yml`.
3. Delete the lock test in `tests/Chrona.Tests/PlacesTests.fs`.
4. Run `dotnet test`. `PlacesTests` and the inventory test show any
   difference between the release and this copy.
