# Retired headless authentication

Owning producer: `TerraScale/terrascale@1d823d8534fc5035859a47e263691d0c7cb7b744`.
`docs/auth/parity.md` SHA256:
`f403a9cd914a3617535dd036530411427186290824373dd992460839eee0dbc2`.
It records removal of headless authentication on 2026-10-22; dashboard uses
hosted pages as a registered OIDC client. RFC 7009 revocation remains served.

The historical `/headless/v1/email-code/start`, `/sign-up`,
`/password-reset/start` and `/password-reset/complete` operations are removed
from the SDK. No replacement HTTP route, caller-IP header, signup adapter or
email-code grant is invented. The Headless client/configuration, password/email-code
grants and exclusive historical fake-server tests are also removed. Its refresh
and revoke methods belonged to that retired client; Device refresh/revoke and
registered-client OIDC methods remain supported. Shared Tokens and error values
retain their public types and exact values.

Device, API-key/client-credential and token exchange, shared Tokens/error codes,
platform/project APIs and registered OIDC remain distinct supported surfaces.
Use the existing Device refresh/revoke or registered-client OIDC methods for
those grants. Preserve client registration and callback authority.

Historical fake servers do not establish deployed support. Contract/source
absence and compiled public-surface removal are retirement evidence; qualified
supported-flow production proof remains a separate gate. No signup, email or
production calls are needed to implement this removal.
