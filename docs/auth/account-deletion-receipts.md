# Account deletion receipts

`DELETE /v1/terrascale/account` (`delete_account`) keeps its authenticated, no-header contract. Clients may supply `X-TerraScale-Deletion-Receipt`: 32 cryptographically random bytes encoded as canonical unpadded base64url (43 characters, final character in `AEIMQUYcgkosw048`). Generate it before submitting; retain it privately for polling. Never put it in URLs, logs, browser storage or analytics. Invalid or repeated header values return 400 `invalid_request`.

The authenticated request creates a SHA-256 digest record in the dedicated `account_deletion_receipts` collection, atomically with the initial closure intent or a version-pinned attachment to the current intent. The record contains only opaque subject, generation and candidate organization ULIDs plus an expiry. Reusing a capability for another subject or generation returns 409 `deletion_refused`. No plaintext capability is persisted. Unauthenticated requests cannot create or bind receipts.

`GET /v1/terrascale/account/deletion` (`account_deletion_status`) requires the same capability header, with no bearer token. It returns no personal data and grants no mutation rights. Responses use `Cache-Control: no-store`:

- 204: the account's `DeletionJob.Key` fence, created atomically by `Users.DeleteAsync`, proves logical deletion of the exact closure generation.
- 202: that account generation remains pending.
- 404 `not_found`: unknown capability, expired receipt, or noncandidate organization.
- 409 `deletion_refused`: no matching current generation and no matching completion fence. User absence and token rejection never certify completion.
- 400 `invalid_request`: malformed capability or organization ULID; 503 for unavailable storage.

Optional `?organization=<ULID>` restricts proof to a candidate organization captured from the initial sole scope (or already-owned organization intents when attaching). A 204 requires both account completion and that organization's own deletion fence. A surviving candidate remains 202, including one that became shared before the account fences committed. An unknown or noncandidate organization returns 404. Dashboard projection or billing cleanup must require each organization's explicit 204; account 204 alone is insufficient.

Receipts expire 15 days after creation, without renewal on retries. Expired lookups return 404; digest-record pruning can be added separately. Receipt expiry never cancels durable closure work. Completion is logical auth deletion, not physical erasure, backup erasure or other products' cleanup. This adds a collection and optional generation metadata to deletion jobs; it needs no engine storage-format change.

The first accepted DELETE temporarily blocks new password and email-code sessions through a transactional absence check on the account intent. It preserves credentials and existing account state until safe removal; refusal cancellation removes the intent and restores sign-in. Existing sessions and access tokens are revoked by logical account deletion, not by a pending response. The worker resumes automatically at startup and every two seconds, in batches of 20; pending retries become due after two seconds. No repeated submit is necessary. Convergence, outages and refusals can extend completion; there is no fixed completion-time guarantee.
