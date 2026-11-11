# Purpose-bound step-up proofs

The pending contract is recorded in `contracts/auth/step-up-provenance.json`.
The owning auth contract must merge before the SDK is published. HTTP-fake tests
verify wire behavior; they do not qualify a live auth/dashboard journey.

Call `auth.Platform.BeginStepUpAsync(accessToken, new(purpose, audience, returnUri))`.
Keep the returned `Proof` on the backend; navigate the browser only to `StepUpUrl`.
The pending proof expires after five minutes. The registered dashboard origin's
`/auth/step-up/callback` is required, without query or fragment. An existing
sign-in alone does not approve the proof; auth requires fresh credentials and any active MFA factor.

Supported pairs are `delete_account` with `ts-auth` or `ts-dashboard`, `create_pat`
with `ts-dashboard`, and `delete_organization`, `start_ownership_transfer`,
`change_plan`, `create_api_key`, `rotate_api_key`, `create_oauth_client`,
`update_oauth_client`, `rotate_oauth_client`, `change_email`, `add_passkey`,
`enroll_totp`, `delete_passkey`, `create_webhook_endpoint` and `rotate_webhook_secret`
with `ts-auth`. For auth operations, pass the approved proof to the required
`stepUpProof` argument on API-key rotate, ownership-transfer start,
`auth.Projects.CreateClientAsync`, `UpdateProjectClientAsync` and `RotateProjectClientSecretAsync`,
`auth.Platform.RequestEmailChangeAsync` or `auth.Platform.DeleteAccountWithProofAsync`.
API-key creation consumes no proof: `CreateOrganizationApiKeyAsync` and `CreateProjectApiKeyAsync`
still take a `stepUpProof` and send it, and ts-auth creates the key without checking it.
The three organization proofs, `delete_organization`, `start_ownership_transfer` and `change_plan`,
bind the exact organization: pass its id as `OrganizationId` when minting. Organization deletion is one call:
pass the approved, unconsumed proof to `auth.Platform.DeleteOrganizationAsync(accessToken, organizationId, proof)`.
It consumes the proof at `POST /v1/terrascale/step-up/consume` for that organization, then sends the
organization `DELETE` with the same proof, which ts-auth spends; never consume the proof first. A refused
or failed consume sends no `DELETE`. Neither request is retried, so a later attempt needs a new proof.
Outside a consume request body, the SDK sends a proof only as
`X-TerraScale-Step-Up-Proof`. The old proof-free account deletion method is removed;
the receipt overload remains for an authorized deletion continuation.

For direct auth deletion, retain a random canonical 32-byte base64url receipt
before calling `DeleteAccountWithProofAsync(accessToken, proof, receipt, cancellationToken)`.
The SDK sends both proof and receipt headers once and returns
`TerraScaleAuthAccountDeletion` with `Completed` (204 true, 202 false) and the
optional `OperationGrant` from `X-TerraScale-Deletion-Authorization`.
Retain that secret grant on the backend with the receipt. A caller-controlled
continuation uses `DeleteAccountAsync(accessToken, receipt, operationGrant, cancellationToken)`
while the sign-in remains valid. It sends both receipt and deletion authorization;
the single-use proof must never be replayed. After token revocation, query
`AccountDeletionCompletedAsync(receipt, cancellationToken)` with only the receipt.

For dashboard operations, call `auth.Platform.ConsumeStepUpAsync(accessToken,
new(proof, purpose, "ts-dashboard", operationReceipt))` before any side effect.
`create_pat` requires a null receipt, returns 204, and the SDK returns null.
A plan change names its organization: `new(proof, "change_plan", "ts-auth", null, organizationId)`;
ts-auth refuses a proof minted for another organization with `step_up_required` and leaves it unconsumed.
`delete_account` requires the exact canonical 32-byte base64url receipt and
returns 200 `{operation_grant}`. The SDK returns that secret only after a successful
acknowledgement. Store it with the receipt before side effects and pass both to
`DeleteAccountAsync(accessToken, receipt, operationGrant, cancellationToken)`.
The grant binds the same account, epoch, authentication state and fresh credential
bindings for 15 days. Receipt alone cannot authorize continuation.

A rejected or lost first response provides no acknowledged grant, even if the
server committed a transaction. Do not continue from the receipt alone or replay
the proof automatically. Use the retained receipt to query status; unknown status
remains an error. To obtain authorization again after an uncertain deletion consume,
start a fresh approved proof and generate a fresh receipt from 32 random bytes;
submit both in the new consume. Never reuse the old receipt: its hash may already
be reserved by the committed grant, causing the new grant creation to conflict.
Keep the old receipt only for status queries about that attempt. Secret grant DTOs
and deletion results redact grants in `ToString()`.

Consume and credential mutations send once. A failure or lost response never
permits automatic replay or proves whether the side effect occurred. Obtain fresh
authorization when needed. `step_up_required` is exposed as a safe error code;
proof and consume DTOs omit proof/receipt secrets from `ToString()`.

API-key creation preserves the optional `AllowedCidrs` list as `allowed_cidrs`.
Absent/null or empty allows any address; the producer accepts up to 32 IPv4/IPv6
CIDRs or addresses and returns canonical CIDRs. API-key metadata from creation,
rotation, get and list includes required non-null `AllowedCidrs`; an empty list
means no key-level restriction. Missing/null metadata is an invalid response,
so restrictions are never silently replaced by an empty list in the SDK.
