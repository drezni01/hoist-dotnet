# Admin Console endpoints

Ported from hoist-core **v42.1.0**. These endpoints are called by the hoist-react v88 Admin Console under `admin/**`.

| | Source |
|---|---|
| hoist-core | `grails-app/controllers/io/xh/hoist/{RestController,admin/*}.groovy`, `grails-app/services/io/xh/hoist/role/provided/{DefaultRoleAdminService,DefaultRoleUpdateService}.groovy`, `grails-app/services/io/xh/hoist/track/TrackLogAdminService.groovy`, `src/main/groovy/io/xh/hoist/data/filter/*` |
| .NET | `src/Hoist.Api/Controllers/Admin/*`, `src/Hoist.Core/Services/{RoleAdminService,TrackLogAdminService}.cs`, `src/Hoist.Core/Filters/HoistFilter.cs` |
| Tests | `Contract/TestRestAdminEndpoints.cs`, `Contract/TestRoleUserAdminEndpoints.cs`, `Contract/TestTrackLogAdminEndpoints.cs` |

The Admin Console also needs `xhView/*` ([views.md](views.md)). It loads the `xhAdminActivityTrackingView` ViewManager at startup and fails to open if that call fails.

## REST controllers (`AdminRestController<T>`)

hoist-core `RestController` together with `AdminRestController`. Reads need `HOIST_ADMIN_READER`. Writes need `HOIST_ADMIN`.

| Request | Action | Response |
|---|---|---|
| `GET rest/X` (optional `query` = JSON string) | read | `{data: [row...]}` |
| `GET rest/X/{id}` | read | `{data: [row]}`, or `{data: [null]}` when the id is not found |
| `POST rest/X` with body `{data: {...}}` | create | `{data: row}` |
| `PUT` or `PATCH rest/X[/{id}]` with body `{data: {id, ...}}` | update. The id is taken from the body. | `{data: row}` |
| `DELETE rest/X/{id}` | delete | 204 |
| `rest/X/bulkUpdate` with body `{ids: [...], newParams: {...}}` | bulkUpdate | `{success: n, fail: n}` |
| `rest/X/bulkDelete?ids=1&ids=2` (query string or form) | bulkDelete | `{success: n, fail: n}` |
| `GET rest/X/lookupData` | lookupData | specific to each controller |

- **Binding.** Like Grails `bindData`, only the keys present in `data` are bound.
- **`lastUpdatedBy`.** `preprocessSubmit` sets it to the authenticated user.
- **Audit.** Each change is tracked as `{msg: "Created|Updated|Deleted <Class>", category: 'Audit', data: {id}}`.
- **Validation.** Failures return 400 `ValidationException` with Grails messages.

| Controller | Domain | Row format | lookupData | Notes |
|---|---|---|---|---|
| `rest/configAdmin` | `AppConfig` | `{id, name, groupName, valueType, value, overrideValue, resolvedValue, defaultValue, clientVisible, note, lastUpdatedBy, lastUpdated}` | `{valueTypes, groupNames}` | `value` is parsed, except json stays a string and pwd is shown as its digest. `overrideValue` is the instance-config override. `resolvedValue` and `defaultValue` are typed-config views, null for untyped configs. |
| `rest/preferenceAdmin` | `Preference` | `{id, name, groupName, type, defaultValue, notes, lastUpdatedBy, lastUpdated}` | `{types, groupNames}` | Json defaults stay strings. Deleting a preference also deletes its user preferences. |
| `rest/userPreferenceAdmin` | `UserPreference` | `{id, name, groupName, type, username, userValue, lastUpdatedBy, lastUpdated}` | `{names}` | `data.name` selects the preference. `query.name` is an exact match and `query.username` a substring match. |
| `rest/jsonBlobAdmin` | `JsonBlob` | admin format: `value` and `meta` as raw strings (see [jsonblob.md](jsonblob.md)) | `{types}` | `archived: true` sets `archivedDate` to now and `false` sets it to 0. Other values leave it unchanged. |

## `roleAdmin/*`

The class requires `HOIST_ROLE_MANAGER`. Writes also re-check the **authenticated** user; if the check fails the response is 500 `AuthUser x is not a 'HOIST_ROLE_MANAGER'`.

| Route | Access | Request | Response |
|---|---|---|---|
| `roleAdmin/config` | `HOIST_ADMIN_READER` | – | `{enabled: true, userAssignmentSupported: true, directoryGroupsSupported: false, directoryGroupsDescription: null}`, or `{enabled: false}` when a host replaces `IRoleService` |
| `roleAdmin/list` | `HOIST_ADMIN_READER` | – | `{data: [role...]}` (shape below) |
| `roleAdmin/create` | role manager | JSON `{name, category, notes, users, directoryGroups, roles}` | `{data: {name, category, notes, lastUpdated, lastUpdatedBy}}` |
| `roleAdmin/update` | role manager | same as create | same as create |
| `roleAdmin/delete` | role manager | JSON `{name}`, normally sent as DELETE | 204 |
| `roleAdmin/bulkCategoryUpdate` | role manager | JSON `{roles: [names], category}` | `{data: [role...]}` |

Each role in `list` has these fields:

- `name`, `category`, `notes`, `lastUpdated`, `lastUpdatedBy`
- `members`: `[{type, name, dateCreated, createdBy}]`
- `inheritedRoles`, `effectiveRoles`, `effectiveDirectoryGroups`: `[{name, sourceRoles}]`
- `effectiveUsers`: `[{name, sources: [{role}]}]`
- `errors`: `{directoryGroups: {}}`

