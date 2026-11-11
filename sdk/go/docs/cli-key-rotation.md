# API key rotation and scope selection

Sign in with `ts auth login`; API-key administration requires a signed-in user,
not `TERRASCALE_API_KEY`. Select the project with `-project` before the command.

`ts -project <project> keys rotate <key-id> 30m` rotates the selected project's
key and gives its previous secret thirty minutes of grace. Grace is required:
use `0s` for immediate replacement, or a Go duration resolving to whole seconds
up to `24h`. The CLI validates the canonical key ULID and grace before auth.
The SDK sends `previous_secret_ttl` as integer seconds (0 through 86400).

Rotation completes its step-up in the terminal: the command
requests a purpose-bound proof from `POST /v1/terrascale/step-up`, prints the
hosted confirmation URL and retries the mutation with
`X-TerraScale-Step-Up-Proof` every five seconds. Approve with a fresh password
or email code, plus two-factor authentication when enrolled; approval consumes
the proof and the secret appears once on stdout. The proof never leaves the
process and its URL never contains it. An unapproved proof expires after five
minutes and the command exits with `step_up_required` (403): run it again for a
fresh confirmation. The wait honours Ctrl+C.

The key ID, grants, database restriction, CIDRs and expiry remain unchanged.
The new secret appears once on stdout. Text diagnostics contain no secret.
`--json` emits the typed `{api_key,key}` result, using the SDK's snake_case names;
`api_key.previous_secret_expires_at` is the old secret's deadline, or null for
immediate replacement. Lists never expose either secret. Store stdout directly
in a private secret store or file and exclude it from CI logs and shell tracing.

Rotation is always available, including during an active grace. A new rotation ends the older previous secret immediately and keeps the current secret for the requested grace; at most two secrets remain valid.
Project-key rotation requires project member authority. The service can also
reject stale sign-in credentials with `authentication_conflict` (409); sign in
again. Operational failures exit 1; invalid arguments exit 2. Errors retain safe
status/code metadata and omit remote message bodies. Beyond step-up there is no
automatic retry: a network, cancellation or output failure may happen after
rotation committed, and the returned secret cannot be retrieved later. Inspect
state before retrying. Already-issued access tokens keep their own expiry;
rotation does not revoke them.

Creation requests no proof and prints no confirmation URL: ts-auth creates
the key at once. It keeps its explicit `read-write` default: `databases:read`,
`databases:write`, `sql:read`, `sql:write`. For read-only work use
`ts -scope read -expires 30d keys create reports <database>`, which requests only
`databases:read` and `sql:read`. The optional database narrows the key;
`-allow-cidr` narrows its networks. Server permissions still limit grants.
Rotation preserves those restrictions and does not escalate a read-only key.
