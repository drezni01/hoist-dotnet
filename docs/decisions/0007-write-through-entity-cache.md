# 0007 - Cache configs, prefs, roles and JsonBlobs in memory

- **Status:** Accepted
- **Date:** 2026-10-18

## Context

Each repository call opens a short-lived `DbContext` and queries the database. A single `xh/getPrefs` reads the preference table and the user's rows, a config lookup reads `xh_config`, and the role service reloads every role on refresh. These tables are small and change rarely, so nearly every read returns data the server already had.

hoist-core avoids these queries with Hibernate's second-level cache. `AppConfig`, `Preference`, `UserPreference`, `JsonBlob`, `Role` and `RoleMember` are all mapped with `cache true` (`grails-app/domain/io/xh/hoist/**`), and hoist-core shares that cache across its cluster.

## Decision

Each of `IConfigRepository`, `IPrefRepository`, `IRoleRepository` and `IJsonBlobRepository` is wrapped in a write-through cache (`Caching*Repository` in `Hoist.Infra/Database`). The EF repositories do not change. This copies the decorator design of the host app this port was built for.

- **Reads.** `EntityCache` loads a table on first read and serves all later reads from memory. User preferences load one user at a time, on that user's first request.
- **Writes.** A write goes to the database first. Only after it succeeds is the saved entity, with its new id and version, put in the cache. If a write fails, for example on a stale optimistic-lock version, that table's cache is cleared and the next read reloads it.
- **Copies.** The cache stores copies and returns copies. Services load an entity, change it, and then validate and save it. Without copies, an edit that failed validation would stay in the cache.
- **Name matching.** Name, type, owner and username matches ignore case, as MySQL's default collation does. Tokens match exactly.
- **Still read from the database:** the uniqueness checks (`NameExistsAsync`, `UserPrefExistsAsync`), which guard writes, and the two admin-only user-preference lookups (`FindUserPrefByIdAsync`, `QueryUserPrefsAsync`). `xh_track_log` is not cached. It is written far more than it is read, and it is queried by date range.
- **Opt-out.** Caching is on by default. `Hoist:Database:CacheEntities=false` registers the plain repositories.

## Alternatives

- **Invalidate on write.** Each write would drop the table and the next read would reload it. This is simpler, but every preference save would trigger a full reload.
- **`IMemoryCache` or `HybridCache`, with expiry.** An expiry bounds how long another writer's change goes unseen, but every expiry costs a reload. These tables only change through this instance, so a timer adds no correctness. A distributed cache is the real answer for more than one writer, and it can be added behind the same decorators.
- **Cache inside the services.** This would put infrastructure concerns into Core, and each service would need its own invalidation.

## Consequences

- After warm-up, config, preference, role and JsonBlob reads issue no SQL.
- **This instance must be the only writer.** Changes made by a second instance, or by a Grails app on the same schema, are not seen until restart. The one exception is a save that hits a stale version, which fails and then reloads. Run with `CacheEntities=false` while Grails still writes the schema, or when running more than one instance.
- Memory use grows with the size of `xh_json_blob` and with the number of users who have preferences. Both are small for typical Hoist apps.
