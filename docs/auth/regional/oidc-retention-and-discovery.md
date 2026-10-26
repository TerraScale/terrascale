# OIDC retention and discovery

Every serving replica runs OIDC cleanup every ten minutes. A pass reads at most
100 records from each of `oidc_tokens` and `oidc_authorizations` through the SDK's
document-list cursor, in primary-key order. It does not run a filtered full scan,
count the collection, or drain an unbounded backlog in one pass. A full traversal
takes multiple ticks; concurrent creation behind the cursor is picked up on the
next traversal. Cursors are scheduling hints. Losing them on restart repeats work
safely; an invalidated cursor restarts the traversal.

Authorization-code, device-code, user-code, refresh, access and ID-token records
become eligible thirty-one days after creation, and only when expired. Expired
access/ID metadata does not accumulate indefinitely after its grant's horizon.
Ad-hoc authorization records become eligible thirty-one days after creation, whether
valid or revoked. This horizon exceeds the supported one-minute code redemption
or ten-minute device approval window, the maximum thirty-day project refresh family,
and the final five-minute access token. Platform refresh families have a fourteen-day
maximum; project families follow the absolute session policy, up to thirty days
from authentication. Refresh cannot slide either family deadline.
Thus expiry of an individual authorization code is insufficient for deleting its
replay evidence: the later refresh family can still depend on it. Permanent grants,
records without dates and records without a finite token expiry are retained.

Deletion names the version observed on the page. Token deletion and release of
its exact reference-handle lookup and its `credential:oidc` redemption-winner claim
share one conditional transaction. A competing token or claim change aborts the
whole deletion. The pruner does not release inherited session, user-state or
authorization bindings. Those checks continue for every retained descendant.
Revoked authorization evidence survives the full horizon; missing token, winner
claim or authorization fails closed after cleanup too. No live descendant of a
supported fixed-lifetime family reaches this horizon.

Thirty-one days is an **eligibility horizon**, not a physical-erasure deadline or a
promise of removal at a particular minute. Backlog, serving capacity and outages
affect the next completed traversal. Unknown/malformed records need investigation;
cleanup does not infer an expiry. Blank identifiers/reference handles and invalid
dates are retained without stopping examination of the rest of the page.
TerraBase's replicated deletion tombstones, retained history, backups and restore
certification remain TerraBase's contracts.
This service does not shorten those tombstones or certify restored regional data.
Lifetime changes must revisit this proof before changing the retention horizon.

Authored `OidcRetentionTests` cover real SDK HTTP/store boundaries, multiple pages,
tenant restriction, concurrent pruners, a concurrent token update, the horizon,
atomic claim retention, malformed-page handling, a final project access token
across the thirty-day boundary, and rejection after a real TerraBase/auth restart.
The project-boundary case advances an injected clock; the restart case explicitly
ages synthetic record metadata. Neither measures a month of production operation
or proves arbitrary partition/recovery behavior.
Required CI and isolated-stack qualification must supply passing run evidence.

Discovery's `claims_supported` includes the emitted standard claims: `iss`, `sub`,
`aud`, `exp`, `iat`, `auth_time`, `email`, `email_verified`, `azp`, `nonce`, `at_hash`,
`jti`, `client_id` and `scope`. Their presence depends on token type and scope.
ID-token/UserInfo email fields require `email`; platform access JWTs include
verified-email identity claims independently. `profile` currently adds no fields.
Internal ancestry, authentication epoch/state and authorization claims are not
advertised. `DiscoveryClaimsTests` compares served metadata with independently
validated code-flow JWTs and real UserInfo responses. Only known application
claims and OpenIddict's private namespace are excluded from the observed union;
unexpected public claims fail the comparison, and credential ancestry in a public
token fails explicitly. This checks metadata accuracy, not OIDC certification.
Anonymous production discovery on 2026-10-26 still advertised only `aud`, `exp`,
`iat`, `iss`, `sub`; authenticated live acceptance
belongs to the release proof and has not been performed by this implementation.
