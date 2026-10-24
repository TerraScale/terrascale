# API key expiry and IP allowlists

## Expiry

A key may get `expires_at` at creation (the dashboard offers 30, 90 or 365 days, or none). From that instant ts-auth
answers its client-credentials exchange with `401 invalid_client`, like a revoked key, and its last token stops working
within 60 seconds.

## IP allowlists

An API key and a project each hold `allowed_cidrs`: up to 32 IPv4 or IPv6 CIDRs or addresses, stored canonical (host
bits cleared, an address as `/32` or `/128`, IPv4-mapped addresses as IPv4). Empty allows any address.

- Set at key creation (`allowed_cidrs` in the create body) or replaced with
  `PUT .../api-keys/{key}/allowed-cidrs` (the role that creates the key) and
  `PUT .../projects/{project}/allowed-cidrs` (project admin).
- ts-auth puts the key's list on its tokens as `key_cidrs` and the token's project's list as `project_cidrs`, each a
  space-separated string, only when non-empty. Organization keys get the list of the project the token is for.
- TerraBase checks every list a token carries, offline, on every client route and on the Postgres wire: the client
  address must fall in each. Otherwise HTTP answers `403 {"error":{"code":"ip_not_allowed"}}` and Postgres refuses the
  connection with SQLSTATE `28000`. A malformed list fails authentication.
- A change reaches tokens issued afterwards, so it applies within one token lifetime (60 seconds).
- Sign-in tokens (the dashboard's token exchange) carry no list: allowlists restrict API keys only.

## Client address

TerraBase believes the client address the ts-orch edge names only when the connection comes from a loopback, private
(RFC 1918, `fc00::/7`) or NetBird (`100.64.0.0/10`) peer; the client listener is reachable from the internet only
through the edge. HTTP takes the rightmost `X-Forwarded-For` hop. Postgres takes the source of an optional PROXY
protocol v2 header the edge writes before the client's bytes; without one, the socket peer counts.
