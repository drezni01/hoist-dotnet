# Activity tracking, client errors and feedback

Ported from hoist-core **v42.1.0**.

| | Files |
|---|---|
| hoist-core | `grails-app/services/io/xh/hoist/track/{TrackService,TrackLoggingService}.groovy`, `src/main/groovy/io/xh/hoist/track/{ActivityTrackingConfig,TrackSeverity}.groovy`, `src/main/groovy/io/xh/hoist/browser/*`, `src/main/groovy/io/xh/hoist/util/StringUtils.groovy` (`elide`), `XhController.{track,recordMetrics,submitSpans}` |
| .NET | `src/Hoist.Core/Services/TrackService.cs`, `src/Hoist.Core/Utils/StringUtils.cs` (`Elide`, `BrowserUtils`), `src/Hoist.Infra/Database/TrackLogRepository.cs`, `src/Hoist.Api/Security/HttpTrackRequestInfo.cs` |
| Tests | `Contract/TestTrackEndpoints.cs`, `Unit/TestTrackHelpers.cs` |

hoist-react v88 has no separate error or feedback endpoints. Everything arrives through `xh/track`:

- **Client errors** have category `Client Error` and data `{error, userAlerted, userMessage?}`.
- **Feedback** has category `Feedback` and data `{userMessage}`.
- **Health reports** have category `App`.

## `xh/track` (`@AccessAll`, requires `clientUsername`)

The request is a POST with JSON body `{entries: [entry, ...]}` and `?clientUsername=`. The response is 204.

Before parsing, the raw body is HTML-escaped for `& < >` (`safeEncode`). A message such as `<b>` is therefore stored as `&lt;b&gt;`.

### Entry fields and how each row is filled

| Field | Notes |
|---|---|
| `msg` | Elided to 255 characters at a word boundary, ending in `...` |
| `category` | Defaults to `Default` |
| `severity` | `DEBUG`, `INFO`, `WARN` or `ERROR`, case-insensitive. Unknown values become `INFO`. |
| `data` | Serialized compact. Dropped (null) when longer than `maxDataLength` (default 2000). |
| `logData` | `true` or a list of data keys to include in the log line. Defaults to the config's `logData`. |
| `correlationId`, `tabId`, `loadId`, `clientAppCode` | Stored as-is |
| `timestamp` | Epoch ms, used as `dateCreated`. Defaults to now. |
| `elapsed` | ms. Dropped when it exceeds `maxElapsedMinsByCategory[category]` or `maxElapsedMins` (default 2). |
| `url` | Truncated to 500 characters |
| `appVersion` | Defaults to the server's `appVersion` |
| `username`, `impersonating` | If absent, taken from the session: the auth username, and the apparent user while impersonating |
| – | `instance` (`Hoist:InstanceName`), `appEnvironment`, `userAgent`, `browser` and `device` are taken from the request. `browser` and `device` are display names from the `Sec-Ch-UA` and `Sec-Ch-UA-Platform` hints or the User-Agent, for example `Chrome`, `Microsoft Edge`, `Windows`, `Mac`, `Unknown`. |

### What gets persisted

1. **Tracking switched off:** if `xhActivityTrackingConfig.enabled` is false, nothing happens.
2. **Persistence paused:** rows are not written when instance config `disableTrackLog` is `true`, or while the rate limit is active. The limit activates when more than `maxEntriesPerMin` entries arrive in a minute. It lifts after 2 compliant minutes.
3. **Severity filter:** the **first** rule in `levels` whose `username` and `category` match (each is `*` or a string that contains the value) sets the minimum severity persisted. The default is `[{username:'*', category:'*', severity:'INFO'}]`, so DEBUG entries are not stored.
4. **Client-error comment merge:** a `Client Error` entry can carry `userMessage`, `error` and `loadId`. If an earlier `Client Error` row in the last 15 minutes has the same user, the same `loadId`, an identical `data.error` and no `userMessage`, the comment is merged into that row instead of creating a new one.
5. **Logging and failures:** every entry is also logged through the `Hoist.TrackLog` logger category. Processing is asynchronous and never fails the request. A row that breaks a GORM constraint, such as `loadId` longer than 8 characters, is logged and dropped.

## Server-side tracking

`TrackService.TrackAsync(new TrackEntry {...})` records an entry from server code. Hoist uses it for impersonation, view and admin audit entries. `FlushAsync()` waits until queued entries are written.

## Stubs

`xh/recordMetrics` and `xh/submitSpans` check `clientUsername`, then return 204 and discard the payload. Metrics and tracing are out of scope. hoist-react only sends them when those features are enabled.

## Deliberate deviations

- Two hoist-core topics are not published, because pub/sub is out of scope: `xhTrackReceived` and `xhClientErrorCommented`. `ClientErrorEmailService` and `FeedbackEmailService` are not ported.
- `userAgent` is truncated to 255 characters, the column size, instead of failing the insert.
- Entries are written one batch at a time, so a client-error comment always sees the error row it follows.
