# Backup policy

```python
from terrascale.backup_config import BackupPolicyUpdate, DatabaseBackupPolicy

policy = DatabaseBackupPolicy(client.database("main"))
current = policy.get_backup_policy()
# Only after approving the retention/scheduling change:
saved = policy.set_backup_policy(BackupPolicyUpdate("0 2 * * *", 7))
```

`AsyncDatabaseBackupPolicy` has identical awaitable methods. GET requires
`databases:read`; PUT requires `databases:write`. A database without a saved
policy returns daily 02:00 UTC, seven days retention, `disabled=False` and
`updated_at=None`. Null or empty cron means no scheduled backups. The server
validates standard five-field UTC cron; retention 1–35 days applies to manual
and scheduled backups. Changing retention can delete older backups.

DECISION: preserve `disabled` as the existing server resource property for
pausing its schedule; it is not an SDK feature switch. Compose with the existing
database rather than modifying the recovery/core modules. GET can retry; PUT
never replays after transport/server failure or an invalid acknowledgement.
Re-read policy after `CommitUncertainError` before retrying. Diagnostics retain
only known error codes/status and a validated correlation ID, with values and
exception chains suppressed. HTTP-fake checks do not qualify live scheduling.
