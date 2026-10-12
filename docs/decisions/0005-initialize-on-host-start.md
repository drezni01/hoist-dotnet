# 0005 - Initialize on host start, read settings at startup

- **Status:** Accepted. Amends the startup part of [0004](0004-hoist-in-host-pipeline.md).
- **Date:** 2026-10-11

## Context

Under [0004](0004-hoist-in-host-pipeline.md), hosts called `await app.Services.InitializeHoistAsync()` between `Build()` and `Run()`, and the `AddHoist` / `AddHoistJwtAuth` lambdas ran at registration time.

In our host app both caused trouble:

- The host's startup method is synchronous, so the call became `.GetAwaiter().GetResult()`.
- The host has two configuration pipelines. Its bootstrap code builds its own configuration, with user secrets in every DEBUG build. The host's configuration, which `Startup` sees, loads user secrets only in the `Development` environment. Lambdas that ran at registration read the host's configuration, so the JWT signing key could resolve to the `appsettings.json` placeholder instead of the secret.

## Decision

- `AddHoist` registers `HoistInitService`, an `IHostedLifecycleService`. It initializes Hoist in `StartingAsync`, which the host completes for every lifecycle service before any hosted service starts, including the web server. Requests cannot arrive before Hoist is ready, whatever the registration order. A failure stops the host.
- `InitializeHoistAsync` remains public and is idempotent. Calling it explicitly as well is harmless.
- The `AddHoist` and `AddHoistJwtAuth` lambdas run when their options are first resolved, which is during startup. `(options, IServiceProvider)` overloads let them read the host's services. The host takes the key from its own settings singleton, which its bootstrap code fills from the configuration that does have the secrets:

  ```csharp
  services.AddHoistJwtAuth((jwt, sp) => jwt.SigningKey = sp.GetRequiredService<MyAuthSettings>().SigningKey);
  ```

## Consequences

- Hosts write no initialization call.
- A missing or malformed key now fails at host start instead of at `AddHoistJwtAuth`. It still fails before any request is served.
- Settings that come only from the host's configuration still need that configuration to contain them. In our host, `Hoist:Database:ConnectionString` in user secrets needs the host's configuration to load user secrets in DEBUG builds.
