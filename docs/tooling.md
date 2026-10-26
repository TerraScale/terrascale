# Schema tooling

DECISION: compose with existing database handles to reuse transport and credentials.

```python
from terrascale.tooling import DatabaseTooling, UniqueDeclaration, UniqueField
tools = DatabaseTooling(client.database("main"))
summary, sample = tools.get_database_schema(), tools.get_collection_schema("people")
tools.declare_collection_schema("people", UniqueDeclaration((UniqueField("email", "lowercase"),)))
tools.put_declared_collection_schema("people", {"type": "object"})
declared = tools.get_declared_collection_schema("people")
tools.delete_declared_collection_schema("people")
```

Uniqueness requires an empty collection (including retained tombstones) and converges
across replication; it is not globally serialized admission. Inference/uniqueness uses
`databases:read/write`; validation uses `documents:read/write`, affects future local writes
and limits schemas to64KiB. GET/DELETE may return schema:null. `AsyncDatabaseTooling`
has identical awaited methods. Reads retry; reconcile `CommitUncertainError` writes.
