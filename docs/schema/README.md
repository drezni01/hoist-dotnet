# Hoist schema (xh_* tables)

The tables are mapped exactly as GORM and Hibernate created them for hoist-core **v42.1.0**:

- **Tables** use the names from `static mapping { table ... }`.
- **Columns** are the snake_case of the property name.
- **Ids:** every table has a `bigint id` and a `bigint version`, except `xh_role`. `version` is used for optimistic locking. It is set to 0 on insert and incremented on each update.
- **Strings** are `varchar(255)` unless a `maxSize` is declared. Properties mapped as `type: 'text'` are `longtext`.
- **Booleans** are `bit(1)` on MySQL. **Dates** are `datetime(6)`, stored in the database's local time; see `Hoist:Database:TimeZone`.

The EF mapping is in `src/Hoist.Infra/Database/HoistDbContext.cs`. `tests/Hoist.Tests/Schema/TestHoistSchema.cs` pins the expected table and column names. `tests/Hoist.Tests/LiveDb/TestLiveHoistSchema.cs` checks them against a real schema.

## Startup

At startup the server does two things with the schema:

1. **Create.** With `Hoist:Database:Migrate=EnsureCreated` (the default), all tables are created if the schema is empty. An existing schema is never altered.
2. **Validate.** With `Hoist:Database:ValidateSchema=true` (the default), every mapped table and column must exist. If any are missing, the server logs them and fails to start, which also stops the host. Extra columns are allowed. See [decision 0003](../decisions/0003-validate-schema-at-startup.md).

## SQL logging

Hoist's own SQL is not logged by default. EF Core logs every DbContext's commands under the one category `Microsoft.EntityFrameworkCore.Database.Command`, so a host that logs its own SQL would otherwise log every Hoist query too: config and preference reads, and `xh_track_log` inserts. Set `Hoist:Database:LogSql=true` to log them. Failed commands (`CommandError`) are logged either way. Tests: `tests/Hoist.Tests/Schema/TestHoistSqlLogging.cs`.

To capture the live DDL as a reference, run `./scripts/dump-hoist-schema.ps1 -Schema <hoist schema> -User <user>`. It writes `hoist-core-42-mysql.sql`. Grails-generated unique-key names are hashes such as `UK_abc123`. They do not matter to EF, which matches on columns.

## Tables

| Table | Domain (hoist-core) | Columns | Keys and indexes |
|---|---|---|---|
| `xh_config` | `config/AppConfig` | id, version, name(50), value(text), value_type, note(1200), client_visible(bit), last_updated_by(50), last_updated, group_name | unique name |
| `xh_preference` | `pref/Preference` | id, version, name(50), type(20), default_value(text), notes(1200), last_updated_by(50), last_updated, group_name | unique name |
| `xh_user_preference` | `pref/UserPreference` | id, version, username(50), user_value(text), last_updated_by(50), last_updated, preference_id | FK preference_id → xh_preference.id; unique (preference_id, username); idx_xh_user_preference_username |
| `xh_json_blob` | `jsonblob/JsonBlob` | id, version, token, type(50), owner(50), acl(text), name(255), value(text), meta(text), description(text), date_created, last_updated, last_updated_by, archived_date(bigint, epoch ms, 0 = active) | unique token (idx_xh_json_blob_token) |
| `xh_track_log` | `track/TrackLog` | id, version, username(50), impersonating(50), date_created, category(100), msg(255), data(text), elapsed(int), severity, correlation_id(100), load_id(8), tab_id(8), instance(50), client_app_code(50), browser(100), device(100), user_agent(255), app_version(100), app_environment(100), url(500) | idx_xh_track_log_{username, date_created, category, tab_id, client_app_code}. No last_updated column (`autoTimestamp false`). |
| `xh_role` | `role/provided/Role` | **name (PK, assigned)**, version, category(100), notes(1200), last_updated, last_updated_by(50) | |
| `xh_role_member` | `role/provided/RoleMember` | id, version, type (`USER` \| `DIRECTORY_GROUP` \| `ROLE`), name, date_created, created_by, role_id (varchar) | FK role_id → xh_role.name; unique (name, type, role_id) |

Tables for features that are not ported (`xh_log_level`, `xh_monitor`) are neither mapped nor created.

## Schema history (hoist-core upgrade notes)

- **v35:** added `xh_track_log.client_app_code VARCHAR(50)`.
- **v38:** added `xh_log_level.suppress_stack_trace` and `include_start_messages` (not mapped here).

When a hoist-core release adds or changes a column, its `docs/upgrade-notes/vNN-upgrade-notes.md` gives the DDL. Make three changes together:

- update `HoistDbContext`;
- update `TestHoistSchema.ExpectedColumns`;
- add a line to this list.
