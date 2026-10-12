# Authentication, identity and access

This page is ported from hoist-core **v42.1.0**:

| hoist-core source | What it covers |
|---|---|
| `src/main/groovy/io/xh/hoist/HoistFilter.groovy` | Request pipeline |
| `src/main/groovy/io/xh/hoist/security/BaseAuthenticationService.groovy` | `allowRequest`, whitelist, `setUser` |
| `grails-app/services/io/xh/hoist/user/IdentityService.groovy` | Session identity, `getClientConfig` |
| `grails-app/controllers/io/xh/hoist/HoistInterceptor.groovy` and `security/Access*.groovy` | Access annotations |
| `src/main/groovy/io/xh/hoist/user/{HoistUser,BaseUserService}.groovy` | Users |
| `grails-app/controllers/io/xh/hoist/impl/XhController.groovy` | `authConfig`, `authStatus`, `getIdentity`, `login`, `logout` |

| .NET source | What it covers |
|---|---|
| `src/Hoist.Api/Middleware/HoistAuthMiddleware.cs` | `HoistFilter` + `allowRequest` |
| `src/Hoist.Api/Security/{HoistSession,BaseAuthenticationService,JwtBearerAuthenticationService,AccessAttributes}.cs` | Session, auth services, access attributes |
| `src/Hoist.Core/Services/{IdentityService,DefaultUserService}.cs` | Identity, default user directory |
| `src/Hoist.Core/Models/HoistUser.cs` | User model |

Tests are in `Contract/TestAuthEndpoints.cs`. Host wiring is tested in `Contract/TestHoistHosting.cs`.

## Request pipeline

These steps run in `UseHoist`'s middleware, and only for Hoist endpoints (marked with `HoistEndpointAttribute`). The host's own endpoints are not affected.

1. **Load the session.** The identity (apparent and authenticated username) is read from the encrypted `xh-identity` cookie. This replaces the servlet session attribute `xhIdentity`.
2. **Allow known users.** If the authenticated user resolves through `IHoistUserService.FindAsync`, the request continues.
3. **Allow whitelisted paths.** Otherwise, the request continues if its path ends with one of `/ping`, `/xh/login`, `/xh/logout`, `/xh/ping`, `/xh/version` or `/xh/authConfig`. `/xh/authStatus` is deliberately not whitelisted.
4. **Complete authentication.** Otherwise, `IHoistAuthenticationService.CompleteAuthenticationAsync` runs.
   - If it returns false, the request stops. If the service set no status, it is 401.
   - If it returns true but no user was established, the result is 401 (`NotAuthenticatedException`).
5. **Failures have no body.** Any exception during these steps sets only the status: the `HoistException` status code, or 500. Nothing is written to the response.
6. **Access check.** The controller's access attributes are checked against the **apparent** user. A failure throws `NotAuthorizedException` → 403 **with** an error body: `You do not have the required role(s) for this action. Currently logged in as: x.`

| Attribute | hoist-core | Rule |
|---|---|---|
| `[AccessAll]` | `@AccessAll` | any authenticated user |
| `[AccessRequiresRole("R")]` | `@AccessRequiresRole` | has R |
| `[AccessRequiresAnyRole(...)]` | `@AccessRequiresAnyRole` | has any |
| `[AccessRequiresAllRoles(...)]` | `@AccessRequiresAllRoles` | has all |
| none | – | denied |

An attribute on the action takes precedence over one on the controller.

## Endpoints

| Route | Access | Request | Response |
|---|---|---|---|
| `xh/authConfig` | whitelisted | – | `IHoistAuthenticationService.GetClientConfig()`, default `{}` |
| `xh/authStatus` | auth required (a failed auth returns 401 with no body) | – | `{authenticated: bool, identity: <identity or null>}` |
| `xh/getIdentity` | `@AccessAll` | – | `<identity>`, or `null` |
| `xh/login` | whitelisted | `username`, `password` (form or query) | `{success: bool, identity: <identity or null>}` |
| `xh/logout` | whitelisted | – | `{success: bool}`. On success the session cookie is cleared. |

