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
acceptance safe when auth adopts an intent in another region. The response carries `evidenceVersion: 2`.
This adds a versioned JSON record and no SQLite schema change or storage-format bump.

Auth can auto-release before the TerraBase roll. It first requests version 2; a missing or unsupported
contract, including old version-1 validation, falls back to the existing version-1 path with the same authority
and closure id. Legacy `not_empty` or `closure_conflict` retains the account erasure intent: a conflict may
encounter a newer fence whose response was lost. Legacy acceptance cannot certify erasure. The background
driver retries version 2 and completes automatically after FR1 upgrades TerraBase, without another submit.
Collect live acceptance evidence after every holder is upgraded.
