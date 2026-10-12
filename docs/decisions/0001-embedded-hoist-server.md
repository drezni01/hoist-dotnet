# 0001 - Run Hoist as an embedded server on its own port

- **Status:** Superseded by [0004](0004-hoist-in-host-pipeline.md)
- **Date:** 2026-10-07

## Context

hoist-react expects a hoist-core server, by default on port 8080. We want that server inside our .NET host processes (for example, our main app backend) instead of as a separate Grails deployment.

The host apps configure ASP.NET Core in ways that break the hoist-core contract:

- Our host applies a global `AuthorizeFilter`. Hoist needs some endpoints, such as `xh/ping` and `xh/login`, to work before login.
- Our host uses Newtonsoft JSON with ISO dates. hoist-core writes dates as epoch milliseconds.
- Our host serves under its own path base, while hoist-react calls `/xh/...`.

MVC options, JSON settings and global filters apply to a whole application, so mapping Hoist's controllers into the host app would mean fighting the host's settings.

## Decision

`services.AddHoist(configuration)` registers a hosted service that builds and runs a separate `WebApplication` inside the host process. That embedded app has its own:

- Kestrel listener, `Hoist:Port`, default 8080
- DI container
- MVC options, restricted to the `Hoist.Api` assembly's controllers
- middleware pipeline

Its configuration sources are cleared, so a host's `Kestrel:Endpoints` or `ASPNETCORE_URLS` setting cannot change where Hoist listens. It logs through the host's `ILoggerFactory`.

The host passes services into the Hoist container with `HoistServerOptions.ConfigureServices((services, hostProvider) => ...)`. For example, a host forwards its own `IHoistUserService` this way.

## Alternatives

- **Map the Hoist endpoints into the host app, filtered by port** (`RequireHost("*:8080")`). This shares DI, but it also shares the host's global filters and JSON options. Rejected.
- **Run a separate process.** This defeats the purpose of the port.

## Consequences

- Hoist runs on a different port from the host API. That matches how hoist-react apps already work against Grails, so the client needs no changes.
- Host services are not visible to Hoist unless forwarded. That boundary is explicit and easy to test.
- Tests run the same embedded app on a `TestServer` (`tests/Hoist.Tests/Support/HoistTestHost.cs`).
