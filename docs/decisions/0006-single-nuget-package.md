# 0006 - Ship one NuGet package, keep three projects

- **Status:** Accepted
- **Date:** 2026-10-11

## Context

Host apps need HoistDotnet as a package from a public feed, so they can restore it without a token. Packing each project separately gives three packages, and none of them is useful alone: `Hoist.Core` has no endpoints, and `Hoist.Api` needs both of the others. Merging the projects into one would give one package, but it would also lose the compile-time check that `Hoist.Core` references no EF Core or ASP.NET.

On nuget.org, `Hoist.Api` is already taken by an unrelated package. A bare `Hoist` would read as an official package from the hoist-core authors.

## Decision

- Pack only `Hoist.Api`, as the package `HoistDotnet` on nuget.org, named after this repo (`hoist-dotnet`). `Hoist.Core.dll` and `Hoist.Infra.dll` ship inside it in `lib/net8.0`.
- `Hoist.Api` references Core and Infra with `PrivateAssets="all"`, so they are not package dependencies. A pack target adds their DLLs to the package.
- Each library's package references live in `Hoist.Core.Packages.props` and `Hoist.Infra.Packages.props`. The library and `Hoist.Api` both import them, so the package's dependency list has one source of truth.
- Assembly names and namespaces stay `Hoist.*`. Only the package ID differs.
- `.github/workflows/ci.yml` publishes on every push to `main` with nuget.org Trusted Publishing, so no long-lived API key is stored. The version is `VERSION` (major.minor) plus the workflow run number.
- The package is Apache-2.0, like hoist-core. `NOTICE` credits hoist-core and ships in the package.

## Consequences

- Add a package reference to the `*.Packages.props` file, not to `Hoist.Core.csproj` or `Hoist.Infra.csproj`, or it will be missing from the package's dependencies.
- A project that references `Hoist.Api` by project reference does not see Core and Infra transitively, so it must reference them too. `Hoist.SampleHost` does this, which matches what a package consumer sees.
- nuget.org versions cannot be deleted, only unlisted.
