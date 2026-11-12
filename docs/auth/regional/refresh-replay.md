# Refresh-token replay and grant revocation

This policy supersedes the consumed-refresh-token replay rule in
`ts-757-winner-reconciliation.md`. Immutable credential ancestry and regional
winner reconciliation remain in force.

Reference refresh tokens rotate on every successful use, with no reuse grace
period, except those of the fixed grants described in
[grants that do not rotate](#grants-that-do-not-rotate). Presenting an already redeemed refresh token through its authenticated
client ends that authorization grant. The request returns `invalid_grant` and
requires a fresh sign-in. Its latest refresh token and other descendants cannot
continue the grant. Other authorizations belonging to the same user remain valid.
Clients must serialize refresh requests; competing requests can end the grant
and require reauthentication.

Before revocation, the server checks the integrity-validated principal's token
type, entry ID, authorization ID and presenter against the stored refresh entry
and the request client. Confidential clients must authenticate successfully.
A wrong client, failed client authentication, or an invalid token does not by
itself revoke a grant. Public clients have no client secret: client registration
and credential ancestry do not establish sender possession. This policy follows
the rotation response in [RFC 9700 section 4.14.2](https://www.rfc-editor.org/rfc/rfc9700.html#section-4.14.2).

An unused descendant rejected solely because its regional ancestry lost is a
different case. Rejecting it does not end the winning grant. If a consumed refresh
parent is replayed, the grant ends regardless of which holder won the earlier
redemption. Storage ordering cannot distinguish the legitimate holder from a
holder of a stolen token.
The replay check reads persisted consumption evidence when ancestry rejection
masks a spent entry's effective status. Ancestry loss alone is not that evidence.

Revocation conditionally updates the authorization in TerraBase through the
existing SDK store. It covers descendants still being issued without scanning
token entries. A storage failure propagates rather than granting tokens or
pretending revocation succeeded. Credential validation also checks for replay
when a refresh loses its consumption transaction or becomes spent after initial
validation. Entity caching stays disabled.

Independent writable regions can acknowledge competing redemptions before
replication. A region rejects the revoked grant once it observes the revocation;
there is no fixed convergence deadline during a partition. This policy does not
add cross-region coordination or change the accepted offline JWT model:
already-issued access JWTs accepted offline can remain usable until their signed
expiry, at most five minutes for newly issued tokens. Store-backed ts-auth checks
reject the grant after observing revocation.

## Grants that do not rotate

A personal access token or an MCP sign-in is a `ts-dashboard` grant that must
live as long as the user chose, up to a year, while the dashboard renews its
five-minute access token every few minutes. When the dashboard redeems that
grant's authorization code it sends `ts_refresh_expires_at`: the Unix time it
also stores as the token's expiry. ts-auth honours it only on the
`authorization_code` grant of the confidential `ts-dashboard` client, after the
client authenticated, and only within (now, now + 365 days + 5 minutes]: the
5 minutes absorb a dashboard clock that runs ahead of ts-auth's, while the
dashboard itself never offers more than 365 days by its own clock. Any other
value from that client answers `invalid_request` and leaves the code unredeemed
(the dashboard then shows "This token could not be created with that expiry");
other clients, other grants and refresh requests ignore the parameter. The
grant's refresh token expires at that instant.

That refresh token never rotates. A refresh writes no redemption, creates no
successor and adds no ancestry binding, and its answer carries no
`refresh_token`, so the client keeps the one it has; nothing sent later changes
its expiry. Every use still checks the client's authentication, the token
entry's status, the authorization's status, the credential ancestry and the
user's epoch, state, second factor and that the user is not disabled. RFC 7009
revocation (which the
dashboard sends when a token is revoked on Profile → Access tokens), sign-out
everywhere, a password reset, turning MFA on or off and account deletion end
it at once, as they end a rotating grant.

Reuse detection does not apply to these grants: presenting the token again is
their normal use. RFC 9700 section 4.14.2 allows this for a confidential
client, whose authentication binds the refresh token to it: without the
dashboard's client secret the token endpoint answers `invalid_client`, and
through another client `invalid_grant`. A holder of both the token and the
client secret can use the grant until one of the revocations above or its
expiry; with rotation, its first reuse would also have ended the grant.

Browser sign-ins and every other client and grant keep rotating as described
above, inside their fixed 14-day family (a project's session lifetime on a
project issuer).

## Rotation limit

A rotating grant carries its whole ancestry, and every redemption adds one
binding to it. Once the next refresh would exceed the limit, the refresh
answers `invalid_grant` ("This sign-in reached its rotation limit") and the
user signs in again. The limit (`Credentials.MaxLineage`) is 224 bindings: the
64 a sign-in code can carry, plus 160 refreshes, one every 4.5 minutes for 12
hours. That is how often the dashboard renews its five-minute access token (30
seconds before it expires) through a 12-hour browser session, so a session in
continuous use lives its full life. A longer dashboard session needs a larger
limit.

The limit keeps the cost of one grant bounded: checking an ancestry reads one
row per binding, every token entry stores its ancestry, and a step-up
confirmation asserts each binding of the caller's ancestry in a single
TerraBase request, which runs at most 256 statements.
