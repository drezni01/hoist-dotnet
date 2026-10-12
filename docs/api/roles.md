# Roles

Ported from hoist-core **v42.1.0**.

**hoist-core source:**

- `src/main/groovy/io/xh/hoist/role/BaseRoleService.groovy`
- `src/main/groovy/io/xh/hoist/role/provided/{DefaultRoleService,RoleSpec}.groovy`
- `grails-app/services/io/xh/hoist/role/provided/DefaultRoleUpdateService.groovy` (`ensureRequiredRolesCreated`, `assignRole`)
- `grails-app/domain/io/xh/hoist/role/provided/{Role,RoleMember}.groovy`

**.NET source:**

- `src/Hoist.Core/Services/DefaultRoleService.cs`
- `src/Hoist.Infra/Database/RoleRepository.cs`

**Tests:** `Unit/TestRoleService.cs`. Admin endpoints (`roleAdmin/*`) are covered in [admin.md](admin.md).

## Behaviour

- `IRoleService` is the port of `BaseRoleService`: `GetAllRoleAssignmentsAsync`, `GetRolesForUserAsync` and `GetUsersForRoleAsync`. A host can replace `DefaultRoleService` by registering its own `IRoleService`.
- **Effective users of role R:** the USER members of R, plus the USER members of every role reachable through R's ROLE-type members, found breadth-first. A ROLE member X on R means holders of X also get R. Usernames are lowercased.
- **Caching:** assignments are cached. They are recomputed after `xhRoleModuleConfig.refreshIntervalSecs` (default 300) and immediately after any change made through this server.
- **Startup:** the service creates the `xhRoleModuleConfig` config (`{refreshIntervalSecs: 300, directoryGroupProvider: 'auto'}`) and four built-in roles, all in category `Hoist` and created by `defaultRoleService`:

  | Role | ROLE members |
  |---|---|
  | `HOIST_ADMIN` | – |
  | `HOIST_ADMIN_READER` | `HOIST_ADMIN` |
  | `HOIST_IMPERSONATOR` | `HOIST_ADMIN` |
  | `HOIST_ROLE_MANAGER` | – |

- **Bootstrap admin:** `Hoist:BootstrapAdminUser` is granted `HOIST_ADMIN`, `HOIST_ADMIN_READER` and `HOIST_ROLE_MANAGER` everywhere except Production. hoist-core does this in local development only, through the `bootstrapAdminUser` instance config.
- `AssignRoleAsync(user, role)` adds a USER member, created by `defaultRoleUpdateService`.

## Deliberate deviations

- **Directory groups are not supported.** `DirectoryGroupsSupported` is false, so LDAP and Entra are not used. DIRECTORY_GROUP members are stored but grant nothing.
- **No refresh timer.** Instead of a cluster-primary timer, a stale cache is refreshed on the next read.
