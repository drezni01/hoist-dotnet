# CLAUDE.md

HoistDotnet is a C# port of the parts of the hoist-core (Grails) server that hoist-react apps need: config, preferences, JsonBlob and views, authentication and roles, activity tracking, and the core Admin Console tabs. It runs as a plugin in a .NET host app's own DI container and pipeline. `Hoist:Port` optionally restricts it to one of the host's ports, such as 8080. It reuses the existing `xh_*` database schema through EF Core.

## Version pins

- hoist-core **v42.1.0** (`../hoist-core`) is the spec.
- hoist-react **v88.1.1** is the paired client. Use an installed copy of that version (`node_modules/@xh/hoist`) as the client spec, not a hoist-react checkout of another version. `CLAUDE.local.md` (gitignored) gives the local paths.
- `docs/PORT-MANIFEST.yaml` maps every ported area to its hoist-core and hoist-react sources and to our code, tests and docs. Update it in the same change as the code.

## Layout

The layout is a clean architecture. Dependencies point inward.

- **`src/Hoist.Core`**: domain models (`xh_*` entities), concrete services, interfaces for infrastructure, exceptions, `HoistOptions`. It references no other project.
- **`src/Hoist.Infra`**: EF Core `HoistDbContext` (MySQL via Pomelo; SQLite for tests), repositories, migrations, Jasypt-compatible encryption, instance config.
- **`src/Hoist.Api`**: the plugin entry points: `AddHoist`, `AddHoistJwtAuth` (the auth provider), `UseHoist` and `MapHoist`. Hoist initializes itself as the host starts. It also contains the controllers, middleware and JSON conventions. See `docs/decisions/0004-hoist-in-host-pipeline.md` and `0005-initialize-on-host-start.md`.
- **`src/Hoist.SampleHost`**: a sample host. It listens on :5080 and :8080, and Hoist answers on `Hoist:Port` (8080).
- **Packaging:** one NuGet package, `HoistDotnet`, is packed from `Hoist.Api` and bundles the Core and Infra DLLs. CI publishes it to nuget.org on every push to `main`. Core's and Infra's package references go in their `*.Packages.props` files, not in the csproj. See `docs/decisions/0006-single-nuget-package.md`.
- **`tests/Hoist.Tests`**: MSTest. The test folders are:
  - `Contract/`: HTTP contract tests on a TestServer with in-memory SQLite.
  - `Unit/`: service unit tests.
  - `Schema/`: EF model tests.
  - `LiveDb/`: `TestCategory=LiveDb`. These need a real MySQL and are skipped without one.

## Working rules

- **Test first.** For any endpoint change:
  1. Update `docs/api/<area>.md`.
  2. Write a contract test that asserts the exact JSON keys and types.
  3. Implement until the test passes.
- **Match hoist-core behaviour exactly**, including the quirks: falsy error fields dropped, 204 for `renderSuccess`, dates as epoch milliseconds, and parameters bound from query and form together. Record any intentional deviation in the area doc under "Deliberate deviations".
- **Cite the hoist-core source** (file and method) in each area doc, so the sync skill can trace changes.
- **Code style:**
  - `Nullable` disabled; block-scoped namespaces.
  - camelCase private fields with no underscore, assigned as `this.x = x`.
  - `Async` suffix on async methods.
  - Core services are concrete classes without interfaces.
  - EF mapping goes inline in `OnModelCreating`.
- **Decisions** go in `docs/decisions/NNNN-*.md` and are append-only.
- Do not commit or push unless asked.

## Commands

```bash
dotnet build
dotnet test                                            # excludes nothing; LiveDb tests self-skip without a connection string
dotnet test --filter "FullyQualifiedName~TestConfig"
dotnet test --filter TestCategory=LiveDb               # needs user-secrets Hoist:Database:ConnectionString (tests project)
dotnet run --project src/Hoist.SampleHost              # host :5080, Hoist :8080
./scripts/dump-hoist-schema.ps1 -Schema hoist -User hoist  # refresh docs/schema DDL from a hoist-core MySQL database
python -I scripts/hoist-core-diff.py --from v42.1.0 --to v43.0.0   # map a hoist-core diff onto ported components
python -I scripts/compare-with-grails.py --grails ... --dotnet ... --username u --token t   # read-only parity check
```

## Upgrading to a new hoist-core release

Use the `hoist-core-sync` skill (`.claude/skills/hoist-core-sync/SKILL.md`). It runs `scripts/hoist-core-diff.py`, reviews the changelog and upgrade notes, and updates docs, tests, code and the manifest pins, test-first.

## Hosting in an app

See `docs/host-integration.md` for wiring `services.AddHoist(...)` into a host app, and `src/Hoist.SampleHost/Program.cs` for a minimal host.

## Public repo

This repo and the `HoistDotnet` package are public. Keep it free of references to specific host apps and of machine-specific paths: write "the host app", and put local paths in `CLAUDE.local.md`.
