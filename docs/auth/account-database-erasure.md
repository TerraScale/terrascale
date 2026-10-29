# Account deletion with TerraBase databases

`DELETE /v1/terrascale/account` authorizes erasure of projects in organizations whose only member is the caller.
Customers do not need to empty those projects first. Shared organizations remain, with the caller's membership removed;
the existing last-owner check still refuses deletion when another member would be left without an owner.

Auth persists `ClosureJob.ProjectClosureVersion = 2` on account-owned project intents before contacting TerraBase.
`TerraBaseManagementClient.EraseProjectAsync(project, organization, closure)` sends version 2 to the existing
peer-only `PUT /admin/projects/{project}/closure`. Retries retain the project's closure id and the intent's version,
including when account cancellation detaches a project intent to finish independently.

Version 2 writes a permanent closure fence, unregisters databases regardless of replica placement, revokes TerraBase
project API keys and removes backup destination authorization. Existing database purge work handles active files
and backup cleanup. Project closure acceptance requires every catalog holder's empty receipt and each
database purge's active-erasure evidence; removing database catalog entries alone is insufficient. An unavailable
holder keeps the operation pending. Backup/history erasure retains its separate evidence and retention lifecycle.

The account request returns 204 when completed or 202 while the durable worker continues. Callers can supply
`X-TerraScale-Deletion-Receipt` and query `GET /v1/terrascale/account/deletion` with the same capability after their
credentials stop working. Completion removes the auth project, its API keys, the sole organization and the user.
The receipt describes account closure completion, not completion of the separate backup/history retention lifecycle.

Standalone project and organization deletion keep version 1 through `CloseProjectAsync`: a nonempty project still
returns `409 not_empty`. Version 2 preserves binding checks: an unverified legacy namespace or a different authority
organization is refused. A fence with a different closure id or authority organization conflicts. An authorized account may upgrade the same fence from version 1 to 2; receipts must match the new version, and downgrades are refused.
Version 1 also waits for prior database purges and current evidence-version-2 receipts. Thus a delayed v1
acceptance from another auth region certifies active erasure even before it sees adoption. Auth requires
response `evidenceVersion: 2` before removing any project.

During a mixed-build rollout, auth first requests version 2. A missing or unsupported contract, including
the old version-1 validation response, falls back to version 1 with the same authority and closure id.
Legacy `not_empty` or `closure_conflict` retains the account erasure intent: a conflict may encounter a newer
fence whose response was lost before auth recorded it. Legacy acceptance cannot certify erasure. The background
driver retries version 2 and completes automatically after FR1 upgrades TerraBase, without another submit.
Validate live completion after all holders update.
