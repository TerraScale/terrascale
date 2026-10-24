# Account project erasure

The peer-authenticated `PUT /admin/projects/{project}/closure` accepts two explicit contracts.
Version 1 keeps the existing empty-project check. Version 2 is ts-auth's handoff after an authorized
account deletion: it permanently fences the project, revokes its TerraBase project keys and removes
its databases regardless of replica placement. Both versions retain verified namespace binding checks.
A different closure id or organization conflicts. Account consent may upgrade the same fence from version 1 to 2; receipt versions must match and downgrade is refused.

Each catalog holder advances bounded cleanup batches. Database unregister records the existing permanent
purge authority and backup cleanup history. Project closure tracks those purges outside project state
until their active-copy erasure receipts have been verified. Catalog absence alone is insufficient.
An offline holder keeps closure pending. `200 accepted` requires every catalog holder's empty receipt;
`202 pending` retains durable work. Backup and shared-history erasure keep their existing deadlines and
completion evidence; this response does not certify that those retention lifecycles have finished.

Public database DELETE now also records a permanent purge without requiring a preceding placement PATCH.
It still requires write authorization and the database's home region. Its 204 means logical deletion and
local cleanup; remote cleanup retries durably. Independent branches remain.

Compatibility: version 1 still refuses nonempty projects and now waits for prior active purges. Both contracts
require evidence-version-2 receipts; legacy empty-only receipts are refreshed. This also makes a delayed v1
acceptance safe when auth adopts an intent in another region. Older TerraBase binaries reject version 2, so auth retains
its durable intent until the TerraBase rollout. This adds a versioned JSON record and no SQLite schema
change or storage-format bump. Complete the TerraBase rollout through FR1 and drain all closure calls started against older binaries before
advancing account adoption traffic. Older responses do not certify active erasure. Collect live acceptance
evidence and release the dashboard consent change only after that barrier.
