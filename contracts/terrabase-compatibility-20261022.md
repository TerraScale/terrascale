# TerraBase compatibility check, 2026-10-22

Compared ts-go main `0cbf30022b2080788e31f04dc830da456209d20a` with
TerraScale/terrascale main `e7fa366ddb289f5612c93d316b316fb92c568151`.
Read `docs/terrabase/management-api.md` and the implemented HTTP features;
server paths below are relative to `src/TerraBase` at that revision.
The earlier [compatibility matrix](terrabase-compatibility.md) pins an older
server revision; this file records the current differences.

## Corrections

1. **Grouped changes:** `Features/Documents/DatabaseChanges.cs:319` emits an
   SSE ID only on the final change in an origin/version group. The SDK
   previously rejected earlier events. It now yields those events with an
   empty cursor and retains its last confirmed reconnect checkpoint. Persist
   only nonempty `ChangeEvent.Cursor` values; interruption can replay a group.
   Fake streams cover complete groups, interrupted replay and EOF before any
   checkpoint.
2. **Operator management authentication:**
   `Features/UserAuth/OperatorHandler.cs:36` refuses operator authentication
   when Authorization is present. `Hosting/Listeners.cs:273` requires the
   internal peer credential. `Management.As` now sends
   `X-TerraBase-Peer-Credential` and the acting user, without a bearer header.
   `WithToken` keeps user JWT authentication. Fake requests check the two
   credential boundaries; they do not qualify deployed operator access.
3. **Unknown write outcomes:** `Features/Documents/DocumentRoute.cs:47` and
   `Features/Documents/DocumentService.cs:380` describe `write_outcome_unknown` after an ambiguous
   commit or missing remote acknowledgement. The SDK exposes
   `ErrWriteOutcomeUnknown`; these API errors also match `ErrCommitUncertain`
   so existing uncertainty handlers recognize them. The original response
   code/body remain available. Fake responses verify a mutation is never
   replayed, including conditional writes and a supplied Retry-After header.
4. **Durability values:** `Features/Documents/DocumentModels.cs:17,131`
   specifies `remote_durable` and assertions-only `unchanged`. Both now have
   exported constants and fake write/transaction decoding coverage. Unknown
   future durability values continue to decode as strings.

5. **Refusal codes (server main `d79bb8d0`):** `Errors/ErrorResponse.cs:23`
   sends `remote_region_unavailable`, `confirmation_busy` and `catalog_stale`
   as 503s with Retry-After for requests that executed nothing; replay-safe
   requests now retry them. `Hosting/BadRequests.cs:63` sends 503
   `copy_faulted` while a copy awaits certified recovery; it is exported and
   not retried. `quota_exceeded` (429) and `request_timeout` (408) are
   exported. If-Match/ETags remain implemented (`Features/Documents/ETags.cs`)
   and TerraBase errors remain `{error:{…}}`, not RFC 9457, so the SDK keeps
   both.

## Limits

This is source comparison and deterministic fake HTTP coverage, with no
TerraBase binary or production request. Existing missing public SDK methods
for backups/restores, branches/placement, indexes/recommendations, JSON
schemas, query shapes and saved queries remain coverage gaps; the legacy
management methods do not substitute for those public JWT routes. Proposed
shape protocol and deployed issuer/authorization qualification remain
separate work, as recorded in the earlier matrix.
