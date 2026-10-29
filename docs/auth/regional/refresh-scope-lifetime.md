# Refresh scope and lifetime contract

A refresh request without `scope` requests the original authorized scopes. An
explicit equal set keeps those scopes; a subset restricts the new access token.
The response's `scope`, when present, describes that access token. Under RFC 6749
§5.1 the field may be omitted when it equals the explicit requested set. Inspect
the granted scopes rather than treating the request as proof of permission.

An expanded set is refused, including a known scope absent from the original
authorization. Unknown scopes are refused too. Neither failure consumes the
refresh token. OpenIddict 7.7.1 performs these checks before redemption and
filters the access principal. The replacement refresh token retains the original
scope, as required by RFC 6749 §6: a later request can ask for the original set
again. A reduced access token does not permanently reduce consent. Use revocation
and a new authorization to replace the authorization itself.

Sign-in grants only `openid`, `email`, `profile` and `offline_access`. Access-token
scopes and UserInfo's email disclosure follow the requested subset. Platform
access JWTs retain their verified-email identity claims for TerraBase even without
the email scope; that claim does not grant an API permission. ID-token email
destinations follow the original sign-in scope. `profile` currently grants no
additional profile fields. Organization/project API permissions require the
separate role-checked exchange; a refresh cannot add them.

A platform refresh family has a **14-day absolute maximum** from initial token
redemption. A project refresh family follows its absolute session policy, up to
**30 days from authentication**; a shorter policy shortens that deadline.
Rotation never extends either deadline. Protocol proof retention is thirty-one
days so it also covers the longest project family and its final access token.
There is also a **64-binding proof budget**, shared with inherited authentication
and session ancestry. Every redemption that creates successor credentials adds a
binding. The number of remaining refreshes therefore depends on the initial
ancestry, and is less than 64. These deadlines are maxima, not a promise that the
family can rotate for that long. Five-hour estimates assume a particular
rotation cadence and ancestry; they are not a measured production lifetime.

At the budget boundary the server returns `invalid_grant` with “This sign-in
reached its rotation limit. Sign in again to continue.” It refuses before
consuming the credential or extending the family. Clients should serialize
refreshes, stop automatic retries on this error, discard the unusable refresh
handle, and start a fresh authorization-code/PKCE sign-in. Complete authentication
again if the inherited browser session also exhausted its budget. A fresh sign-in
starts new ancestry. Do not repeatedly retry a consumed token.

No ancestry compaction is implemented. Dropping old bindings would stop checking
whether an ancestor lost regional reconciliation. A safe checkpoint would need
durable evidence that every serving region agrees on every covered winner, retain
authorization/user-state revocation checks, and prevent delayed replication or
restored data from reopening a losing branch. The current store does not expose
that checkpoint guarantee. Regional proof-owner review and partition/recovery
integration evidence are prerequisites for replacing ancestor checks.

`RefreshScopeContractTests` covers HTTP issuance, signed access scopes, UserInfo
disclosure, rejection without consumption, and successor authority on real
TerraBase. `TokenContractTests.Refresh_at_the_lineage_budget_requires_signin_without_consuming_or_extending_the_family`
covers the proof boundary and fresh sign-in recovery. Test source is not a passing
test result; required CI and controlled own-client production acceptance remain
separate evidence. No SDK signature change or new SDK capability is implied.
