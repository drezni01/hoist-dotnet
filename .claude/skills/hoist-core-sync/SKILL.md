---
name: hoist-core-sync
description: Update HoistDotnet (this repo's C# port of hoist-core) to a new hoist-core release. Diffs hoist-core between the pinned version and the target, maps the changed files to ported components via docs/PORT-MANIFEST.yaml, and drives a test-first update of contracts, docs, code and the version pins. Use when the user mentions a new hoist-core version or release, upgrading or syncing hoist-core, a newer hoist-react that needs newer server endpoints, or checking what changed in hoist-core since the port was made.
---

# hoist-core-sync

HoistDotnet implements part of the hoist-core HTTP contract. Each ported area records the hoist-core source files it came from in `docs/PORT-MANIFEST.yaml`. This skill turns a hoist-core version diff into a focused, test-first change set.

## Inputs

- **Target version.** A tag such as `v43.0.0`. If the user gives none, use the latest tag in `../hoist-core`, after `git -C ../hoist-core fetch --tags` if the user agrees to a fetch.
- **Pinned version.** `hoistCore.version` and `hoistCore.commit` in `docs/PORT-MANIFEST.yaml`.

## Procedure

### 1. Map the diff

Run:

```bash
python -I scripts/hoist-core-diff.py --from v<pinned> --to <target>
```

Add `--json` to get machine-readable output. The report sorts every changed file into one of three groups:

- **Ported components affected.** These need review.
- **Unmapped changes.** Triage each one: is it a new feature in scope, or a file a ported component should list?
- **Skipped areas.** Clustering, websockets, metrics and similar. These need no action unless the user wants to widen the scope.

### 2. Read the intent

Read these for the version range:

- the hoist-core `CHANGELOG.md` sections;
- every `docs/upgrade-notes/vNN-upgrade-notes.md`;
- for each affected component, the actual diff: `git -C ../hoist-core diff v<pinned>..<target> -- <file>`.

Pay most attention to these kinds of change:

- endpoint signatures and the params they read;
- JSON shapes (`formatForJSON`, `formatForClient`, `renderJSON` maps);
- exception types and messages;
- access annotations;
- domain fields and constraints (schema);
- `BootStrap` config and pref specs;
- `TypedConfigMap` fields;
- defaults.

### 3. Check the paired client

hoist-core release notes name the hoist-react version each release pairs with. Compare the relevant hoist-react files listed under `hoistReactSources` in the manifest: `svc/*`, `admin/**`, `cmp/viewmanager/**`, `core/XH.ts` (`MIN_HOIST_CORE_VERSION`). A newer hoist-react may call endpoints, or read fields, that the new hoist-core adds.

### 4. Write a plan before editing

For each affected component, list:

- the contract change: request, response, error or access;
- the files under `dotnet`, `tests` and `docs` for that component in the manifest;
- any schema change. Upgrade notes give the DDL. Update `HoistDbContext`, `TestHoistSchema.ExpectedColumns` and the history list in `docs/schema/README.md`.

Show the plan to the user and confirm it before editing.

### 5. Implement test-first, one component at a time

1. Update `docs/api/<area>.md`. Cite the new hoist-core `file:method` and adjust "Deliberate deviations".
2. Update or add contract tests and fixtures (`tests/Hoist.Tests/Contract`, `Fixtures/*.json`), then run them and see them fail.
3. Change the code until `dotnet test` passes.

Typical edits by kind of change:

| Change | Where |
|---|---|
| Built-in configs, prefs, typed config fields | `src/Hoist.Core/Configuration/BuiltIn/hoist-builtins.json`. Keep declaration order; it is the JSON output order. Update the counts in `Unit/TestTypedConfig.cs` and `Unit/TestConfigService.cs`. |
| Endpoint shape | Controller in `src/Hoist.Api/Controllers`, service in `src/Hoist.Core/Services` |
| Domain or schema | `src/Hoist.Core/Models/HoistEntities.cs`, `src/Hoist.Infra/Database/HoistDbContext.cs` |
| Access | `[AccessRequiresRole]` and related attributes, which mirror the hoist-core annotations |

### 6. Bump the pins and record the sync

- Set `hoistCore.version` and `hoistCore.commit` in the manifest. Set `hoistReact.version` / `minHoistCoreVersion` if they changed.
- Update `HoistOptions.HoistCoreVersion` (`src/Hoist.Core/Configuration/HoistOptions.cs`). It is reported as `hoistCoreVersion` and checked by hoist-react against `MIN_HOIST_CORE_VERSION`.
- Update the version pins at the top of `CLAUDE.md` and in each `docs/api/*.md` header ("Ported from hoist-core vX").
- For each newly mapped file, add it to a component's `hoistCoreSources` or to `skipped`, so the next diff has no unmapped entries.

### 7. Verify

- `dotnet build` and `dotnet test` must pass.
- If a connection string is configured, also run `dotnet test --filter TestCategory=LiveDb` against a real hoist-core schema.
- Suggest a live check: run a hoist-react client's end-to-end tests against `Hoist.SampleHost` or a host app with Grails stopped. `CLAUDE.local.md` may name a local client and its test commands.

## Rules

- Match hoist-core behaviour exactly, including messages and status codes. Any intentional difference goes under "Deliberate deviations" in the area doc.
- Do not port skipped areas unless the user asks. If they do, move the area from `skipped` to `components` in the manifest.
- Do not commit unless the user asks.
