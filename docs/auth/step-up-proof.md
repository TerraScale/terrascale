# Purpose-bound step-up proof

Sensitive operations require a fresh credential check, including MFA when active.
A valid management access token or hosted session alone cannot authorize account
deletion, API-key creation/rotation, or confidential OAuth client creation.
Dashboard PAT issuance also consumes a proof before storing any credential.

The backend calls `POST /v1/terrascale/step-up` with its management bearer and
`{purpose,audience,return_uri}`. A 201 response returns `proof`, `step_up_url`,
and `expires_at`. Keep `proof` on the backend and send the browser only to the
hosted URL. The callback must be `/auth/step-up/callback` on a registered
dashboard callback origin, without a query or fragment.

Supported pairs are `delete_account` with `ts-dashboard` or `ts-auth`,
`create_pat` with `ts-dashboard`, and `create_api_key`, `rotate_api_key`, or
`create_oauth_client` with `ts-auth`. The hosted page identifies the operation
and account. It requires a fresh password, or one explicitly requested email
code for a passwordless account, followed by a fresh active MFA factor.
Opening the page sends no email. An attempt requests at most one code and uses
the existing mail and credential budgets. Email codes are bound to that attempt
and cannot sign in or approve another attempt.

Proofs expire five minutes after creation, including time spent verifying MFA.
TerraBase stores their secret hashes and account, epoch, authentication state,
purpose and audience bindings. Approval alone performs no product operation.
Consumption conditionally updates the proof and requires the current account
state in the same transaction. Expired, unapproved, cross-account, wrong-purpose,
wrong-audience and replayed proofs fail with 403 `step_up_required`.

Auth-owned operations receive `X-TerraScale-Step-Up-Proof` and consume it before
mutation. The dashboard calls `POST /v1/terrascale/step-up/consume` with
`{proof,purpose,audience,operation_receipt}` before issuing a PAT or performing
any account-deletion side effect. PAT consumption returns 204. Deletion
consumption returns 200 with a separate `operation_grant` bearer capability only
after consumption completes before the proof's deadline. These
mutations must never be automatically retried after an ambiguous response.

Dashboard deletion additionally supplies a canonical base64url encoding of 32
random bytes as `operation_receipt`. Consumption atomically creates a grant
bound to that receipt hash and the same account authentication state, for 15
days. Persist the returned capability before any side effect. Subsequent
authenticated auth DELETE requests use both that exact
`X-TerraScale-Deletion-Receipt` and the returned capability in
`X-TerraScale-Deletion-Authorization` to continue the authorized deletion. This grant
cannot authorize any other operation or receipt; the original proof remains
consumed. A lost consume response before dashboard authorization is persisted
requires a new step-up attempt and a new receipt before any dashboard side effect;
the old receipt hash may already hold the unusable grant. A rejected late
consume never reveals the new capability, so its persisted hash cannot authorize
deletion. Direct proof-bearing DELETE requests with a receipt return the
capability in `X-TerraScale-Deletion-Authorization`; receipt-only status reads do
not expose it. Continuations retain the verified primary credential lineage and
fail closed if that ancestry loses reconciliation.

Production auth has one GRU authority and one conditional storage writer for
proof consumption; production startup rejects other auth regions independently
of email configuration. This does not claim exclusive consumption across
independent regional writers. Request creation is budgeted per account and
expired proof, passcode and grant records are pruned.

Issued API-key secrets and confidential OAuth-client secrets retain the fresh
primary verification's credential lineage, including the account authentication
state. Global sign-out, password reset, or a losing verification branch prevents
further exchanges with those secrets. Closing only the initiating browser
authorization does not revoke the independent credential. During API-key rotation,
the previous secret retains its own original lineage throughout its grace period.
Already issued access tokens retain their existing bounded lifetime. Immutable
credential winner claims remain after expired step-up code material is pruned so
long-lived descendants can still validate their ancestry.

This is an explicit breaking authorization change. Clients that omit proof
must obtain it through hosted verification. Existing account-deletion status
receipts and completion evidence retain their status-reading semantics. No
storage-format bump or application-data migration is required; the new proof
and grant collections are auth-owned TerraBase entities.
