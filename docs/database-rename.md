# Database rename contract

> **Availability hold:** this SDK operation is prepared for the pending API rename contract.
> Rename remains unavailable: the producer currently returns 400 for name changes.
> TerraScale/terrascale#698 is an additive prerequisite for ULID addressing and name/ID claims; it does not enable rename.
> Keep merge, release and activation held until TS-1120 merges, the owning rename API is available, and required CI passes.
> Local HTTP fixtures prove SDK transport and decoding only; they do not prove live rename, namespace exclusivity, data preservation or cross-region completion.

The pending contract sends a name-only JSON body (`{"name":"production"}`) to
`PATCH /v1/projects/{projectULID}/databases/{databaseULID}` with `databases:write`.
Success is 200 with the managed database metadata. Standard SDK error policies
preserve 400 `invalid_request`, 409 `name_taken` and 421 `wrong_region`.
The SDK does not automatically replay an uncertain mutation; reconcile by immutable
ULID before an explicit retry. Existing name and immutable ULID read selectors remain supported.

After the owning API makes rename available, callers must update name-based
connection strings and handles. Server identity, data, placement and credential
scope guarantees require producer integration and rollout evidence.
