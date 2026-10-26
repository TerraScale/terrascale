# Project API-key authentication

Build with `Client::for_project_api_key(project_id, api_key).build()?`, reading credentials from your application's secret storage. The constructor validates locally; the first authenticated operation exchanges the key.
`Client::for_project` and `Client::builder` still accept static, already exchanged bearer tokens. Static credentials are not refreshed.

Keys go only to `https://auth.terrascale.tech/terrascale/oauth/token`; redirects are never followed. A data endpoint override does not change the auth destination.
Exchange uses HTTP Basic with the key's ULID as client ID and its full value as password. The form contains `grant_type=client_credentials` and `project`; omitting scope inherits the key's grant.
Data endpoints receive only bearer tokens, and data routes retain their selected project scope. Never log credentials.

An asynchronous lock serializes exchange and refresh across client clones. Refresh occurs 60 seconds before expiry, or at half-life for tokens shorter than 120 seconds, measured from before exchange starts.
A 401 triggers at most one refresh and replay per logical HTTP request, including writes because 401 refuses admission. A late 401 cannot invalidate a newer token shared by another clone.
A 403 never triggers refresh. Transport failures and other statuses retain the existing replay-safety policy.

Raw changes feeds acquire a cached or refreshed token whenever opened. Typed feeds do so on reconnect, retaining their last delivered cursor in `Last-Event-ID`. A refused connection refreshes once after 401; another refusal ends the feed normally with an error.

`Error::TokenExchange { status, code }` distinguishes `token_exchange_refused`, `invalid_token_response`, and `token_transport_error`. The pending attempt or replay has not been sent; any preceding request was refused with 401, so `is_uncertain()` is false.
Errors expose no upstream body, error text, credential, or token. Grants require a nonempty Bearer token and positive integer `expires_in`; oversized, malformed, or already stale grants are rejected.

DECISION: A dedicated constructor preserves bearer-token compatibility. The issuer and refresh margin are fixed with no new settings. Exchange follows Python's first-party project-key contract; TypeScript's generic OIDC resource/scope helper serves a separate grant interface. Loopback HTTP tests substitute the issuer through private test access, without a production override.
