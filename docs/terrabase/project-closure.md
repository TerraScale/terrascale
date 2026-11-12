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
| 200 `{"status": "accepted"}` | Every expected holder recorded an empty receipt under the fence, and every other serving region the fence names acknowledged it. | fence (earlier) |
| 202 `{"status": "pending", "pendingHolders": n, "notEmptyRegions": []}` | Fence persisted; some holders or regions have not confirmed emptiness, or one sees a database. `notEmptyRegions` names each other region that refused the closure with `not_empty` after the fence was written. | fence |
| 409 `not_empty` | Version 1 only: this node or another serving region sees a database, project API key, backup destination or file bucket. | nothing, in any region |
| 409 `closure_conflict` | A fence for this project exists with another closure id or authority organization. | nothing |
| 409 `binding_conflict` | TerraBase's own binding names another authority organization, or an unverified legacy namespace holds the id. | nothing |
| 409 `platform_project_protected` | The project is a [platform project](platform-databases.md) (ts-auth `00000000000000000000000002`, ts-dashboard `00000000000000000000000005`), for either version. | nothing |
| 503 | Catalog unavailable or not verified (restore gate). Version 1 before the fence: another serving region did not answer within 5 s whether it holds anything of the project. | nothing |

404 is never returned for an unknown project: a never-enrolled project is fenced like an enrolled one.

## TerraBase rule

- The fence is `project_closures/<project>` in `_system`, outside `projects/<project>/` so project-state prefix deletes
  never tombstone it. Closure records are never collected, so it is permanent.
- Version 1 is refused before any fence is written while another region holds something of the project. A region
  that has no fence for the project first reads its own state, then asks every other serving region with
  `GET /admin/projects/{project}/closure?version=1&closureId=…&authorityOrganizationId=…` on its internal listener.
  That route has no handler but this read: a region answers it from one read transaction and writes nothing:
  `200 {"status": "empty"}` when it has no fence and holds no database, project key, backup destination or bucket of
  the project, its own status when it already has this fence, and otherwise the same 409 as above. One `not_empty`
  refuses the closure with `not_empty`; one `closure_conflict` or `binding_conflict` refuses it with that code; a region
  that does not answer within 5 s, or answers anything else, refuses it with 503. Nothing is written in any region in
  these cases. ts-auth cancels its intent and shows "Delete the project's databases, TerraBase API keys and backup
  destination first." on `not_empty`, and keeps the intent and tries again by itself on 503. A request for a project
  that already has the fence skips the check.
- A node of an earlier release has no handler for that GET: its internal listener answers `404 unknown_route` before
  any handler runs and writes nothing, which is "anything else" above. So while a region still runs the earlier
  release, a version 1 closure answers 503 and ts-auth's project deletion stays pending; it goes through by itself
  once every region runs this release. No region is fenced by a check.
- The request's transaction resolves TerraBase's own mapping and binding, checks local emptiness and writes the fence
  plus this machine's receipt atomically. Expected holders are this machine plus one holder `region:<name>` for every
  other serving region of the peers file. The region that takes the request sends the same closure to each of those
  regions with the header `X-TerraBase-Closure-Scope: region`; a region answers such a request for itself alone, and
  its `200 accepted` becomes that holder's receipt ([account-project-erasure.md](account-project-erasure.md), "Every
  region").
- Database registration, project API key issue (`EngineApiKeyStore`), backup destination saves
  (`ProjectBackupDestinations`), file bucket creation, upload reservation and upload completion
  (`FileStorageService`) all read the fence in their committing transaction. A file request answers 404 once it is there. Project-key and destination writes
  answer 404 once it is there; the operator key route keeps its fixed 400 for an unknown scope.
- Every machine's convergence pass (2 s) writes or refreshes its own receipt `project_closure_receipts/<project>/<machine>`
  in a local catalog transaction that sees the fence. Receipt `empty` is computed from live database records of the
  project. Registration (`CanAddDatabaseAsync`: create, branch, copy/restore, adoption) and enrollment
  (`ResolveProjectAsync` with `create`) read the fence in their committing transaction, so after a machine's receipt no
  database of the project can be registered on that machine.
- After the fence, the request erases on its machine the first 100 purges the closure tracks
  (`project_closure_purges/<project>`, read once): for each it deletes the local files, then destroys the backup
  key scopes, the database's own first, then each managed snapshot's, as a database DELETE does. A failed file
  deletion does not stop the key destruction, and a failed key scope does not stop the others. Each scope is
  recorded as destroyed (`keysShreddedAt` of `backup_purge_progress/<id>`) only after its keys are gone, the
  database's own once all of its scopes are, so the next request and the purge passes go on from what is not
  recorded. No new database or key scope starts after 10 seconds; one already started finishes. The request then
  refreshes this machine's receipt until the purge-history scan stops advancing or the 10 seconds have run out, at
  least once, and answers `200 accepted` only when every holder's receipt is empty. It answers `202 pending`, and the convergence, purge and backup-erasure passes and
  the next request continue, while a client session holds a database open, a peer region has not confirmed a key
  destruction, the project has more than 100 databases or tracked purges, the 10 seconds ran out, or a purge found
  only by the purge-history scan (a peer's shred, a regional import) that is not yet erased and recorded.
- A replicated version-2 erasure fence whose authority organization conflicts with the local storage binding never
  erases local contents. Convergence records this holder's receipt as non-empty, keeping closure pending until the
  binding agrees and erasure can be verified.
- Acceptance is computed on read: every holder in `expectedHolders`, and every machine currently listed as a catalog
  member, has an `empty` receipt naming the fence's closure id. The binding is causal, not wall-clock: a receipt is
  written only in a transaction that sees the permanent fence, so clock skew between machines cannot strand or forge
  acceptance. A local transaction alone is never acceptance. A request with the header
  `X-TerraBase-Closure-Scope: region` is answered from the receipt of the machine that holds the catalog now, and from
  no other holder.
- Something created in another region while a closure is on its way there:
  - Version 1. What exists in a region when the closure is requested refuses it before any fence (the check above).
    What is created in a region after it answered the check and before the fence reaches it makes that region refuse
    the forwarded closure with `not_empty`. That region writes nothing and is not fenced. The requesting region stores
    the refusal as that region's receipt with `empty` false, counts it once and names the region in `notEmptyRegions`
    of every pending answer. Nothing is destroyed, the project stays usable in the refusing region and stays fenced in
    the others, and the closure stays pending until a customer deletion (with its own purge authority) empties the
    refusing region; the next forwarded request then fences it and its receipt becomes empty.
  - Version 2. The region fences the project when the closure reaches it and erases what it holds, whenever that was
    created. The closure stays pending until that region's receipt is empty.

Limits: a machine that joins membership after the acceptance read, or a historical disk absent from membership, is
not covered (same limit as database purge). An unreachable holder keeps the closure pending forever (fail closed).
Receipts do not travel when the catalog moves to another machine of the region: a fence written before such a move
names the earlier machine, which writes no receipt in the new catalog, so the answer to ts-auth for that closure
stays `202 pending` in that region. The answer to another region does not depend on it.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
