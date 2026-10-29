# Collection permissions

```python
from terrascale.rules import CollectionPermissions, CollectionRules, OperationGrants

permissions = CollectionPermissions(client.database("main").collection("people"))
current = permissions.get_rules()
owner_only = CollectionRules(
    owner="/userId",
    read=OperationGrants(owner=True),
    write=OperationGrants(owner=True),
)
permissions.put_rules(owner_only)
```

Use `AsyncCollectionPermissions(async_collection)` and await both methods for
asyncio. These project-credential operations reuse the client's authorization.
Both sync and async GET/PUT operations have been qualified against production.

`CollectionRules()` sends explicit all-false read/write grants and empty roles;
it denies end-user access. The server's explicit `{rules: null}` returns `None`
and also denies every end-user operation. Null read/write grants in a response
remain `None`, with no permission inferred; replacements require both grant
objects. A missing required response member is an invalid response.

`roles=None` remains JSON null; `roles=[]` remains an empty array. Neither grants
role access. Role spelling and case are preserved. Public grants apply to
authenticated end users, never anonymous requests. Ownership is the immutable
signed subject at a JSON Pointer; ownership-transfer restrictions stay enforced
by the server. PUT replaces the entire policy and is not automatically replayed
after an uncertain outcome. Read the policy before deciding whether to retry.
