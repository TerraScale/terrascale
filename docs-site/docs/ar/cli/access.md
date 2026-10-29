---
title: CLI access and execution context
---

<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

<div lang="en" dir="ltr">

# CLI access and execution context

Use `ts auth login`, approve the displayed URL/code, then check `ts whoami`, `ts orgs` and `ts projects`. Login progress goes to stderr in JSON mode; tokens are never printed. `ts auth logout` revokes the saved session and removes its credential file.

`ts -expires 30d keys create reports example` requests a database-restricted key. Server permissions still apply; `-allow-cidr` restricts client networks and `-expires` accepts `30d`, `90d`, `365d` or `never` (default).
Capture a newly created key once in a private file or secret store; exclude secrets from logs and shell history. `keys list` returns safe metadata, expiry, allowed networks, creator, creation time and nullable last-use time (a successful token exchange, up to five minutes stale); `keys revoke <id>` revokes access. `TERRASCALE_API_KEY` selects API-key authentication for database commands; organization keys also need `-project <id>`.

`ts context set my-org my-project example` resolves organization/project IDs and verifies the optional database before saving. `ts context show` and `ts context clear` work offline; saved selectors live beside credentials in `context.json`, without tokens.
`ts db info @` explicitly uses the saved database. An explicit database name wins. Explicit `-org` or `-project` before the command overrides saved selectors and discards dependent database defaults, so supply the database name with those flags. To use `@`, select a matching context and omit the override flags. API-key commands ignore sign-in context. Another account's saved defaults fail until cleared or explicitly overridden; every request still checks access.

`ts -project my-project db delete example` asks for the exact database name; organization and project deletion ask for the resolved ID. Unattended commands require explicit `-yes` before the command; consent never grants permissions.

For automation, inspect `ts --json db info preview`, verify the project and branch lineage, then supply `-expected-id <ULID>` and `-yes` before `db delete`:

```sh
ts --json -yes -expected-id 01ARZ3NDEKTSV4RRFFQ69G5FA9 db delete preview
```

Successful JSON output is `{"name":"preview","deleted":true}`. The server requires the name to resolve to that immutable ID; a mismatch returns `409 database_identity_mismatch`. The CLI uses the dedicated `DELETE …/databases/{database}/if-id/{expectedId}` route without a placement PATCH. An unsupported server returns 404; the CLI never falls back to unconditional deletion. The ID must be a canonical uppercase ULID. An identity match does not validate branch lineage or make reset atomic.

To branch an inspected source, use its ID from `ts --json db info main`:

```sh
ts --json -expected-id 01ARZ3NDEKTSV4RRFFQ69G5FA9 db branch main preview
```

Success returns the same typed database metadata as ordinary `db branch`. The dedicated `POST …/databases/{database}/branches/if-id/{expectedId}` route requires that source ID before copying; a mismatch exits 1 with `{"error":{"code":"database_identity_changed","message":"Conflict","status":409}}` (and optional `requestId`). Unsupported servers return 404 without an unguarded branch retry. Malformed IDs and identical source/target names exit 2 before authentication. The admitted source ID is retained during copying, but concurrent deletion can still interrupt it. Verify project, ownership and branch lineage separately; this does not make reset atomic.

`ts billing` prints https://dash.terrascale.tech/billing; `ts --json billing` returns `{"url":"https://dash.terrascale.tech/billing"}`. It works offline without opening a browser; the dashboard may require sign-in. Billing writes stay in the dashboard. `ts usage [<database>]` reports project and database usage.

See [automation and JSON](./automation) for output and exit contracts.

`ts keys rotate <id> 30m` returns the new secret once with thirty minutes of grace for the previous secret. Grace is required, from `0s` to `24h` in whole seconds; `0s` replaces immediately. Capture stdout privately and never log it. An active grace returns `rotation_pending` (409); inspect state before retrying uncertain failures. Rotation preserves the key ID, scopes and restrictions.

</div>