Write behaviour:

- **Members.** Members are diffed: missing ones are removed, new ones added. USER names are lowercased.
- **Create.** The role name must be unique ignoring case, otherwise the response is 400 `Role Name must be case-insensitive unique.`.
- **Delete.** ROLE-type memberships that point at the deleted role are removed too.
- **Audit entries.**

  | Action | Message | Data |
  |---|---|---|
  | create | `Created role: 'X'` | `{role, category, notes, users, directoryGroups, roles}` |
  | update | `Edited role: 'X'` | `{role, category, notes, added/removed users/directoryGroups/roles}` |
  | delete | `Deleted role: 'X'` | – |
  | bulk category update | `Updated N roles.` | `{roles, category}` |

- **Refresh.** Role assignments are refreshed immediately after every write.

## `userAdmin/*` (`HOIST_ADMIN_READER`)

| Route | Params | Response |
|---|---|---|
| `userAdmin/users` | `activeOnly`, exactly `true` or `false` (any other value returns 500) | `[{username, email, displayName, active}]` from `IHoistUserService.ListAsync` |
| `userAdmin/roles` | – | `{role: [usernames]}` |
| `userAdmin/rolesForUser` | `user` | `{user, roles}` |
| `userAdmin/usersForRole` | `role` | `{role, users}` |

## `trackLogAdmin` (`HOIST_ADMIN_READER`)

**`trackLogAdmin` (or `trackLogAdmin/index`)** takes a JSON body `{startDay?, endDay?, filters?, maxRows?}`.

- Days are `yyyy-MM-dd` or `yyyyMMdd`. They default to 1970-01-01 and today in the app time zone.
- It returns a **bare array** of TrackLog rows, newest first, from the app-timezone start of `startDay` to the end of `endDay`.
- The row cap is `min(maxRows or maxRows.default, maxRows.limit)` from `xhActivityTrackingConfig`.
- When tracking is disabled it returns 400 `DataNotAvailableException`.

Each row is `{id, username, impersonating, dateCreated, day, category, msg, data, elapsed, severity, correlationId, loadId, tabId, instance, clientAppCode, browser, device, userAgent, appVersion, appEnvironment, url}`. `data` is the raw JSON string. `day` is `yyyy-MM-dd` in the app time zone.

**`trackLogAdmin/lookups`** returns `{appVersion, browser, category, device, username}`, each a sorted list of distinct values.

### Filter spec (`Filter.parse`)

| Spec | Meaning |
|---|---|
| null or empty | no filter |
| an array | AND of the filters in it |
| `{field, op, value}` | a FieldFilter |
| `{filters: [...], op: 'AND'\|'OR'}` | a CompoundFilter. With one child it is that child; with none it is no filter. |

FieldFilter details:

- **Operators:** `=`, `!=`, `>`, `>=`, `<`, `<=`, `like`, `not like`, `begins`, `not begins`, `ends`, `not ends`. `includes` and `excludes` are unsupported for database queries.
- **Multiple values:** the operators other than the comparisons (`>`, `>=`, `<`, `<=`) accept an array `value`.
- **Null values:** `=` with `null` adds `IS NULL`. `!=` with `null` adds `IS NOT NULL`. `!=` never matches NULL columns, which is SQL semantics.
- **Text matching:** `like`, `begins` and `ends` are case-insensitive matches anywhere, at the start, or at the end.
- **Fields** are TrackLog property names. For dates, the value is epoch ms or an ISO string.

## `alertBannerAdmin/*`

| Route | Access | Request | Response |
|---|---|---|---|
| `alertBannerAdmin/alertSpec` | `HOIST_ADMIN_READER` | – | the spec, or `{active: false}` |
| `alertBannerAdmin/alertPresets` | `HOIST_ADMIN_READER` | – | an array |
| `alertBannerAdmin/setAlertSpec` | `HOIST_ADMIN` | JSON object | 204. The published banner refreshes immediately. |
| `alertBannerAdmin/setAlertPresets` | `HOIST_ADMIN` | JSON array | 204 |

## Not ported (404 `NotFoundException`)

`clusterAdmin/*`, `clusterObjectsAdmin/*`, `serviceManagerAdmin/*`, `envAdmin/*`, `logViewerAdmin/*`, `rest/logLevelAdmin`, `memoryMonitorAdmin/*`, `connectionPoolMonitorAdmin/*`, `metricsAdmin/*`, `clientAdmin/*`, `webSocketAdmin/*`, `monitorResultsAdmin/*`, `rest/monitorAdmin`, `jsonSearch/*`, `*DiffAdmin/*`, `roleAdmin/{usersForDirectoryGroup,directoryGroupsInfo,searchDirectoryGroups}`.

Their Admin Console tabs show an error when opened. To hide them, an app can:

- override `createTabs()` in its admin `AppModel`;
- set `xhEnableMonitoring` / `xhEnableLogViewer` to `false`;
- set `xhAdminAppConfig.hideUsersTab`.

## Deliberate deviations

- **`pwd` digest guard.** The admin form posts back the digest shown for an unchanged `pwd` config. When the posted value equals the current digest, the stored secret is kept instead of encrypting the digest.
- **Lookups skip nulls.** `trackLogAdmin/lookups` leaves out null values.
- **Directory-group endpoints return 404.** They aren't implemented. hoist-react treats 404 as "unsupported", and `directoryGroupsSupported: false` already tells it not to call them.
