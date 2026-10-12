# Environment and alert banner

Ported from hoist-core **v42.1.0**.

| | Files |
|---|---|
| hoist-core | `grails-app/services/io/xh/hoist/environment/EnvironmentService.groovy`, `grails-app/services/io/xh/hoist/alertbanner/AlertBannerService.groovy`, `src/main/groovy/io/xh/hoist/environment/EnvPollConfig.groovy`, `src/main/groovy/io/xh/hoist/alertbanner/AlertBannerConfig.groovy` |
| .NET | `src/Hoist.Core/Services/{EnvironmentService,AlertBannerService}.cs`, `src/Hoist.Infra/Database/DatabaseInfo.cs` |
| Tests | `Contract/TestEnvironmentEndpoints.cs` |

## `xh/environment` (`@AccessAll`)

hoist-react calls this endpoint at startup. Startup **fails** in two cases:

- `appVersion` is not exactly the client build's `xhAppVersion`. Set `Hoist:AppVersion` to match.
- `hoistCoreVersion` is below hoist-react's `MIN_HOIST_CORE_VERSION`, which is 40.5.0 in v88.

| Key | Value |
|---|---|
| `appCode`, `appName`, `appVersion`, `appBuild` | `Hoist:*` options |
| `appEnvironment` | `Hoist:Environment`, one of `Production`, `Beta`, `Staging`, `Development`, `Test`, `UAT`, `BCP` |
| `grailsVersion` | `null` |
| `hoistCoreVersion` | `"42.1.0"`, the contract version this port implements |
| `javaVersion` | the .NET runtime description |
| `serverTimeZone`, `serverTimeZoneOffset` | local zone as an IANA id, and its current offset in ms |
| `appTimeZone`, `appTimeZoneOffset` | the `xhAppTimeZone` config, falling back to UTC when invalid (the error is logged); offset in ms |
| `webSocketsEnabled` | `false`, because websockets are not ported |
| `instanceName` | `Hoist:InstanceName` (default: machine name) |
| `alertBanner` | always an object, `{active: false}` when none is set. hoist-react destructures it, so it must never be null. |
| `pollConfig` | the `xhEnvPollConfig` typed config: `{interval, onVersionChange}` |
| `databaseConnectionString`, `databaseUser`, `databaseCreateMode` | **only when the authenticated user is `HOIST_ADMIN_READER`**. The connection string never includes the password. |

## `xh/environmentPoll` (`@AccessAll`)

Returns `{appCode, appVersion, appBuild, instanceName, alertBanner, pollConfig}`. The client polls it every `pollConfig.interval` seconds and reacts to version changes according to `onVersionChange`.

## Alert banner (read side)

- The banner spec is stored in a JsonBlob with type `xhAlertBanner` and owner `xhAlertBannerService`. Its name is `xhAlertBanner` in Production and `xhAlertBanner_{Environment}` elsewhere. Presets are in the blob named `xhAlertBannerPresets`.
- The published banner is the spec itself when three things are true: `xhAlertBannerConfig.enabled` is set, `spec.active` is true, and `spec.expires` (epoch ms) is missing or in the future. Otherwise it is `{active: false}`.
- The banner is re-read every 2 minutes, and immediately after a change through `alertBannerAdmin` (see [admin.md](admin.md)).

## Deliberate deviations

- `grailsVersion` is null and `javaVersion` holds the .NET runtime description.
- `webSocketsEnabled` is always false.
- The banner is refreshed lazily on read. hoist-core uses a cluster-primary timer instead.
