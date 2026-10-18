# Running HoistDotnet inside a host app

HoistDotnet replaces a hoist-core (Grails) server, usually on :8080, with Hoist running in your .NET app's own pipeline ([decision 0004](decisions/0004-hoist-in-host-pipeline.md)). A hoist-react client needs no changes when:

- It still calls `xh/*` endpoints on :8080.
- Its auth model sends the host's JWT as `Authorization: Bearer` on every request.
- It then calls `xh/login` with the matching username.

## 1. Reference the plugin

Add the `HoistDotnet` package from nuget.org to the host's API project. It needs no extra feed or token:

```xml
<PackageReference Include="HoistDotnet" Version="0.1.*" />
```

`HoistDotnet` contains `Hoist.Core`, `Hoist.Infra` and `Hoist.Api` ([decision 0006](decisions/0006-single-nuget-package.md)) and targets `net10.0`. It uses EF Core 9, because the Pomelo MySQL provider has no EF Core 10 release yet ([decision 0007](decisions/0007-target-net10.md)). Every push to hoist-dotnet's `main` publishes a new version (`.github/workflows/ci.yml`). The version is `VERSION` (major.minor) plus the workflow run number, e.g. `0.1.42`, and each release is tagged `v0.1.42`. Bump `VERSION` for a breaking change.

To work against a local checkout instead, reference `Hoist.Api`, `Hoist.Core` and `Hoist.Infra` as projects. Reference all three, because `Hoist.Api` references the other two privately.

## 2. Configure

Add a `Hoist` section to the host's `appsettings.json`, and make Kestrel also listen on 8080:

```json
"Urls": "http://localhost:5000;http://localhost:8080",
"Hoist": {
  "AppCode": "myapp",
  "AppName": "My App",
  "AppVersion": "0.0.1",
  "Environment": "Development",
  "Port": 8080,
  "Database": { "Provider": "MySql", "Migrate": "EnsureCreated" },
  "Auth": { "UsernameClaim": "unique_name" }
}
```

- **`Urls`** keeps the host's existing port and adds 8080. Use whatever the host listens on today in place of 5000.
- **`Hoist:Port`** restricts the Hoist endpoints to 8080, so they don't also appear on the host's own port.
- **`AppVersion`** must equal the client build's `appVersion`. Otherwise hoist-react refuses to start with "out of sync".
- **Secrets** go in user-secrets or the host's secret store: `Hoist:Database:ConnectionString`, the **dedicated Hoist schema** (the existing hoist-core schema, if you are replacing Grails), and `Hoist:Auth:SigningKey` unless step 3 takes the key from the host's own settings.
- **Schema check.** At startup Hoist checks that every `xh_*` table and column it maps exists. If one is missing, the host fails to start and the log names what is missing. Set `Hoist:Database:ValidateSchema` to `false` to bypass the check.
- **Logging.** Hoist logs through the host's logging setup, under `Hoist.*` categories, e.g. a Serilog `"Override": { "Hoist": "Information" }`. Hoist's SQL is not logged unless `Hoist:Database:LogSql` is `true`. The host's own SQL logging is unaffected.
- **Caching.** Hoist keeps configs, preferences, roles and JsonBlobs in memory and writes changes through to the database, so reads rarely query it. This assumes Hoist in this host is the only writer to the `xh_*` tables. If a Grails app or a second instance writes the same schema, set `Hoist:Database:CacheEntities` to `false`. See [0008](decisions/0008-write-through-entity-cache.md).
- **Time zone.** If the database was written by a JVM in a zone other than this machine's, set `Hoist:Database:TimeZone` to that zone.

## 3. Register services

In `Program.cs`, or `Startup.ConfigureServices`:

