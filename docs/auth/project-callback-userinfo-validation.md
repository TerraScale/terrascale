# Project OAuth callback URL validation

`POST /v1/{project}/clients` rejects any `redirect_uris` entry with nonempty URL
userinfo (an embedded username or password), returning `400 invalid_request`.
This applies to HTTPS callbacks and HTTP loopback callbacks, including localhost,
IPv4 and IPv6. URLs must also be absolute and contain no fragment; HTTP remains
limited to loopback hosts.

This is an explicit greenfield input-validation tightening: project redirect
allowlists already reject userinfo. Exact matching against the client's registered
redirect URI remains binding, as does the project's nonempty redirect allowlist.
An empty project allowlist does not bypass registration validation. There is no
API payload shape, response shape or storage format change, and no compatibility
shim or migration is introduced.
