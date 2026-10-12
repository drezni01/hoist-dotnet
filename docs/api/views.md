# Views (ViewManager)

This is a port of hoist-core **v42.1.0**.

- **hoist-core source:** `grails-app/services/io/xh/hoist/view/ViewService.groovy`, `grails-app/controllers/io/xh/hoist/impl/XhViewController.groovy`
- **hoist-react caller:** `cmp/viewmanager/DataAccess.ts`
- **.NET source:** `src/Hoist.Core/Services/ViewService.cs`, `src/Hoist.Api/Controllers/XhViewController.cs`
- **Tests:** `Contract/TestViewEndpoints.cs`

## Storage

A view is a JsonBlob whose type is the ViewManager's `type`. The blob's fields depend on how the view is shared:

| View kind | `owner` | `acl` | `meta` |
|---|---|---|---|
| Private | user | null | `{group, isShared: false}` |
| Shared | user | `'*'` | `{group, isShared: true}` |
| Global | null | `'*'` | `{group}` |

Each user's state for a type is stored in a sidecar blob of the same type. It is named `xhUserState` and owned by the user. Its value has this shape:

```
{ currentView: { [viewInstance]: token | null },
  userPinned: { [token]: bool },
  autoSave: bool }
```

## Endpoints

All endpoints are `@AccessAll`. Unless the table says otherwise, a request carries a JSON body, and query parameters as listed.

| Route | Request | Response |
|---|---|---|
| `xhView/allData` | `type`, `viewInstance` (form) | `{state, views: [blob, no value], manageGlobal}`. `manageGlobal` is `canWriteGlobal(type)` and drives hoist-react v88's global-view UI. |
| `xhView/updateState` | `?type=&viewInstance=`, body is a partial state | `state` |
| `xhView/get` | `token` (form) | blob, with value |
| `xhView/create` | `{type, name, group, description, isShared, isGlobal, isPinned?, value}` | blob, with value |
| `xhView/updateInfo` | `?token=`, body is a subset of `{name, description, group, isShared, isGlobal, isPinned}` | blob, with value |
| `xhView/bulkUpdateInfo` | `?tokens=a,b`, same body as `updateInfo` | 204 |
| `xhView/updateValue` | `?token=`, body is the new value | blob, with value |
| `xhView/delete` | `?tokens=a,b` | 204. Archives the views. |
| `xhView/renameGroup` | `?type=`, body `{from, to, isGlobal}` | `{count}` |

**State shape.** `state` is `{userPinned, autoSave, currentView?}`. `currentView` is the value stored for this `viewInstance`. The key is left out when nothing is stored, and it is `null` when the stored value is null (the default view).

**`updateState`.**

- `currentView` sets the value for this `viewInstance` only.
- `userPinned` is merged into the stored map, which then keeps only the tokens that still exist.
- `autoSave` replaces the stored value.

**`updateInfo` and `bulkUpdateInfo`.**

- `name` and `description` go to blob columns. `group` goes to `meta`.
- `isGlobal: true` sets `owner` to null and `acl` to `'*'`, and drops `meta.isShared`. This needs a global write role.
- `isShared: true` sets `owner` to the user and `acl` to `'*'`.
- When neither is true, `meta.isShared` becomes false, `owner` stays as it was (or becomes the user), and `acl` becomes null.
- `isPinned` updates `userPinned` in the `default` instance state.

**Bulk operations** (`bulkUpdateInfo`, `delete`) are best-effort. Each item is processed independently. If any item fails, a single error is raised afterwards: a 400 `RoutineRuntimeException` when every failure was routine, otherwise a 500. Its message reads `Failed to delete N view(s) - <first message>` or `Failed to update N of M view(s) - token: msg; ...`.

**`renameGroup`** rewrites `meta.group` for the group itself and for every path under it (`from + '/'`). It applies to the user's own views, or to global views when `isGlobal` is true.

**Tracking.** Every change is tracked with category `Views`. The messages are `Created View`, `Updated View Info`, `Bulk Updated View Info`, `Updated Global View definition`, `Deleted Views` and `Renamed View Group`.

`xh/clearUserState` deletes the user's `xhUserState` blobs for every type. The views themselves are kept.
