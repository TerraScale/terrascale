# File bucket metadata

Bucket management requires project credentials with documents:read/write scopes;
end-user and database-restricted credentials cannot manage buckets.

```rust,ignore
use terrascale::v1::CollectionRules;
let created = client.create_bucket("avatars", None).await?;
let page = client.list_buckets(None).await?;
if let Some(after) = page.next {
    let next_page = client.list_buckets(Some(&after)).await?;
}
client.set_bucket_rules(&created.id, Some(&CollectionRules::default())).await?;
client.delete_bucket(&created.id).await?;
```

Pages contain at most 50 buckets in ID order; `after` is an exclusive ULID.
Pass `None` as rules to create or replace a denying end-user policy. Explicit
grants reuse collection rules, evaluated against immutable file metadata.
`public` still means authenticated end users, and roles are case-sensitive.
Deletion queues physical cleanup; reserved bytes can remain billed until cleanup.

DECISION: bucket rules use `{rules: ...}`, unlike the direct collection PUT.
Responses require bucket metadata or `{items,next}`; malformed IDs and missing
or invalid continuations fail decoding. The data client retains its project
scope and mutation replay policy. Transfers and signed capabilities are separate.
