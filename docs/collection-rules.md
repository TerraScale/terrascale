# Collection access rules

Project credentials manage rules; end-user permissions are evaluated by the server.
`public` grants access to authenticated end users, never anonymous requests.
Read and write grants are independent, and trusted role names are case-sensitive.

```rust,ignore
use terrascale::v1::{CollectionRules, ReadWriteGrants};
let collection = client.database("orders").collection("items");
let before = collection.get_rules().await?; // None means end users are denied.
let rules = CollectionRules {
    owner: Some("/userId".into()),
    read: ReadWriteGrants { owner: true, public: false, roles: vec![] },
    write: ReadWriteGrants { owner: true, public: false, roles: vec![] },
};
collection.put_rules(&rules).await?;
```

The owner pointer binds an immutable signed subject. End users cannot transfer
ownership or recreate another subject's deleted ID. To deny all access, replace
the policy with `CollectionRules::default()`; there is no rules deletion route.

DECISION: collection PUT serializes the policy directly, while GET/PUT responses
require a `{rules: ...}` wrapper. Only explicit `rules:null` means undeclared;
malformed responses fail decoding. Nullable response grants and roles become
denying defaults. PUT always emits both grant objects and complete roles arrays.
The owner is optional per route prose; serialization always writes `owner:null`
when absent. The same DTOs are reused by bucket policies.
