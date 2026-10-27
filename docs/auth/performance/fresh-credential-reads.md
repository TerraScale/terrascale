# Fresh credential reads

Credential ancestry is checked from TerraBase on every authentication. Independent ancestor checks run in groups of at most eight, with exact duplicate bindings read once per invocation. A user-state check still reads the user before its email uniqueness claim. Every ancestor must match; missing state, a losing credential claim or a revoked device authorization still refuses the credential. Storage errors and cancellation propagate. No state is retained across requests.

The MFA factor-before-user ordering is unchanged: an activation or global sign-out during the factor read must be visible to the subsequent user read.

Project lists still authorize the organization from fresh membership and organization documents. Owners and admins inherit the highest project role, so their lists need no explicit project-grant reads. Lower organization roles and collaborators still read their grants. Responses, pagination, storage formats and transaction authority assertions are unchanged.

The `TerraScale.Authentication.Storage` activity source records point reads, uniqueness reads and paged queries as children of the current request. The `ts_auth.storage.read.duration` histogram reports seconds by collection, operation, serving region and completion/failure. Spans and metrics omit document keys, tenant/user ids and credentials; no new setting is required. HTTP client spans retain the actual upstream connection timing.
