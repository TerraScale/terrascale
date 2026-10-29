# File bucket metadata

```python
from terrascale.storage import Storage
from terrascale.rules import CollectionRules, OperationGrants

storage = Storage(client)
bucket = storage.create_bucket("avatars")  # explicit denying policy
policy = CollectionRules("/userId", OperationGrants(owner=True), OperationGrants(owner=True))
bucket = storage.set_bucket_rules(bucket.id, policy)
page = storage.list_buckets()
if page.next_cursor is not None:
    page = storage.list_buckets(after=page.next_cursor)
storage.delete_bucket(bucket.id)
```

Use `AsyncStorage(async_client)` and await each method for asyncio. The same
`FileBucket`, `FileBucketPage`, `CollectionRules` and `OperationGrants` DTOs apply.
This source candidate covers four metadata routes. File transfer lifecycle,
root wrappers, live qualification and publication remain separate work.

Bucket management requires project credentials; end-user and database-restricted
tokens cannot manage buckets. Creating a bucket without a policy sends explicit
all-false grants and empty role arrays. Replacing rules requires a complete
policy wrapped as `{rules: ...}`. Ownership refers to immutable file metadata;
public grants apply to authenticated end users only, never anonymous callers.
Null role arrays remain null and grant no role access.

Lists contain at most 50 buckets in ID order, using an exclusive ULID `after`
cursor. Bucket arguments are IDs, not names. Deletion queues physical cleanup;
reserved bytes can remain billed until cleanup succeeds. Existing signed file
capabilities expire normally after a policy change. Mutations are not replayed
after uncertain outcomes; reconcile the bucket state before deciding to retry.
