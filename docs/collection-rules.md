# Document collection access rules

Read with `database.GetCollectionRulesAsync(collection, cancellationToken)`;
replace with `database.SetCollectionRulesAsync(collection, rules, cancellationToken)`.
Project credentials need documents:read/write respectively. End-user credentials
cannot administer rules. A null Rules result denies all end-user document access.

`TerraBaseCollectionAccessRules` has an optional immutable-owner JSON Pointer and
required Read/Write policies. Each policy grants owner, authenticated public or
case-sensitive trusted-role access; all-false grants with an empty roles array deny.
Public does not allow anonymous requests. The producer validates owner pointers,
roles and ownership changes. PUT JSON has a fixed 16 KiB bound and is sent once.
File-bucket rules use a separate public contract.

The pinned source contract is contracts/terrabase/collection-rules.json. HTTP
fixtures cover contract behavior; own-user/foreign-project live authorization and
cleanup remain pending. Install from source/local pack; NuGet remains held.
