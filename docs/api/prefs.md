# Preferences

Ported from hoist-core **v42.1.0**.

| | Files |
|---|---|
| hoist-core | `grails-app/services/io/xh/hoist/pref/PrefService.groovy`, `grails-app/domain/io/xh/hoist/pref/{Preference,UserPreference}.groovy`, `src/main/groovy/io/xh/hoist/pref/PreferenceSpec.groovy`, `XhController.{getPrefs,setPrefs,unsetPrefs,clearUserState}` |
| .NET | `src/Hoist.Core/Services/PrefService.cs`, `src/Hoist.Infra/Database/PrefRepository.cs` |
| Tests | `Contract/TestPrefEndpoints.cs`, fixture `Fixtures/xh.getPrefs.json` |

## Endpoints

All four endpoints are `@AccessAll`. Each also requires a **`clientUsername`** param, from the query string or form, that equals the session's **apparent** username. Otherwise the request fails with a 400 `SessionMismatchException`, `isRoutine: true`:

- **Param missing:** `Unable to confirm match between client and session user.`
- **Param differs:** `The reported clientUsername does not match current session user.`

| Route | Request | Response |
|---|---|---|
| `xh/getPrefs` | `clientUsername`; hoist-react posts it as a form field | `{prefName: {type, value, defaultValue, isSet}}` for **every** preference |
| `xh/setPrefs` | JSON body `{prefName: value}`, `?clientUsername=` | `{preferences: {...}}`, for the submitted keys only |
| `xh/unsetPrefs` | JSON body `["prefName", ...]`, `?clientUsername=` | `{preferences: {...}}`, for those keys |
| `xh/clearUserState` | `clientUsername` | 204. Deletes all the user's UserPreferences and, from Stage 5, their `xhUserState` view-state blobs. |

### Client format

- `type` is one of `string`, `int`, `long`, `double`, `bool` or `json`.
- `value` is the user's value if one is set, otherwise the default. `defaultValue` is the default. Both are parsed: json values become objects or arrays, numbers become numbers, and bools follow Groovy `toBoolean`.
- `isSet` is true when the user has a row in `xh_user_preference`. hoist-react v86.4+ needs this field to call `unsetPrefs`.

### How `setPrefs` stores each value

- **Object:** `setMap`, which requires a `json` pref. Stored compact.
- **Array:** `setList`, which also requires a `json` pref.
- **Anything else:** `setPreference(key, value.toString())`. There is no type check, but domain validation still runs. For example, `"maybe"` for a `bool` pref fails with a 400 `ValidationException`.
- **Unknown pref name:** fails with 500 `Preference not found: x`.

Reads and writes act on the **apparent** user. `lastUpdatedBy` records the **authenticated** user.

## Built-in prefs (`PrefService.InitAsync`, from `hoist-builtins.json` → `prefs`)

| Pref | Type | Default |
|---|---|---|
| `xhAutoRefreshEnabled` | bool | true |
| `xhIdleDetectionDisabled` | bool | false |
| `xhLastReadChangelog` | string | `0.0.0` |
| `xhShowVersionBar` | string | `auto` |
| `xhSizingMode` | json | `{ }` |
| `xhTheme` | string | `system` |

Missing prefs are created with `lastUpdatedBy = hoist-bootstrap`. hoist-react **requires** `xhTheme` and `xhIdleDetectionDisabled` to exist.

## Validation (GORM constraints, 400 with Grails default messages)

- **Preference:** `name` is required, at most 50 characters, and unique. `type` must be one of the allowed types. `defaultValue` must be valid for the type. `notes` is at most 1200 characters.
- **UserPreference:** `username` is at most 50 characters and unique per preference. `userValue` must be valid for the pref's type.

## Deliberate deviations

- A `null` value in `setPrefs` fails validation with a 400. In hoist-core it fails with a `NullPointerException`, which is a 500.
