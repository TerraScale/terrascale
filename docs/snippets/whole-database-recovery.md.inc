**Pending rollout.** Check the deployed dashboard and MCP tool discovery for this workflow. The existing [HTTP backup and restore guide](/guides/backups-and-restore) describes the underlying API.

Restore retained point-in-time history into a **new database in the same project**, leaving the source unchanged. To recover individual historical documents into the original database, use [selective recovery](/guides/selective-recovery) instead; whole-database recovery does not use its record-approval token.

## Access, history and quota

Use an active customer database you can manage as an organization owner or admin. Restore also requires provisioning rights and a unique new name. Existing access, retention, erasure and quota checks still apply.

Choose `platform` (default) or `customer` as the **source backup bucket**. This selects existing history, not the new database's storage configuration. Each destination has its own actual `earliest`/`latest` window; there is no fallback between buckets. A `null` window reports no retained history for that destination; an error may instead mean history or access is unavailable. A manual backup is separate from this continuous PITR window.

A restore consumes one additional organization database slot and normal storage quota; the plan reserves neither. Free allows one database, so retaining its source leaves no slot for a new database. See [plan limits](/limits). Do not delete the source to make room: deleting it also removes its backups. Recovery does not extend retention or bypass erasure.

## In the dashboard

1. Open the source database, choose **Backups**, then **Restore database at UTC time**.
2. Select the backup source bucket and **Read retained window**. Review its actual UTC bounds and the organization database allowance.
3. Enter a retained **UTC** timestamp and a unique new database name. The timestamp defaults to the latest retained point; the form performs no local-time conversion.
4. Choose **Preview advisory metadata**. Review the source bucket, chosen time, snapshot time and applicable segment count/latest time. This is **metadata only**: it does not verify replay bytes, object integrity, segment continuity or transaction times, reserve history, or guarantee a successful restore.
5. Explicitly approve the source bucket, time and new name. Type that new name exactly into the confirmation field, then choose **Restore as new database**. The source remains unchanged. Successful identity readback opens the new database; independently read its expected application records before using it.

## Over MCP

Connect through the [MCP sign-in workflow](/guides/mcp). Use `get_context` and `list_databases` to identify the source; `databaseId` is its ID, not its name. These tools accept optional `organizationId` and `projectId`; pass both consistently to make the scope explicit. All calls require management access, and restore also checks provisioning rights.

| Tool | Required arguments | Result |
| --- | --- | --- |
| `get_point_in_time_window` | `databaseId` | `destination`, actual `window` (`earliest`/`latest`, or `null`), `databasesInUse`, `databaseLimit` |
| `plan_point_in_time_recovery` | `databaseId`, `pointInTime` | Advisory `window`, `pointInTime`, `snapshotAt`, `latestSegmentAt`, `segmentCount`, `historyCheck: "metadata"` |
| `restore_database_point_in_time` | `databaseId`, `pointInTime`, `name`, `confirmName` | New `databaseId`, `name`, `status: "ready"` after identity readback |

Each tool accepts `destination`, either `platform` (default) or `customer`. Pass `pointInTime` as a UTC ISO timestamp ending in `Z`, for example `2026-10-25T12:10:00.000Z`, inside that destination's actual window. Read the window, request the advisory plan, then obtain the user's explicit consent for the exact source, bucket, time and new name **before** calling restore. Never derive consent by automatically copying a proposed name into `confirmName`.

For example, after the user approves, the MCP `tools/call` parameters are:

```json
{
  "name": "restore_database_point_in_time",
  "arguments": {
    "organizationId": "YOUR_ORGANIZATION_ID",
    "projectId": "YOUR_PROJECT_ID",
    "databaseId": "SOURCE_DATABASE_ID",
    "destination": "platform",
    "pointInTime": "2026-10-25T12:10:00.000Z",
    "name": "orders-before-import",
    "confirmName": "orders-before-import"
  }
}
```

Replace the IDs and example time with the reviewed values. Window and plan use current-user project tokens with `databases:read`; restore uses `databases:write`, followed by `databases:read` for identity readback. MCP sign-in credentials and project API tokens have separate authentication contracts; do not substitute one for the other. The dashboard routes these operations to the source's home region.

## Refusals, unknown outcomes and cleanup

Retention can advance after planning. For unavailable, incomplete or unproven history, refresh the selected destination's window and review another retained point. Unreadable history may need support; retain the request ID. For a name conflict choose another new name; for a quota refusal review the organization allowance. None of these conditions permits overwriting the source or bypassing access checks.

An interrupted request, timeout or unavailable response can leave the restore outcome **unknown**, including when the new database was created but readback failed. Inspect the same project's database list and the proposed new name before submitting another restore. Reconcile its identity and read expected records; a missing response is not proof that nothing was created. Do not automatically retry with another name or a broader identity. For a confirmed temporary refusal, respect `Retry-After` when supplied.

Keep the original source until you have verified the new database and decided how your application will use it. Delete an unwanted restored database only after checking its distinct ID/name and approving that deletion; its backups expire with it. Whole-database PITR does not automatically switch application traffic or clean up either database.
