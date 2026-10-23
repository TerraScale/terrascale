# Recovery retry for ts-auth-owned projects

Production projects enrolled from ts-auth project tokens have catalog storage envelopes and a verified project binding, but no TerraBase membership mirror. The original retry endpoint therefore hid even the real ts-auth owner behind `404 not_found`.

The internal retry route now accepts supplemental live authority proof for an enrolled project:

- Existing internal peer credential (`X-TerraBase-Peer-Credential`).
- Acting user (`X-TerraBase-User-Id`), one canonical ULID; canonical IDs starting with `5` are valid.
- That same user's current ts-auth **sign-in access token** in `Authorization: Bearer …`.

This is the only operator route that accepts the additional bearer proof. The client listener still returns 404, and a bearer token cannot replace the internal peer credential or acting-user header. Use the existing authenticated session; do not mint an API key, persistent recovery grant, or membership record. Deliver credentials through a protected header stream/file, never shell arguments, output, source control, or the audit reason.

TerraBase checks the configured ts-auth issuer's existing UserInfo, organization, and project APIs. UserInfo must return the acting user; the organization must return the exact ID and `owner` or `admin`; the project must return the exact organization/project IDs and effective `admin`. A project collaborator alone is insufficient. Each API validates the same current sign-in token; expired/revoked tokens, wrong identities, missing targets, redirects and malformed responses fail closed. Network failures, rate limits and provider errors return 503. Reads retain existing bounded response/concurrency admission and no redirects or credential logging.

The catalog must contain the exact enrolled organization/project/database and supported issuer binding, without legacy memberships or a legacy authority binding. The exact target is checked again after live verification, outside of which no catalog transaction is held. Legacy storage continues to use its original local owner/admin check without the supplemental bearer header; a ts-auth administrator cannot use this route to take over legacy storage.

## Retained throwaway-tenant rerun

After deploying the merged commit, the rollout owner reruns the same internal request on the retained `fr1-proof4` tenant, with its actual owner's current sign-in token as supplemental proof. All route IDs are ULIDs, including the **database ID**, rather than its name:

`POST /v1/management/organizations/{organization}/projects/{project}/databases/{database}/recovery/retry`

Body: `faultedIncarnation` (canonical ULID; use `00000000000000000000000001` for the no-matching-record check, not a marker containing `I` or `L`), `reason` (1–256 characters, no controls). No request manufactures a fault or rebuild record.

Expected observations with a current trusted catalog:

1. Missing peer credential → 401; public listener → 404.
2. Wrong actor, ordinary organization member, project collaborator, or rejected sign-in proof → 404 and no rebuild dispatch.
3. Genuine owner/admin with live proof but no matching durable rebuild → 409 `no_matching_rebuild`.
4. Only for an independently approved throwaway-tenant fault with an existing certified rebuild: the matching request may return 204 after certification. Busy/pending rebuilds remain 409; completed requests cannot start another rebuild.
5. Stale catalog → 503 `catalog_stale`, including if freshness expires during authority verification. Do not relax freshness to obtain a different result.

Freshness/trust checks before authorization and dispatch, exact faulted-incarnation matching, bounded reason hashing/audit, certified rebuild behavior, and rollback/data-task grace are unchanged. Revoke the existing sign-in using the normal ts-auth flow if needed; no new persistent permission needs cleanup.

## Validation recorded for this fix

The Fast integration filter `OperatorRecoveryTests|ProjectBindingTests` passed 51 cases, including the enrolled owner/admin path, denied roles/identities, malformed/redirect/revoked/unavailable proof, peer/listener isolation, deletion during the online check, freshness expiring during the check, and retained legacy ownership checks.

FR1 originally observed owner 404 on the retained production tenant. A follow-up on 2026-10-23 at 21:21:45Z reached that same tenant but returned 503 `catalog_stale`, before owner authorization. Successful live owner admission and certified recovery remain the rollout owner's post-merge drill; local coverage does not claim that production proof.
