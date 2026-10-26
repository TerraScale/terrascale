# API key rotation and scope selection

Sign in with `ts auth login`; API-key administration requires a signed-in user,
not `TERRASCALE_API_KEY`. Select the project with `-project` before the command.

`ts -project <project> keys rotate <key-id> 30m` rotates the selected project's
key and gives its previous secret thirty minutes of grace. Grace is required:
use `0s` for immediate replacement, or a Go duration resolving to whole seconds
up to `24h`. The CLI validates the canonical key ULID and grace before auth.
The SDK sends `previous_secret_ttl` as integer seconds (0 through 86400).

The key ID, grants, database restriction, CIDRs and expiry remain unchanged.
The new secret appears once on stdout. Text diagnostics contain no secret.
`--json` emits the typed `{api_key,key}` result, using the SDK's snake_case names;
`api_key.previous_secret_expires_at` is the old secret's deadline, or null for
immediate replacement. Lists never expose either secret. Store stdout directly
in a private secret store or file and exclude it from CI logs and shell tracing.

An active prior grace returns `rotation_pending` (409); wait until its deadline.
Project-key rotation requires project member authority. The service can also
reject stale sign-in credentials with `authentication_conflict` (409); sign in
again. Operational failures exit 1; invalid arguments exit 2. Errors retain safe
status/code metadata and omit remote message bodies. There is no automatic retry:
a network, cancellation or output failure may happen after rotation committed,
and the returned secret cannot be retrieved later. Inspect state before retrying.
Already-issued access tokens keep their own expiry; rotation does not revoke them.

Creation keeps its explicit `read-write` default: `databases:read`,
`databases:write`, `documents:read`, `documents:write`. For read-only work use
`ts -scope read -expires 30d keys create reports <database>`, which requests only
`databases:read` and `documents:read`. The optional database narrows the key;
`-allow-cidr` narrows its networks. Server permissions still limit grants.
Rotation preserves those restrictions and does not escalate a read-only key.
