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

### Every region

A bucket and its file records are stored in the catalog of the region whose listener created the bucket; no other region sees them. So the region that takes the closure sends it to every other serving region, each region fences and erases what its own catalog holds, and the closure is finished only when all of them have. Both versions are sent; the steps, with A the region ts-auth called and B any other:

| Step | Region | What happens and what is stored |
| --- | --- | --- |
| Check | A, then B | Version 1 only, and only while A has no fence for the project. A reads its own state, then sends the request to every other serving region with the header `X-TerraBase-Closure-Scope: check`, all at once, 5 s each. B answers from one read transaction and stores nothing: `200 {"status": "empty"}`, its own status when it already has this fence, or `409`. A answers ts-auth `409 not_empty` when a region is not empty, the region's `closure_conflict` or `binding_conflict`, or `503` when a region did not answer or answered anything else, and stores nothing. Only when every region is empty or already fenced does A go on. |
| Fence | A | The fence `project_closures/<project>` names as expected holders this machine and one holder `region:<name>` for every other serving region the peers file lists at that moment. A fence written in a cluster of one region names no region. |
| Send | A | A sends the same request (`PUT /admin/projects/{project}/closure` with the fence's version, closure id and organization) to B's internal listener with the header `X-TerraBase-Closure-Scope: region`, waiting at most 5 s for each region. The closure request does it once, for all regions at the same time as its own erasure. A pass every 2 s does it again for each holder without a receipt: it reads 100 fences, handles 8 of them at a time, and ends 10 s after it started; the next run goes on after the last fence of the page that was handled with all those before it. |
| Fence and erase | B | B checks the request as it checks ts-auth's: canonical ULIDs, no fence under another closure id or organization, and its own binding of the project to that organization. It then writes its own fence and, for version 2, erases its own databases, keys, backup destination and file buckets with the steps above; where B binds the project to that organization, which every region that holds a bucket of it does, they write B's sweep marker and B's purge records. B's fence names the other regions too, so B also sends the closure on, and reaches a region A could not. |
| Answer | B | To a request with the header B answers for itself alone: `200 accepted` once the receipt of the machine that holds B's catalog now is empty, `202 pending` before that. A machine id that B's fence names from before a move of B's catalog is not asked for. Inside such a request no database, key scope or bucket step starts after 3 s. After those 3 s the request runs at most the one catalog transaction that computes its answer, which unregisters up to 100 databases and revokes up to 100 keys still listed; B's convergence pass goes on from what is left. |
| Receipt | A | On `200 accepted` that carries this closure id and evidence version 2, A writes the receipt `project_closure_receipts/<project>/region:<name>` in a transaction that reads the fence again and requires the same closure id, organization and version and that holder. On `409 not_empty` it writes that receipt with `empty` false, once: a later refusal finds it and changes nothing. |
| Finished | A | ts-auth gets `200 accepted` once this machine's receipt and every region's receipt are there, and `202 pending` with the number of missing holders until then. `notEmptyRegions` of a pending answer names the regions whose receipt says `empty` false. |

Once B has the fence it refuses, with 404, a new bucket, a new upload URL and the completion of an upload for the project, because each of them reads the fence in its committing transaction. A file that arrives through a URL B signed before the fence is deleted by B's own late-object pass, and after that by B's sweep.

Each step is safe to repeat. The fence is written once and every later request is compared with it; the erasure goes on from the records that are left; B's answer is computed from B's records each time; a receipt is written again with the same content. A crash therefore leaves one of these:

- before A's fence: nothing, and ts-auth sends the request again;
- after A's fence: a pending closure, which A's pass sends to every region without a receipt;
- after B's fence: B's own convergence pass, which erases without another message from A;
- after B's answer and before A's receipt: a holder without a receipt, which A asks again and B answers the same way.

The closure stays pending, and A keeps sending it, while:

- a region is down or does not answer within 5 s. The closure request is not held up by it beyond those 5 s, which run beside its own 10 seconds. In the pass a region that did not answer is not asked again in that run. A closure waits for its slowest region, and the pass handles 8 closures at a time, so a region that takes up to 5 s for every closure holds back the other closures of the same page: 100 closures that all wait for it take about a minute (100 / 8 × 5 s), in runs of at most 10 s. A run that reaches its 10 s stops the requests it has open, which are sent again by the next run;
- a region answers `202 pending`: it has the fence and is still erasing;
- a region refuses with 409. A region that holds something of the project refuses a version-1 closure at the check, before any fence, so `not_empty` reaches a fenced closure only when a database, a project key, a backup destination or a bucket was created in that region between its answer to the check and the arrival of the fence. That region writes nothing and is not fenced; A records the refusal once, names the region in `notEmptyRegions`, and the closure waits until the project is empty there or the account erasure raises the closure to version 2, which is then sent and erases it. `closure_conflict` and `binding_conflict` mean the region holds the project under another closure id or organization and erases nothing;
- a region named by the fence is no longer in the peers file. Nothing answers for it, so the closure stays pending.

Every time a region does not acknowledge, except with `202 pending`, `terrabase_project_closure_forward_errors_total` is raised with the labels `region` and `error`: `timeout`, `unreachable`, `not_listed` (the peers file no longer lists the region), `closure_conflict`, `binding_conflict`, `refused` (any other status), `invalid_answer` (a `200` without version-2 evidence of this closure) and `not_empty`, which is counted once for each closure and region, when its receipt is written. A failure of the pass itself or of one closure in it is counted with `error="failed"` and no region. A warning is written at most once every 10 minutes for each region and error. No such failure stops the pass, the other closures or the node.

Two gauges come from the same pass, set each time it has read every fence once: `terrabase_project_closure_pending`, the closures for which a holder or a region has no empty receipt, and `terrabase_project_closure_oldest_pending_age_seconds`, the time since the oldest of them was requested (0 when none is pending). A region that answers `202 pending` for days raises no error, and shows in these two.

A region that is serving now and is missing from a pending fence is added to it by the pass, in a transaction that reads the fence again and finds it still pending, and is then sent the closure like the others. That covers a region added to the peers file, and one that was taken out of it and put back, while the closure is pending. Three cases are left as they are:

- a fence that names no region, written before closures were sent to other regions or in a cluster of one region, gets no region and is completed as before;
- a region added after a closure finished that did not serve the project before holds no record of it. Its files bucket is still swept while a marker lasts, because the sweep reads the serving regions each time. Nothing fences the project there;
- a region that held something of the project, was out of the peers file while the closure finished and came back keeps what it held and is not fenced. This is not handled: no pass and no command erases it. The region erases it only when it is sent the closure request itself, with the same version, closure id and organization, on its internal listener.

A node without file credentials fences and erases like any other: bucket definitions and every other record need no credentials. It can hold no file record, because it could sign no upload URL; if credentials were taken away from a node that holds file records, their objects cannot be deleted and the region answers `202 pending` until the credentials are back.

Rolling deploy: the message between regions is the existing closure request plus one header, and the region holders are entries of the fence's existing `expectedHolders` list, so nodes of both releases work together:

- A node of an earlier release ignores the header. It fences, erases what its release erases and answers for its own machine, which is what the header asks for.
- A node of an earlier release that takes ts-auth's request writes a fence without region holders and sends nothing. That closure is reported complete as it was before this change, and stays that way after the upgrade.
- A node of an earlier release that takes over a fence with region holders (a handoff inside a region) finds no receipt for them, because receipts do not travel in the handoff, and answers `202 pending` until an upgraded node holds the catalog and asks the regions again.
- A node of an earlier release ignores the value `check` as it ignores `region`: it handles the check as the closure itself, so it writes its fence when it is empty and answers `409 not_empty` when it is not. A reads both answers correctly. If another region then refuses the same check, that node stays fenced for a project that was not closed, until the closure is requested again and goes through. This can happen only while a region still runs the earlier release.

A move of a region's catalog to another machine (a handoff inside the region) carries the fences and not the receipts:

- In the region that moved, the new machine writes its own receipt in its convergence pass, and its answer to another region is computed from that receipt alone, so the other regions get `200 accepted` from it as before the move.
- In the region that took the closure, the receipts of the other regions are gone. Its pass sends every closure that names regions to each of them once more, 8 closures at a time; each region answers from its records in one transaction and the receipts are written again. With `n` such closures that is `n` requests to each region, once after each move.
- The receipts are not added to the handoff, because a node of an earlier release refuses a handoff that contains a record kind it does not know. The handoff stays within its bound of 10 000 rows and 4 MB, which it reaches or not as before.
- A fence written before this change names no region: nothing is sent for it and its completion is computed as before, also after an account erasure raises it to version 2.

### Prefix sweep

Records do not name every object. An upload that started before its URL expired can end after its purge record is gone, and a URL can be signed by a machine whose clock runs ahead. So every project erasure also sweeps the project's own prefix, `app-files/<organization>/<project>/`, in the files bucket of every serving region (the one file identity reaches all of them): it lists the prefix, 1000 keys a call, and deletes every key listed, 16 at a time.

| Step | When | What |
| --- | --- | --- |
| Marker | The first erasure pass that sees the fence, before the first bucket | One record `file_project_purges/<project>`: organization, the time it was written, the time it ends (written + 5 minutes, the longest life of an upload URL issued before the fence, + 24 hours) and the next sweep, due at once. It is written only when absent. |
| Sweep | A pass every 5 s, for each marker that is due | Reads the project's closure record, lists and deletes the prefix in every region's files bucket, then moves the next sweep to twice the marker's age: sweeps at about 5 s, 10 s, 20 s, 40 s and so on, about 17 in the 24 hours, never later than the marker's end. A region whose listing was cut at 1000 keys keeps the marker due for the next pass. A region that fails (the store refuses the list or a delete, has no such bucket or does not answer) is counted and logged; the other regions are still swept and the marker moves to its next sweep as usual. |
| Last sweep | The first sweep that starts at or after the marker's end, leaves no region cut and has no failed region | Removes the marker in the transaction that would have moved it. A sweep at or after the end with a failed region keeps the marker and sets the next sweep 12 hours later, about the longest gap of the schedule; this repeats until one sweep lists every region, and that sweep removes the marker. |
| No credentials | Every pass of a node without file credentials | Makes no list call. A marker that has not reached its end is kept as it is: another node, or this node once it has credentials, may still sweep it. A marker past its end is removed in a transaction that sees it unchanged. |

The prefix is built only from the closure record: the sweep reads `project_closures/<project>` again each time and requires version 2, that project id and the organization the marker holds, both canonical ULIDs; a marker without such a closure lists nothing, is counted and logged, and is tried again at the next pass. The prefix ends in `/`, so it selects exactly the keys whose third segment is that project id; a key the store returns outside the prefix fails that region's sweep before any delete in it. The closure does not wait for a sweep: the marker is not part of the project's emptiness.

A failed pass is counted and retried; it never stops the node. Each file pass (expired files every 30 s, late objects and closed projects every 5 s) catches every error except the node's own shutdown, adds one to `terrabase_file_storage_cleanup_errors_total` (labels `pass` and `error`, the exception type, plus `region` for a failed region of a sweep) and runs again at its normal cadence. A warning with the exception is written at most once every 10 minutes for each pass, region and exception type. Inside the closed-projects pass a failure belongs to one marker: a busy catalog or a marker without its closure leaves that marker as it is, due again at the next pass, and a failed region moves it to its next sweep. Either way the pass goes on with the other regions and the other markers, and its paging moves past the marker.

A marker stays past its end while a region keeps failing, one record for each project closed in that time; a marker without its closure, which no path is known to write, also stays. A region keeps failing when the file identity may not list a bucket or a serving region has no files bucket yet ([file-storage-identity.md](file-storage-identity.md)); the markers go at the first sweep after the fault is fixed, at most 12 hours later. A node without file credentials removes a marker past its end without sweeping because it can sign no upload URL, and by the end every URL issued before the fence has expired, so that marker has nothing left to wait for.

What is guaranteed:

- When the closure is reported complete, every bucket definition and file record of the project is gone from the catalog of every region the fence names, which is every serving region at the time the closure was first requested and every region that was serving at a later run of the pass while the closure was pending, and every object those records named was deleted before its record.
- An object that arrives under the prefix later, in any region's files bucket, is deleted by the next sweep: one that arrives when the marker is `t` old is gone when the marker is about `2t` old plus one pass.
- The last object is gone when the last sweep has ended: 24 hours and 5 minutes after the marker was first written in that catalog, by this machine or by another one (the record travels in the regional handoff), or later while a region's files bucket cannot be listed.
- Once the marker is removed nothing deletes an object that arrives under the prefix, unless the closure is still pending on that machine: its convergence pass, or a repeated closure request, runs the erasure again, which writes the marker again, and a new window of 24 hours and 5 minutes starts. A settled closure writes no marker again.

An offline holder keeps closure pending. `200 accepted` requires every catalog holder's empty receipt and the acknowledgement of every region the fence names;
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
