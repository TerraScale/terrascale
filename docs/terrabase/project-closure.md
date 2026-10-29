# Permanent project closure fence (TS-873)

TerraBase accepts an empty-project closure only after every catalog holder confirms emptiness under a permanent fence.
This file describes the server contract; `deletion-retention.md` describes the surrounding deletion evidence.

## Peer operation

`PUT /admin/projects/{project}/closure`, internal listener only (peer credential), never the public listener. SDK:
`TerraBaseManagementClient.CloseProjectAsync(projectId, authorityOrganizationId, closureId)` over ts-auth's existing
authority transport (`App.TerraBaseOrigin` plus `TERRABASE_PEER_SECRET`). No bearer token is sent or retained.

Body: `{"version": 1, "closureId": "<ULID>", "authorityOrganizationId": "<ULID>"}`. `closureId` is an opaque random
ULID that ts-auth draws once per project and stores on its project record, never derived from the project id; a retry
after a lost response, a restart or a repeated request presents the same identity.

| Answer | Meaning | Written |
| --- | --- | --- |
| 200 `{"status": "accepted"}` | Every expected holder recorded an empty receipt under the fence. | fence (earlier) |
| 202 `{"status": "pending", "pendingHolders": n}` | Fence persisted; some holders have not confirmed emptiness, or one sees a database. | fence |
| 409 `not_empty` | This node sees a database, project API key or backup destination. | nothing |
| 409 `closure_conflict` | A fence for this project exists with another closure id or authority organization. | nothing |
| 409 `binding_conflict` | TerraBase's own binding names another authority organization, or an unverified legacy namespace holds the id. | nothing |
| 503 | Catalog unavailable or not verified (restore gate). | nothing |

404 is never returned for an unknown project: a never-enrolled project is fenced like an enrolled one.

## TerraBase rule

- The fence is `project_closures/<project>` in `_system`, outside `projects/<project>/` so project-state prefix deletes
  never tombstone it. Catalog GC is off (`system-catalog-invariants.md`), so it is permanent.
- The request's transaction resolves TerraBase's own mapping and binding, checks local emptiness and writes the fence
  plus this machine's receipt atomically. Expected holders are the retained catalog members plus this machine, the same
  rule as permanent database purge.
- Database registration, project API key issue (`EngineApiKeyStore`) and backup destination saves
  (`ProjectBackupDestinations`) all read the fence in their committing transaction. Project-key and destination writes
  answer 404 once it is there; the operator key route keeps its fixed 400 for an unknown scope.
- Every machine's convergence pass (2 s) writes or refreshes its own receipt `project_closure_receipts/<project>/<machine>`
  in a local catalog transaction that sees the fence. Receipt `empty` is computed from live database records of the
  project. Registration (`CanAddDatabaseAsync`: create, branch, copy/restore, adoption) and enrollment
  (`ResolveProjectAsync` with `create`) read the fence in their committing transaction, so after a machine's receipt no
  database of the project can be registered on that machine.
- A replicated version-2 erasure fence whose authority organization conflicts with the local storage binding never
  erases local contents. Convergence records this holder's receipt as non-empty, keeping closure pending until the
  binding agrees and erasure can be verified.
- Acceptance is computed on read: every holder in `expectedHolders`, and every machine currently listed as a catalog
  member, has an `empty` receipt naming the fence's closure id. The binding is causal, not wall-clock: a receipt is
  written only in a transaction that sees the permanent fence, so clock skew between machines cannot strand or forge
  acceptance. A local transaction alone is never acceptance.
- A concurrent creation in another region before the fence arrived there makes that region's receipt non-empty: the
  closure stays pending, nothing is destroyed, and the project's databases stay usable until a customer database
  deletion (with its own purge authority) empties it.

Limits: a machine that joins membership after the acceptance read, or a historical disk absent from membership, is
not covered (same limit as database purge). An unreachable holder keeps the closure pending forever (fail closed).