The `<identity>` object is `IdentityService.getClientConfig` and takes one of two shapes:

- **Normal:** `{user: <user>, roles: [string]}`
- **Impersonating:** `{apparentUser, apparentUserRoles, authUser, authUserRoles}`

`<user>` is `HoistUser.formatForJSON`: `{username, email, displayName, active}`. A host can add fields by subclassing `HoistUser` and overriding `FormatForJson`. Roles are sorted.

## Impersonation

**hoist-core:** `IdentityService.{impersonate,endImpersonate}`, `BaseUserService.impersonationTargetsForUser`, `XhController.{impersonationTargets,impersonate,endImpersonate}`. **Tests:** `Contract/TestImpersonationEndpoints.cs`, `Contract/TestHoistHosting.cs`.

| Route | Request | Response |
|---|---|---|
| `xh/impersonationTargets` | – | `[{username}]`. Empty unless the auth user has `HOIST_IMPERSONATOR`. Otherwise the active users from `IHoistUserService.ImpersonationTargetsAsync(authUser)` (default: `ListAsync(true)`). Admins get all of them; other impersonators get only non-admins. |
| `xh/impersonate` | `username` | 204. The session becomes `(apparent = target, auth = unchanged)`. |
| `xh/endImpersonate` | – | 204. The session becomes `(auth, auth)`. |

`xh/impersonate` fails with a **500** `HoistException`, as hoist-core's RuntimeExceptions do, in these cases:

| Condition | Message |
|---|---|
| `xhEnableImpersonation` is not true | `Impersonation is disabled for this app.` |
| The auth user is missing or lacks `HOIST_IMPERSONATOR` | `User not found or user not authorized for impersonation.` |
| The target user is unknown | `User 'x' not found` |
| The target is not in the auth user's impersonation targets | `'a' is not authorized to impersonate 'x'` |

**Behaviour while impersonating:**

- A current impersonation is ended (and tracked) before a new one starts.
- Each change is tracked with category `Impersonate`, severity `WARN`, message `Started impersonation` or `Stopped impersonation`, and data `{target}`.
- Access checks, preferences, views and the `clientUsername` checks all use the **apparent** user. `lastUpdatedBy` and the tracking `username` use the **authenticated** user.

Users returned by `ImpersonationTargetsAsync` must also be found by `FindAsync`, because the apparent user is resolved through it on every request.

`HOIST_IMPERSONATOR` is granted through role inheritance from `HOIST_ADMIN` DB membership. `Hoist:BootstrapAdminUser` does **not** confer it, matching hoist-core.

## Auth provider: Jwt

Selected with `AddHoistJwtAuth`. `JwtBearerAuthenticationService` mirrors the usual Grails pass-through `AuthenticationService` for a JWT-based host, but it validates the token in-process instead of calling back to the token issuer.

- **Token source.** The client sends `Authorization: Bearer <jwt>`. The client app's auth model adds this header to every request.
- **`CompleteAuthenticationAsync`.** Validates the token and establishes its user. With no token, or an invalid one, it sets 401 and returns false.
- **`LoginAsync(username, password)`.** Validates the token and requires `username` to match the token's user. The password is ignored, so a client can send a placeholder such as `*****`.
- **`LogoutAsync`.** Returns true, so the session is cleared.

Its settings are `HoistJwtOptions`, which the host binds from a section of its choice (the sample host uses `Hoist:Auth`):

| Setting | Default | Meaning |
|---|---|---|
| `SigningKey` | – (required) | Base64 HS256 key, the same one the token issuer uses |
| `Issuer`, `Audience` | – | Checked only when set |
| `UsernameClaim` | `unique_name` | Becomes the username, lowercased when `LowercaseUsernames` is true (the default) |
| `EmailClaim` | `email` | Becomes the email |
| `DisplayNameClaim` | `name` | Becomes the displayName, falling back to the username |

