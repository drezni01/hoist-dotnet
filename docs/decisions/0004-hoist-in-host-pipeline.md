# 0004 - Run Hoist in the host's own pipeline

- **Status:** Accepted. Supersedes [0001](0001-embedded-hoist-server.md). Startup amended by [0005](0005-initialize-on-host-start.md).
- **Date:** 2026-10-11

## Context

[0001](0001-embedded-hoist-server.md) ran Hoist as a separate embedded `WebApplication`, with its own Kestrel listener and DI container, to isolate it from the host's filters, JSON settings and path base.

In use, the separate container was the main source of confusion. Anything the host provided had to be forwarded across: the user directory, the JWT key, the impersonation targets. That needed factories taking two service providers, and `Use*` methods that wrapped DI registrations. Hosts expect the usual ASP.NET shape instead: register services, add middleware, map endpoints.

The isolation can be achieved inside one pipeline:

- Hoist writes its own JSON, so a host's MVC JSON settings (for example, Newtonsoft) do not apply.
- `[AllowAnonymous]` on the Hoist controllers bypasses a host's global `AuthorizeFilter`. Hoist does its own authentication and access checks.
- An `[AcceptAnyContentType]` filter, being the `[Consumes]` closest to the action, overrides a host's global `[Consumes("application/json")]`. Without it, `xh/login` form posts would get 415.
- `UsePathBase` only strips requests that start with the prefix, so `xh/*` requests pass a host's path base (e.g. `/api`) unchanged.
- The Hoist middleware acts only on endpoints that carry `HoistEndpointAttribute`, so the host's own routes are untouched.

## Decision

Hoist registers in the host's container and pipeline:

```csharp
builder.Services.AddHoist(o => config.GetSection("Hoist").Bind(o));
builder.Services.AddHoistJwtAuth(jwt => config.GetSection("Hoist:Auth").Bind(jwt));
builder.Services.AddSingleton<IHoistUserService, MyUsers>();          // optional

var app = builder.Build();
await app.Services.InitializeHoistAsync();   // auth check, schema, built-in configs/prefs/roles
app.UseHoist();                              // after routing: Hoist errors, session, authentication
app.MapHoist();                              // Hoist controllers + JSON 404 for unknown Hoist routes
```

- **Settings are bound explicitly by the host.** Nothing reads configuration implicitly. Duplicating a value such as the signing key between the host's section and Hoist's is acceptable.
- **The auth provider is chosen explicitly** from a fixed set of `AddHoist<Provider>Auth` methods. Today the set is `AddHoistJwtAuth`. `InitializeHoistAsync` fails if none was chosen.
- **The user directory** is an ordinary DI registration of `IHoistUserService`. Who may be impersonated is the overridable `IHoistUserService.ImpersonationTargetsAsync`, as in hoist-core's `BaseUserService`.
- **Port.** `Hoist:Port`, when set, restricts the Hoist endpoints to that port through host metadata. The host must also listen on that port. When unset, Hoist answers on every port the host listens on.
- **CORS** is the host's responsibility.
- **Wiring mistakes are reported.** If `InitializeHoistAsync` was not called, Hoist requests get a 500 naming it. If `UseHoist` is missing, Hoist actions refuse to run, because no one would be authenticated.
- Hoist controllers are hidden from the host's API Explorer. Their Grails-style actions accept any HTTP method, which Swagger generators reject.

## Alternatives

- **Keep the embedded server and flatten its API.** This keeps the strongest isolation, but host services still have to be forwarded into a second container.
- **Combine routing, middleware and endpoints in one `UseHoist()`.** In a classic `Startup` host that already has `UseRouting` and `UseEndpoints`, a second routing step would run the host's endpoints before its authorization middleware. `UseHoist` and `MapHoist` stay separate, following the usual ASP.NET `Use*` / `Map*` pairing.

## Consequences

- One DI container. Host services are visible to Hoist without forwarding, and Hoist's services, such as `ConfigService`, are visible to the host.
- The host's global MVC filters other than authorization and `[Consumes]` apply to Hoist controllers. Global result filters, such as a global `Produces`, do not affect Hoist, because Hoist returns content results.
- Serving Hoist on 8080 next to a host on another port takes a host `Urls` entry plus `Hoist:Port`. It is no longer automatic.
