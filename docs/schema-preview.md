# Preview validation without applying it

DECISION: freeze schema bytes in a collection-scoped preview handle because the
producer binds continuation to the identical body, process and local frontier.

```python
from terrascale.schema_preview import CollectionSchemaPreview
preview = CollectionSchemaPreview(client.database("main"), "people", {"type": "object"})
page = preview.page()
if page.next_cursor is not None:
    following_page = preview.page(cursor=page.next_cursor)
```

`AsyncCollectionSchemaPreview` has the same constructor and awaited `page`.
Both `documents:read` and `documents:write` project scopes are required, even
though the POST changes neither schemas nor documents and can safely retry.
Counts are per page (100 documents/1MiB, with one larger document permitted);
up to16 diagnostics expose only ID/path hashes and keywords, never values.
`409 preview_stale` or `400 invalid_cursor` requires restarting without a cursor.
This observes one local frontier; later writes can stale it. Preview is not an
apply gate or a globally serialized guarantee. Apply remains a separate prospective
validation operation in `DatabaseTooling`. Bodies are limited to64KiB.
