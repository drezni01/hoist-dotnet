# 0002 - Dedicated Hoist schema, created with EnsureCreated

- **Status:** Accepted
- **Date:** 2026-10-07

## Context

hoist-core has no migrations. GORM's `dbCreate` creates and updates its tables. The .NET port has to work in two cases:

- against an existing hoist-core schema, including while a Grails app still uses it;
- on a fresh database.

## Decision

The `xh_*` tables live in their own schema, reached through `Hoist:Database:ConnectionString`. They are never mixed with a host app's tables.

`Hoist:Database:Migrate` is `EnsureCreated` by default, or `None`:

- **`EnsureCreated`** creates all `xh_*` tables when the schema is empty. It does nothing when the tables already exist.
- **`None`** leaves the schema alone.

The EF model maps the GORM layout exactly: names, `version` optimistic locking, and the database-local `DATETIME` values (see [docs/schema/README.md](../schema/README.md)).

## Alternatives

- **EF migrations with a baseline step for existing GORM schemas.** This is more machinery, and it isn't needed while the schema is dedicated.
- **Creating the tables only when `xh_config` is missing.** This would only be needed if the Hoist tables shared a schema with the host's tables.

## Consequences

- When hoist-core changes a column, the change must be applied by hand to existing databases, as hoist-core's own upgrade notes already require. The schema README lists each such change.
- If schema changes become frequent, introduce EF migrations then, and record that in a new decision.
