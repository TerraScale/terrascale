# Database backups and recovery

This source candidate adds six advertised API operations through
`terrascale.backups`. Root-client integration is pending the concurrent safety
fixes; use the explicit handles below. This is HTTP contract coverage, with live
customer qualification and package publication still pending.

```python
from terrascale.backups import DatabaseBackups

backups = DatabaseBackups(client.database("main"))
snapshot = backups.create_backup()
page = backups.list_backups(limit=10)
if page.next_cursor is not None:
    page = backups.list_backups(cursor=page.next_cursor)
restored = backups.restore_backup(snapshot.id, name="snapshot-copy")
window = backups.get_restore_window()
plan = backups.plan_point_in_time(window.latest)
recovered = backups.restore_point_in_time(plan.point_in_time, name="time-copy")
```

For asyncio, construct `AsyncDatabaseBackups(async_client.database("main"))`
from the same module and await each method. Arguments and typed results match.
Restores return `RestoredDatabase`, a management acknowledgement without a
serving `region`; access the result with `client.database(restored.name)`.
`Backup.size_bytes` and `PointInTimePlan.segment_count` are Python integers even
when the server encodes them as decimal strings. Management cursors use `after`
on requests and `next` on responses; list limits are 1–100 (server default 20).

Recovery defaults to the platform backup destination. Use `destination="customer"`
to inspect, plan or restore from an already configured customer backup bucket.
Planning reads metadata only and does not create a database. A successful plan
does not guarantee restoration: restore also checks segment contents and can
return `backup_history_incomplete`. Snapshot creation and both restore methods
are mutations; reconcile an uncertain outcome before retrying.

## Release handoff

The repository documents bumping `src/terrascale/_version.py`, tagging the same
`v0.0.X` version and publishing a GitHub release. `.github/workflows/publish.yml`
checks the tag matches the source version, builds with uv and uses PyPI trusted
publishing through the GitHub `pypi` environment. It also exposes a manual
dispatch, subject to the same tag check. CI tests Python 3.10–3.13 and checks
wheel metadata. The inventory records that PyPI publication is still pending
with its existing publisher owner; this change neither verifies that external
trust configuration nor publishes a package. The current source version is 0.0.1.
