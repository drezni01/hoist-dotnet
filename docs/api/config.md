# Config

Ported from hoist-core **v42.1.0**:

- `grails-app/services/io/xh/hoist/config/ConfigService.groovy`
- `grails-app/domain/io/xh/hoist/config/AppConfig.groovy`
- `src/main/groovy/io/xh/hoist/config/{TypedConfigMap,ConfigSpec}.groovy`
- `src/main/groovy/io/xh/hoist/util/InstanceConfigUtils.groovy`
- `grails-app/init/io/xh/hoist/BootStrap.groovy` (`ensureRequiredConfigsCreated`)

.NET source:

- `src/Hoist.Core/Services/ConfigService.cs`
- `src/Hoist.Core/Configuration/{TypedConfigRegistry.cs,BuiltIn/hoist-builtins.json}`
- `src/Hoist.Infra/Configuration/InstanceConfig.cs`
- `src/Hoist.Infra/Security/JasyptEncryptor.cs`

## `xh/getConfig`

| | |
|---|---|
| Access | authenticated (`@AccessAll`) |
| Params | none |
| Response | `{configName: value}` for every config with `clientVisible = true` |
| hoist-core | `XhController.getConfig` → `ConfigService.getClientConfig` |
| Tests | `Contract/TestConfigEndpoints.cs`, fixture `Fixtures/xh.getConfig.json` |

How each value is rendered (`externalValue(obscurePassword: true, jsonAsObject: true)`):

| valueType | Value |
|---|---|
| `string` | string |
| `int`, `long`, `double` | number |
| `bool` | boolean (Groovy `toBoolean`: `"true"`, `"y"` or `"1"`) |
| `json` | parsed object or array |
| `pwd` | always `"*********"` |
| typed config | rendered through its typed class (see below), so every declared field is present with its default when not stored |

- An instance-config override, when it parses, replaces the stored value.
- If one config fails to render, it is logged and left out; the rest are still returned.

## Typed configs

hoist-core declares 17 `TypedConfigMap` subclasses. They are ported as **data**: each class's fields, types and defaults are in `hoist-builtins.json` → `typedClasses`, in declaration order, with the Groovy source file noted. `TypedConfigRegistry.Resolve` reproduces `new X(map).formatForJSON()`:

- Only declared fields appear, in declaration order. Unknown keys are dropped.
- Nested typed objects merge onto their defaults. Lists of typed items are typed item by item.
- Types are checked strictly: a number can widen (int to double) but a number is never accepted as a boolean. A mismatch fails with `Field 'f' on Cls expects Boolean but got String`.
- `ActivityTrackingConfig.MaxRows.defaultValue` is read and written as `default`, with `defaultValue` accepted as an alias.

**Upgrade note:** when a hoist-core release changes a typed class or `BootStrap` specs, update `hoist-builtins.json`. `Unit/TestTypedConfig.cs` checks the counts and that every typed class resolves.

## Service behaviour

| hoist-core | .NET | Notes |
|---|---|---|
| `getString/Int/Long/Double/Bool/Map/List/Pwd(name, notFound)` | `Get*Async` | Unknown name: returns `notFound` if given, else throws `No config found with name: [x]` (500). Type mismatch: `Unexpected type for config: [x] \| config is a \| expected b`. |
| `getStringIfSet`, `getPwdIfSet` | `Get*IfSetAsync` | The value `none` (`AppConfig.NONE`) becomes null. |
| `getStringList` | `GetStringListAsync` | Splits on commas and trims each item. A `[otherConfig]` token expands that config recursively. |
| `getObject(Class)` | `GetObjectAsync(typedClass)` | |
| `setValue` | `SetValueAsync` | Non-string json values are pretty-printed. `lastUpdatedBy` defaults to `authUsername` or `hoist-config-service`. |
| `ensureRequiredConfigsCreated` | `EnsureRequiredConfigsCreatedAsync` | Creates missing configs only, with `lastUpdatedBy = hoist-bootstrap`. Json defaults are Jackson pretty-printed (`{ }`, `[ ]`, `"k" : v`). A typed config with no default seeds `{ }`. A mismatched type or `clientVisible` on an existing config is only logged. |

### Validation

GORM constraints plus `AppConfig.isValid` give a 400 `ValidationException`. The messages are the Grails defaults joined with ` | `.

- `name` is required, at most 50 characters, and unique.
- `value` must not be blank.
- `valueType` must be one of the allowed types.
- `bool` values must be exactly `true` or `false`. `int`, `long` and `double` values must parse.
- `json` values must be valid JSON and, for a typed config, must bind to the typed class: `Cannot apply value to X: <error>`.

### `pwd` values

Encrypted at rest with Jasypt `BasicTextEncryptor`, using hoist-core's password. Encryption happens on insert and whenever the value changes, so Grails and .NET can share rows.

- Admin "digest" output is Base64 MD5 (Jasypt plain digest).
- Instance-config overrides are treated as plaintext.
- Tests use real Jasypt 1.9.3 vectors.

### Instance config

`InstanceConfigUtils.getInstanceConfig(key)` looks in two places, in order:

1. The environment variable `APP_{APPCODE}_{KEY}`, both parts in Jackson UPPER_SNAKE_CASE.
2. The `Hoist:InstanceConfig:{key}` setting, which replaces `/etc/hoist/conf/{appCode}.yml`.

## Built-in configs

All 39 `xh*` configs from `BootStrap` are seeded, including ones for features that are not ported, so the Admin Console and a shared schema stay consistent. 15 of them are client-visible. See `hoist-builtins.json`.

## Deliberate deviations

- The YAML instance-config file and the config-directory mode are not read. Use `Hoist:InstanceConfig` or environment variables instead.
- The `xhConfigChanged` topic is not published, because clustering and pub/sub are out of scope.
- Configs are read from the database on each call, without Hibernate's second-level cache. This also means edits made by a Grails app sharing the schema are seen immediately.
