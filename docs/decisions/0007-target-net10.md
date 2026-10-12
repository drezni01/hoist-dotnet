# 0007 - Target .NET 10, keep EF Core on 9

- **Status:** Accepted
- **Date:** 2026-10-12

## Context

.NET 10 is the current LTS release, and host apps are moving to it. HoistDotnet targeted `net8.0`.

EF Core 10 is not an option yet. The MySQL provider, `Pomelo.EntityFrameworkCore.MySql`, is at 9.0.0, and that version requires `Microsoft.EntityFrameworkCore.Relational [9.0.0, 9.0.999]`. Oracle's `MySql.EntityFrameworkCore` does support EF Core 10. Switching to it is a provider change. Its type mappings and configuration API differ from Pomelo's, and it would need revalidating against the live `xh_*` schema, so it is out of scope here.

## Decision

- Target `net10.0` only. `global.json` pins SDK 10.0.100 with `rollForward: latestFeature`.
- Move the ASP.NET Core and `Microsoft.Extensions.*` packages to 10.0.x.
- Keep EF Core (and the Sqlite and Design packages) on the latest 9.0.x, with Pomelo 9.0.0. EF Core 9 supports `net8.0` and later, so it runs on .NET 10.
- `Hoist.Core.Packages.props` skips its `Microsoft.Extensions.*` references in Web SDK projects. `Hoist.Api` gets them from the ASP.NET Core shared framework, and the .NET 10 SDK warns about the redundant references (NU1510).

## Consequences

- Host apps must target `net10.0` to use new HoistDotnet versions.
- A host app that uses EF Core in its own code must stay on EF Core 9 too, because NuGet resolves one EF Core version per app.
- Move EF Core to 10 when Pomelo ships an EF Core 10 release.
