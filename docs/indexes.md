# Index tooling

DECISION: use `DatabaseIndexes(database)` or `AsyncDatabaseIndexes(database)`;
reuse schema tooling's exact integer decoding, transport and mutation safety.

```python
from terrascale.indexes import DatabaseIndexes, IndexRequest
indexes = DatabaseIndexes(client.database("main"))
created = indexes.create_index(IndexRequest("people", ["email"], name="people_email"))
listed = indexes.list_indexes(collection="people")
policy = indexes.get_auto_index_policy("people")
recommendations = indexes.list_index_recommendations()
indexes.drop_index(created.name)
```

Reads require `databases:read`; mutations require `databases:write`. The existing
server's policy `enabled` field is resource state, not a new SDK feature switch.
`set_auto_index_policy(collection, enabled)` sends that exact published body.
`apply_index_recommendation(id)` takes `collection:field`, not a ULID, and returns
`applied` (201) or `already-applied` (200). Vector indexes need one field and1–4096
dimensions; state may be building. Dimensions are absent/null for other kinds.
Async methods have identical arguments. Only reads retry; reconcile uncertain
mutations before repeating them. Manual index operations remain independent of policy.