```csharp
services.AddHoist(o => Configuration.GetSection("Hoist").Bind(o));
services.AddHoistJwtAuth((jwt, sp) =>
{
    Configuration.GetSection("Hoist:Auth").Bind(jwt);
    jwt.SigningKey = sp.GetRequiredService<MyAuthSettings>().SigningKey;   // the key the host signs its tokens with
});

// Optional: the host's users as the Hoist user directory (Admin Console Users tab, impersonation targets).
// services.AddSingleton<IHoistUserService, MyHoistUserService>();
```

These lambdas run when the host starts, not when they are registered ([decision 0005](decisions/0005-initialize-on-host-start.md)). The `(options, IServiceProvider)` overload lets them read the host's own services, so the signing key needs to be configured only once.

The `Hoist` section itself, including `Hoist:Database:ConnectionString`, is read from the host's configuration. The default ASP.NET configuration loads user secrets only in the `Development` environment. If you run the host in another environment, add the user-secrets source explicitly or use another secret store.

Make sure the host's CORS policy allows the client's origin with credentials.

## 4. Add to the pipeline

Hoist initializes itself as the host starts, before the server accepts requests. It creates or validates the schema and seeds the built-in configs, prefs and roles, and a failure stops the host. There is no startup call to add.

Add `UseHoist()` after `UseRouting()`, and `MapHoist()` with the endpoints:

```csharp
app.UsePathBase(new PathString("/api"));          // if the host uses one
app.UseRouting();
app.UseHoist();                                   // Hoist session + authentication, Hoist endpoints only
app.UseAuthentication();
app.UseAuthorization();
...
app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapHoist();                         // Hoist controllers + JSON 404s
});
```

Common host-wide MVC settings and their effect on Hoist:

| Host setting | Effect on Hoist |
|---|---|
| Global `AuthorizeFilter` | Bypassed: Hoist controllers are `[AllowAnonymous]` and do their own checks |
| Global `Consumes("application/json")` | Overridden by Hoist's `[AcceptAnyContentType]`, so `xh/login` form posts work |
| Global `Produces("application/json")`, Newtonsoft | No effect: Hoist writes its own JSON |
| A path base such as `/api` | No effect on `:8080/xh/*` requests, which do not start with it |
| Swagger | Hoist controllers are hidden from API Explorer |

## 5. Users and roles

- **Users.** Without a host user service, Hoist's default user service knows only users who have logged in since startup. To list all of the host's users in the Admin Console Users tab and as impersonation targets, register an `IHoistUserService` over the host's user store. Hoist usernames must match the token's username claim (`Hoist:Auth:UsernameClaim`). Implement `IHoistUserProvisioner` too if users who are not in the host's store should be created on first login.
- **Impersonation targets.** By default these are all active users. To narrow them, override `ImpersonationTargetsAsync` in the `IHoistUserService`. Hoist still hides admins from non-admin impersonators.
- **Impersonation in the host's own API.** Hoist impersonation only changes what Hoist serves. For the host's API to act as the impersonated user too, the host issues the client a token for the target that carries an `impersonator` claim naming the real user. The client switches to that token while impersonating and back when impersonation ends. Check Hoist's rules with `IdentityService.CheckImpersonationAsync` before issuing it. See [auth.md, "Impersonation tokens"](api/auth.md#impersonation-tokens).
- **Roles.** These stay in `xh_role` / `xh_role_member` and are managed in the Admin Console Roles tab. Role claims in the host's JWT are separate. To use them as Hoist roles, register a custom `IRoleService`.
- **Usernames.** Hoist lowercases usernames (`Hoist:Auth:LowercaseUsernames`). If existing `xh_user_preference` rows were written with mixed-case usernames by a Grails server, set this option to `false`.

## 6. Verify

1. Stop the Grails server and start the host.
2. Run the client's end-to-end tests, or log in and open the Admin Console.
3. Before switching over, you can compare the two servers on the same database. While Grails still runs on 8080, run the host with Hoist on another port (`Urls` including 8090, and `Hoist:Port=8090`), then run `python -I scripts/compare-with-grails.py --grails http://localhost:8080 --dotnet http://localhost:8090 --username <you> --token <jwt>`. It only reads.
