# Token entries carry their ancestry from creation

Every non-limiter TerraBase write from ts-auth waits for another region's durable
ack (`TB-Durability: remote_durable`, see [rate-limit durability](../rate-limit-durability.md)),
so a token request costs roughly one cross-region round trip per sequential write.

Each stored token entry is bound to the credential ancestry that `RedeemExactlyOnce`
resolved for the request. `OidcTokenStore.CreateAsync` stores those bindings in the
write that creates the entry. Previously a separate handler re-read each new entry
(with its whole ancestry check) and rewrote it, which added one remote-durable write
per issued token:

A refresh or code grant that issues access, refresh and identity tokens went from
eight OIDC writes to five; an authorization code issued from a bound session saves one.

An entry is never stored without its bindings, and no credential is returned before
its entry exists, so the revocation and replay rules are unchanged.
`TokenWriteBudgetTests` keeps the refresh grant at five OIDC writes.

Measure live with `scripts/auth/flow-latency.py` (synthetic account, deleted at the end).
