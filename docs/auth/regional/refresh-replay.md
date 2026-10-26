# Refresh-token replay and grant revocation

This policy supersedes the consumed-refresh-token replay rule in
`ts-757-winner-reconciliation.md`. Immutable credential ancestry and regional
winner reconciliation remain in force.

Reference refresh tokens rotate on every successful use, with no reuse grace
period. Presenting an already redeemed refresh token through its authenticated
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
