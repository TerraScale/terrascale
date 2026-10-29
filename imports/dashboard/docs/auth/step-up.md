# Sensitive dashboard operations

Account deletion, personal access token issuance (including MCP OAuth's 90-day
PAT), API-key creation and rotation, and OAuth client creation
require hosted confirmation at ts-auth. Confirmation uses password or email
verification and enrolled MFA. A recent OIDC login alone does not authorize an
operation. PAT issuance still creates its own independent OIDC grant afterward.

The dashboard keeps the opaque purpose/audience proof inside encrypted TerraBase
`auth-flows` state. The browser receives only the hosted confirmation URL and an
HttpOnly correlation cookie. The fixed return path is `/auth/step-up/callback`;
query-bearing callbacks are refused. Pending intents are single use, expire with
the proof, and bind the initiating account, browser session, selected workspace
and original form. Switching accounts or sessions invalidates continuation.

The dashboard consumes `create_pat/ts-dashboard` immediately before storing the
independent credential. API-key and OAuth-client operations send their
purpose-bound `ts-auth` proof through the SDK in
`X-TerraScale-Step-Up-Proof` to the owning service. API/MCP
credential mutations without hosted confirmation receive an actionable refusal;
they do not get an unattended credential issuance path.

Deletion generates a canonical 32-byte receipt before confirmation. Consuming
`delete_account/ts-dashboard` returns a secret operation grant. Before canceling
any Stripe subscription, the dashboard persists that grant, the exact receipt,
the initiating session and the candidate sole-organization scope in an encrypted
`account_deletions` operation. Pending confirmation state is separate and cannot
resume deletion. A missing grant or lost consume response requires a new proof
and a new random receipt before any Stripe or erasure effect; the previous
receipt hash may already be reserved by an unusable grant.

The initial auth DELETE sends both the original receipt and
`X-TerraScale-Deletion-Authorization`; receipt or proof alone cannot authorize it.
Once auth accepts the receipt, 202/204 receipt status resumes that durable auth
job for up to 15 days, including after token revocation. If receipt status is 404,
the dashboard clears its encrypted operation and deletion cookie and requires
new hosted confirmation with a new random receipt. It makes no resumed Stripe
call or DELETE using that unaccepted operation; a fresh deletion attempt cannot
be intercepted by its old record. Organization membership is rechecked
before Stripe cancellation. Dashboard cleanup follows auth's confirmed account
and organization erasure status; uncertain answers retain local records.

This change deliberately requires confirmation in previously unattended
credential/deletion journeys. Encrypted operations without a consumed grant are
not accepted as authorized deletion operations. Proofs, operation grants and
newly issued secrets must never be logged. Step-up proofs and deletion grants
must never appear in browser URLs or storage.

The pre-existing CIDR-only HTTP adapter remains for GET project allowlists and
PUT key/project allowlists; these operations are outside this step-up change and
remain outside this flow. Begin, consume, credential issuance,
rotation, OAuth-client creation and deletion continuation use SDK methods.
