# 0003 - Validate the schema at startup

- **Status:** Accepted
- **Date:** 2026-10-10

## Context

[0002](0002-hoist-schema.md) creates the `xh_*` tables only in an empty schema. An existing schema is never changed. If it drifts from the EF model, for example because a hoist-core upgrade's DDL was not applied or only some tables exist, the first failure is a SQL error deep inside a request.

## Decision

After the create step, `HoistSchema` checks that every mapped table and column exists in the database. It reads each table's columns with an empty `SELECT * ... WHERE 1 = 0`, which works on MySQL and SQLite alike.

- Missing tables and columns are logged as one error, and startup fails with `HoistSchemaException`. Hoist's startup initialization throws it, so the host does not start.
- Columns in the database that the model does not map are allowed, and logged at debug level. A newer hoist-core may add columns that this port does not use.
- `Hoist:Database:ValidateSchema=false` turns the check off.

## Alternatives

- **Log only.** This was rejected: the server would then fail on whichever request first touched the bad table.
- **Also compare column types and nullability.** This needs provider-specific `INFORMATION_SCHEMA` queries, and type names differ between MySQL and SQLite. It can be added later if type drift turns out to matter.
- **EF migrations.** See 0002. These would fix drift as well as detect it, at the cost of a baseline migration for every existing GORM schema.

## Consequences

- A schema that is out of date is reported at deploy time, with the exact missing columns.
- Startup runs one extra empty query per table, seven in all.
