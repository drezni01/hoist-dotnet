# JsonBlob

Ported from hoist-core **v42.1.0**.

- **hoist-core source:** `grails-app/services/io/xh/hoist/jsonblob/JsonBlobService.groovy`, `grails-app/domain/io/xh/hoist/jsonblob/JsonBlob.groovy`, `src/main/groovy/io/xh/hoist/jsonblob/JsonBlobConfig.groovy`, and the `XhController` actions `getJsonBlob`, `findJsonBlob`, `listJsonBlobs`, `createJsonBlob`, `updateJsonBlob`, `createOrUpdateJsonBlob` and `archiveJsonBlob`.
- **.NET source:** `src/Hoist.Core/Services/JsonBlobService.cs`, `src/Hoist.Infra/Database/JsonBlobRepository.cs`.
- **Tests:** `Contract/TestJsonBlobEndpoints.cs`.

## Endpoints

All endpoints are `@AccessAll`. hoist-react sends parameters as a form-encoded POST.

| Route | Params | Response |
|---|---|---|
| `xh/getJsonBlob` | `token` | the blob in client format |
| `xh/findJsonBlob` | `type`, `name`, `owner` (null finds a global blob) | the blob in client format, or `null` |
| `xh/listJsonBlobs` | `type`, `includeValue` (`true`/`false`) | `[client format]`, with `value` only when `includeValue` is true |
| `xh/createJsonBlob` | `data` = JSON string with `{type, name, description, value, meta, owner, acl}` | the blob in client format |
| `xh/updateJsonBlob` | `token`, `update` = JSON string with `{name, description, value, meta, owner, acl}` | the blob in client format |
| `xh/createOrUpdateJsonBlob` | `type`, `name`, `update` = JSON string | the blob in client format. Updates the user's own blob of that type and name, or creates one. |
| `xh/archiveJsonBlob` | `token` | the blob in client format, with `archived` true |

**Client format** (`formatForClient`): `{id, token, type, owner, acl, name, archived, archivedDate, description, dateCreated, lastUpdated, lastUpdatedBy, meta, value}`. `meta` and `value` are parsed JSON. Dates are epoch ms. `archivedDate` is 0 while the blob is active.

**Admin format** (`formatForJSON`, used by `rest/jsonBlobAdmin`): has the same fields, but `value` and `meta` are raw strings.

## Rules

**Reading.** A user can read a blob when its `acl` is `'*'` or the user is its `owner`. Otherwise the request fails with a 403 `User 'u' does not have access to JsonBlob with token 't'`.

**Listing.** `list` returns the active blobs of a type that the user owns or that have `acl '*'`.

**Writing (changed in v42).** Only the owner can write a blob. A global blob (`owner == null`) needs a role from `xhJsonBlobConfig.globalWriteRoles`:

- The roles listed under the blob's type apply, otherwise those under `*`. The default is `{"*": ["HOIST_ADMIN"]}`.
- An empty list means no one can write. A list that contains `'*'` means everyone can.

Write failures return 403:

- `User 'u' does not have write access to JsonBlobs owned by 'o'`
- `User 'u' does not have write access to global JsonBlobs of type 't'`

**Creating.** `owner` defaults to the current user. `value` and `meta` are serialized. `token` is the first 8 characters of a random UUID.

**Updating.**

- `type` is fixed and is ignored on update.
- Changing `owner` requires write access to the target namespace.
- `lastUpdatedBy` is set to the current user.

**Archiving.** Sets `archivedDate = now` (epoch ms). `lastUpdatedBy` becomes the authenticated user.

**Missing or archived token.** Fails with a 500 `Active JsonBlob not found with token 't'`.

**Validation.** Failures are 400s with Grails messages:

- `type` and `name` are required. `type` is at most 50 characters, `name` at most 255, and `owner` at most 50.
- `value` must be non-null JSON. `meta` must be JSON or null.
- `name` must be unique across (`name`, `type`, `archivedDate`, `owner`).
