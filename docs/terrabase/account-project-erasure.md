# Account project erasure

The peer-authenticated `PUT /admin/projects/{project}/closure` accepts two explicit contracts.
Version 1 keeps the existing empty-project check. Version 2 is ts-auth's handoff after an authorized
account deletion: it permanently fences the project, revokes its TerraBase project keys, removes
its databases regardless of replica placement and erases its file buckets with their files. Both versions retain verified namespace binding checks.
A different closure id or organization conflicts. Account consent may upgrade the same fence from version 1 to 2; receipt versions must match and downgrade is refused.

Each catalog holder advances bounded cleanup batches. Database unregister records the existing permanent
purge authority and backup cleanup history. Project closure tracks those purges outside project state
until their active-copy erasure receipts have been verified. Catalog absence alone is insufficient.
The closure request itself erases the first 100 purges the closure tracks when its erasure starts: it deletes each
database's local files and destroys its backup key scopes, the database's own first and then each managed snapshot's,
and records each scope as destroyed once its keys are gone. It starts no new database or key scope after 10 seconds;
one already started finishes. The closure stays pending, for the purge passes and the next request to continue from
what is recorded, while a client session holds a database open, a peer region has not confirmed a key destruction, the
project has more than 100 databases or tracked purges, the 10 seconds ran out, or a purge found only by the
purge-history scan (a peer's shred, a regional import) is not yet erased and recorded.
## File buckets

Version 2 also erases the project's file buckets: the bucket definitions, every file record (completed, incomplete and deleted but held) and the objects in the object store. The request does it after the databases, inside the same 10 seconds, and each machine's convergence pass (2 s) does it for every closure that is not settled, so a spent budget, a crash or a lost response goes on from what is left. The steps, for one bucket at a time:

1. Read up to 100 file records of the bucket.
2. Delete the object of each record in the object-store bucket of the definition's region (16 at a time).
3. In one transaction remove those records. A record whose upload URL could still be used gets a `file_object_purges` record in the same transaction ([file-buckets.md](file-buckets.md)).
4. Repeat from 1 until the bucket has no record. Then one transaction that sees no record drops the file kind and deletes the bucket definition.

Before the first bucket, the erasure writes the project's sweep marker (below) in a transaction that reads the fence. An object is deleted before its record, and the bucket definition goes last, so whatever a stopped erasure leaves behind is still named by a record the next pass reads. Every transaction of the erasure reads the fence again: a version-2 closure record of that project whose authority organization is TerraBase's own binding of the project. Without it nothing is read or removed, and the closure stays pending. Kinds and object keys are built from that project id, that organization id, the bucket ids of the project's own definition kind and the file ids of that bucket's own file kind. The 6 minute hold on deleted files is not waited for. Usage and quota are sums over file records, so they are zero for the project once step 3 has removed the last one.

After the fence no upload can be reserved or completed and no bucket created. An upload URL issued before it stays valid for up to five minutes; a file uploaded through it is deleted again by the 5 s late-object pass until the URL has expired by a minute, which the closure does not wait for. An unreachable object store keeps the closure pending while a file record is left.

### Prefix sweep

Records do not name every object. An upload that started before its URL expired can end after its purge record is gone, a URL can be signed by a machine whose clock runs ahead, and a bucket created through another region's listener has its records in that region's catalog. So every project erasure also sweeps the project's own prefix, `app-files/<organization>/<project>/`, in the files bucket of every serving region (the one file identity reaches all of them): it lists the prefix, 1000 keys a call, and deletes every key listed, 16 at a time.

| Step | When | What |
| --- | --- | --- |
| Marker | The first erasure pass that sees the fence, before the first bucket | One record `file_project_purges/<project>`: organization, the time it was written, the time it ends (written + 5 minutes, the longest life of an upload URL issued before the fence, + 24 hours) and the next sweep, due at once. It is written only when absent. |
| Sweep | A pass every 5 s, for each marker that is due | Reads the project's closure record, lists and deletes the prefix in every region's files bucket, then moves the next sweep to twice the marker's age: sweeps at about 5 s, 10 s, 20 s, 40 s and so on, about 17 in the 24 hours, never later than the marker's end. A region whose listing was cut at 1000 keys keeps the marker due for the next pass. |
| Last sweep | The first sweep that starts at or after the marker's end and leaves no region cut | Removes the marker in the transaction that would have moved it. |

The prefix is built only from the closure record: the sweep reads `project_closures/<project>` again each time and requires version 2, that project id and the organization the marker holds, both canonical ULIDs; a marker without such a closure lists nothing and is an error that stops the process, as any unexpected error in the file passes does. The prefix ends in `/`, so it selects exactly the keys whose third segment is that project id; a key the store returns outside the prefix fails the sweep before any delete. The closure does not wait for a sweep: the marker is not part of the project's emptiness.

What is guaranteed:

- When the closure is reported complete, every bucket definition and file record in the catalogs of the machines that hold the fence is gone, and every object those records named was deleted before its record.
- An object that arrives under the prefix later, in any region's files bucket, is deleted by the next sweep: one that arrives when the marker is `t` old is gone when the marker is about `2t` old plus one pass.
- The last object is gone when the last sweep has ended, 24 hours and 5 minutes after the erasure began on that machine, or later while the object store does not answer: a failed sweep is counted, logged and repeated at the next pass, and the marker stays. A node without file credentials makes no list call and keeps the marker until it runs with them.
- After the last sweep nothing deletes an object that arrives under the prefix.

Limit: the record erasure covers the catalog of each machine that holds the fence. A bucket created through another region's listener is recorded in that region's catalog, which a closure sent to one region does not reach: its definition and file records stay there after the closure is reported complete. Its objects are removed by the prefix sweep, so those records then name nothing. That region does not refuse new uploads to the bucket, and an object uploaded there after the last sweep stays. Erasing those records and fencing that region is a separate change that must land before file storage is switched on in production.

An offline holder keeps closure pending. `200 accepted` requires every catalog holder's empty receipt;
`202 pending` retains durable work. Backup and shared-history erasure keep their existing deadlines and
completion evidence; this response does not certify that those retention lifecycles have finished.

Public database DELETE now also records a permanent purge without requiring a preceding placement PATCH.
It still requires write authorization and the database's home region. Its 204 means logical deletion and
local cleanup; remote cleanup retries durably. Independent branches remain.

Compatibility: version 1 still refuses nonempty projects and now waits for prior active purges. Both contracts
require evidence-version-2 receipts; legacy empty-only receipts are refreshed. This also makes a delayed v1
acceptance safe when auth adopts an intent in another region. The response carries `evidenceVersion: 2`.
This adds a versioned JSON record and no SQLite schema change or storage-format bump.

Auth can auto-release before the TerraBase roll. It first requests version 2; a missing or unsupported
contract, including old version-1 validation, falls back to the existing version-1 path with the same authority
and closure id. Legacy `not_empty` or `closure_conflict` retains the account erasure intent: a conflict may
encounter a newer fence whose response was lost. Legacy acceptance cannot certify erasure. The background
driver retries version 2 and completes automatically after FR1 upgrades TerraBase, without another submit.
Collect live acceptance evidence after every holder is upgraded.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