A missing or malformed `SigningKey` fails in `AddHoistJwtAuth` itself, at startup.

The user comes from `IHoistUserProvisioner.GetOrCreateAsync(userFromClaims)` when the registered user service implements it. Otherwise it comes from `IHoistUserService.FindAsync`. An inactive user gets a 403 `NotAuthorizedException("'x' is not an active user.")`.

### Impersonation tokens

Hoist impersonation only changes what Hoist serves. For a host's own API to act as the target too, the host issues the client a token for the target user that carries an `impersonator` claim (`HoistJwtOptions.ImpersonatorClaim`) naming the real user. The host exposes this as its own endpoint, e.g. `POST /api/login/impersonate`.

- **Before issuing,** the host calls `IdentityService.CheckImpersonationAsync(authUser, targetUsername)`. These are the same checks as `xh/impersonate`, without a Hoist session: impersonation enabled, `authUser` an impersonator, and the target among their targets. It returns the target or throws.
- **When such a token establishes a Hoist session,** either on a request without a session or through `xh/login`, the session is the impersonation: apparent = the token's user, auth = the impersonator. The rules are checked again first. If they no longer allow it (impersonation disabled, roles changed), the token is refused with 401. So a session that expires during an impersonation is rebuilt as the impersonation, not as the target's own session.

Tests: `Contract/TestImpersonationTokens.cs`.

## Users

`IHoistUserService` ports `BaseUserService`: `FindAsync`, `ListAsync`, and the overridable `ImpersonationTargetsAsync`. The default `DefaultUserService` keeps every user who has authenticated, built from their token claims, in memory. Usernames that appear as USER members of a role are also known.

## Configuring authentication

Everything is explicit, in the host's own DI container ([decision 0004](../decisions/0004-hoist-in-host-pipeline.md)):

```csharp
builder.Services.AddHoist(o => config.GetSection("Hoist").Bind(o));
builder.Services.AddHoistJwtAuth(jwt => config.GetSection("Hoist:Auth").Bind(jwt));   // the auth provider
builder.Services.AddSingleton<IHoistUserService, MyHoistUsers>();                      // optional: the user directory
```

- **Auth provider.** Exactly one `AddHoist<Provider>Auth` call. The set of providers is fixed; today it is `AddHoistJwtAuth`. Startup fails if none was called.
- **Settings are read at startup.** The configure lambdas run when the host starts, not when they are registered. The `(options, sp)` overloads can also read the host's services, e.g. to take the JWT key from the host's own settings: `services.AddHoistJwtAuth((jwt, sp) => jwt.SigningKey = sp.GetRequiredService<MyAuthSettings>().SigningKey)`.
- **User directory.** Register an `IHoistUserService` before or after `AddHoist`. Either way it replaces `DefaultUserService`. If it also implements `IHoistUserProvisioner`, users first seen in a token are passed to it; otherwise only users `FindAsync` knows can sign in.
- **Impersonation targets.** Override `ImpersonationTargetsAsync` in your `IHoistUserService`.

A host that needs a scheme outside the fixed set can register its own `IHoistAuthenticationService` (usually a `BaseAuthenticationService` subclass, calling `SetUser(ctx, user)` on success) instead of calling a provider method.

## Deliberate deviations

- **Session storage.** The session is an encrypted cookie (ASP.NET Data Protection), not a server-side `HttpSession`. Keys persist in the default Data Protection location. A multi-instance deployment needs a shared key ring.
- **Impersonation filter always applies.** In hoist-core an app overrides `impersonationTargetsForUser` and is only *encouraged* to call the base filter. Here `ImpersonationTargetsAsync` supplies candidates, and `IdentityService` always applies the impersonator and admin checks on top, so an override cannot let a non-admin impersonate an admin.
- **Unknown session user.** A session whose user is no longer known (for example, after a restart with the in-memory `DefaultUserService`) is treated as absent. The Bearer token then re-establishes it.
