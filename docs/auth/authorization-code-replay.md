# Authorization-code replay

RFC 6749 §4.1.2 says that when an authorization code is used more than once,
the server should revoke the tokens issued from it. ts-auth does this. For
authorization codes, this file replaces the "winner stays usable" replay
behavior in [regional/ts-757-winner-reconciliation.md](regional/ts-757-winner-reconciliation.md).
Refresh-token replay is documented separately.

- **What counts as a replay.** A token request presents a code whose stored
  entry is already redeemed. The request must also be one that could have
  redeemed the code itself: the same client (authenticated, if confidential),
  the same `redirect_uri`, and a PKCE `code_verifier` that matches the code's
  S256 challenge. A request that fails any of these, or whose code cannot be
  validated, gets `invalid_grant` and changes nothing.
- **What a replay does.** It revokes the code's authorization. Every refresh
  token issued from the code, in every region, is refused once the
  revocation replicates, and ts-auth (including UserInfo) stops accepting
  the grant's access tokens. Access tokens that other services validate
  offline stay valid until they expire (at most 5 minutes).
- **Concurrent redemptions.** When several requests redeem the same code at
  once, one of them issues tokens. Each of the others counts as a replay,
  so the grant ends, including the tokens the first request returned. The
  client must start a new authorization.
- **Regional branches.** Detection reads the stored redemption, not the
  regional winner of the redemption claim. A replay therefore ends the grant
  even when the redemption it repeats lost to another region.
- The error response is `invalid_grant` with the description "This
  authorization code was already used. Its sign-in has ended. Sign in again
  to continue."
