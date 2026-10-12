# Core conventions and endpoints

Ported from hoist-core **v42.1.0**. This page covers the conventions every endpoint shares, then the bootstrap endpoints that need no area of their own.

## Shared conventions

### Routing

hoist-core source: `grails-app/controllers/io/xh/hoist/UrlMappings.groovy`.

- `/{controller}/{action}` maps to `XxxController.action()` for any HTTP method. The .NET port uses explicit attribute routes such as `[Route("xh/getConfig")]` and does not restrict the HTTP method, matching Grails.
- `/rest/{controller}[/{id}]` maps the HTTP method to an action: POST is `create`, GET is `read`, PUT and PATCH are `update`, DELETE is `delete`. `/rest/{controller}/bulkUpdate`, `/bulkDelete` and `/lookupData` are separate routes. See [admin.md](admin.md).
- Unmatched routes return a 404 `NotFoundException` (`xh/notFound`).

### Parameters

Grails `params` merges the query string and the form-encoded body. hoist-react sends `fetchJson({params})` as GET with a query string, and POST params as a form body. `postJson({body, params})` sends a JSON body with params on the query string.

**In .NET:** controllers derive from `BaseController` and are **not** `[ApiController]`, so simple action parameters bind from route, query and form. Read JSON bodies explicitly with `ParseRequestJsonAsync()` or `ParseRequestJsonArrayAsync()`. With `safeEncode`, the raw body is HTML-escaped for `& < >` first; only `xh/track` does this.

### JSON rendering

hoist-core source: `src/main/groovy/io/xh/hoist/json/JSONSerializer.java`. .NET source: `src/Hoist.Api/Json/HoistJson.cs`.

| Type | Wire format |
|---|---|
| `Date`, `Instant` (.NET `DateTime`, `DateTimeOffset`) | epoch milliseconds (number) |
| `LocalDate` (.NET `DateOnly`) | `"yyyy-MM-dd"` |
| `Double` NaN or Infinity | `null` |
| `null` | written. A null top-level value renders the body `null`. |
| map keys | unchanged (no camel-casing) |

- Content type is `application/json; charset=UTF-8`.
- `renderSuccess()` returns **204** with an empty body.

### Errors

hoist-core source: `exception/ExceptionHandler.groovy`, `json/serializer/ThrowableSerializer.groovy`. .NET source: `Middleware/HoistExceptionMiddleware.cs`, `Hoist.Core/Exceptions`.

Body: `{name, message, cause, isRoutine}`. Falsy values are dropped, so `isRoutine` only appears when it is true and `cause` only when there is an inner exception. `traceId` is never sent because tracing is out of scope.

| Exception | Status | isRoutine |
|---|---|---|
| `NotAuthenticatedException` | 401 | true |
| `NotAuthorizedException` | 403 | true |
| `NotFoundException` | 404 | false |
| `RoutineRuntimeException`, `DataNotAvailableException`, `SessionMismatchException`, `ValidationException` | 400 | true |
| any other exception | 500 | false |

Routine exceptions are logged at Debug, all others at Error.

**Exception:** authentication failures raised inside the auth middleware (see [auth.md](auth.md)) set only the status and write **no body**.

## Endpoints

| Route | Access | Response | Source |
|---|---|---|---|
| `xh/ping`, `/ping` | whitelisted | `{appCode, timestamp: ms, success: true}` | `XhController.ping` |
| `xh/version` | whitelisted | `{appCode, appVersion, appBuild}` | `XhController.version` |
| `xh/getTimeZoneOffset` | `@AccessAll` | `timeZoneId` (Java/IANA id) → `{offset: ms}` for now, including DST. An unknown id returns 404 `Unknown timeZoneId`. | `XhController.getTimeZoneOffset` |
| `xh/echoHeaders` | `@AccessAll` | `{headerName: value}` for every request header | `XhController.echoHeaders` |
| *(unmatched `xh/*`, `xhView/*`, `rest/*`, `*Admin/*`)* | any | 404 `{name: "NotFoundException", message: "Not Found"}` | `XhController.notFound` |

- Tests: `tests/Hoist.Tests/Contract/TestCoreEndpoints.cs`, `Contract/TestImpersonationEndpoints.cs` (getTimeZoneOffset, echoHeaders)
- .NET source: `src/Hoist.Api/Controllers/XhController.cs`

## Deliberate deviations

- `traceId` is never included in error bodies.
- Hoist runs in the host app's pipeline. The host decides the listen ports; `Hoist:Port` only restricts the Hoist endpoints to one of them.
- The JSON 404 covers only the Hoist route prefixes listed above. Other unmatched paths are left to the host, because the host's own routes share the server.
