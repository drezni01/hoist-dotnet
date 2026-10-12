# HoistDotnet

HoistDotnet is a .NET 10 implementation of the parts of the [hoist-core](https://github.com/xh/hoist-core) server that [hoist-react](https://github.com/xh/hoist-react) apps need to run. It is a library that a .NET host app adds to its own services and pipeline, so you no longer need a separate Grails/JVM deployment.

```csharp
builder.Services.AddHoist(o => config.GetSection("Hoist").Bind(o));
builder.Services.AddHoistJwtAuth(jwt => config.GetSection("Hoist:Auth").Bind(jwt));   // the auth provider

var app = builder.Build();
app.UseHoist();   // Hoist session and authentication, Hoist endpoints only
app.MapHoist();   // Hoist endpoints
```

Hoist initializes as the host starts, before it accepts requests: schema check, built-in configs, prefs and roles. The configure lambdas run then too, so they see the complete configuration, including user secrets.

To serve Hoist on 8080, where hoist-react expects it, while the host keeps its own port, have Kestrel listen on both and set `Hoist:Port` to 8080. See [decision 0004](docs/decisions/0004-hoist-in-host-pipeline.md) and [src/Hoist.SampleHost/Program.cs](src/Hoist.SampleHost/Program.cs).

**Target versions:** hoist-core **v42.1.0** and hoist-react **v88**. `docs/PORT-MANIFEST.yaml` records the exact source commit for every ported area.

## What is ported

| Area | Endpoints | Docs |
|---|---|---|
| Core | `xh/ping`, `xh/version`, `xh/getTimeZoneOffset`, `xh/echoHeaders` | [core](docs/api/core.md) |
| Auth and identity | `xh/authStatus`, `login`, `logout`, `authConfig`, `getIdentity`, impersonation | [auth](docs/api/auth.md) |
| Roles | `DefaultRoleService` over `xh_role` | [roles](docs/api/roles.md) |
| Config | `xh/getConfig`, typed configs, `pwd` (Jasypt-compatible) | [config](docs/api/config.md) |
| Environment | `xh/environment`, `environmentPoll`, alert banner | [environment](docs/api/environment.md) |
| Preferences | `xh/getPrefs`, `setPrefs`, `unsetPrefs`, `clearUserState` | [prefs](docs/api/prefs.md) |
| Activity tracking | `xh/track` (activity, client errors, feedback) | [tracking](docs/api/tracking.md) |
| JsonBlob | `xh/*JsonBlob` | [jsonblob](docs/api/jsonblob.md) |
| Views | `xhView/*` (ViewManager) | [views](docs/api/views.md) |
| Admin Console | Config, Prefs, User Prefs, JSON Blobs, Users, Roles, Activity, Alert Banner | [admin](docs/api/admin.md) |

**Not ported:** clustering, websockets, metrics, tracing, monitors, the log viewer, email, LDAP/Entra and grid export. `xh/recordMetrics` and `xh/submitSpans` are accepted and discarded. The other unported endpoints return 404.

## Layout

The solution follows clean-architecture conventions. See [CLAUDE.md](CLAUDE.md) for the conventions and commands.

| Project | Contents |
|---|---|
| `src/Hoist.Core` | Domain, services, interfaces |
| `src/Hoist.Infra` | EF Core over the existing `xh_*` schema (MySQL; SQLite for tests), Jasypt, instance config |
| `src/Hoist.Api` | The plugin entry point (`AddHoist`, `AddHoistJwtAuth`, `UseHoist`, `MapHoist`), controllers, middleware |
| `src/Hoist.SampleHost` | A host app that uses the plugin |
| `tests/Hoist.Tests` | Contract, unit, schema and live-DB tests (MSTest) |

## Database

Hoist keeps its tables in a dedicated schema with its own connection string (`Hoist:Database:ConnectionString`). It can share that schema with an existing hoist-core app:

- The table and column mapping, optimistic locking and `pwd` encryption all match GORM/Hibernate.
- An empty schema is created automatically.
- At startup the schema is checked against the model. A missing table or column is logged and stops startup.

See [docs/schema](docs/schema/README.md) and [decision 0002](docs/decisions/0002-hoist-schema.md).

## Upgrading hoist-core

`scripts/hoist-core-diff.py` maps a hoist-core version diff onto the ported components. The `hoist-core-sync` skill (`.claude/skills/hoist-core-sync`) then walks through the update, test-first.

## Install

```bash
dotnet add package HoistDotnet
```

`HoistDotnet` is published to nuget.org on every push to `main`. See [docs/host-integration.md](docs/host-integration.md) for wiring it into a host.

## License

Apache License 2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE). HoistDotnet ports parts of [hoist-core](https://github.com/xh/hoist-core), © Extremely Heavy Industries, Inc., which is also Apache 2.0.
