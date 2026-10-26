---
title: CLI access and execution context
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# CLI access and execution context

Use `ts auth login`, approve the displayed URL/code, then check `ts whoami`, `ts orgs` and `ts projects`. Login progress goes to stderr in JSON mode; tokens are never printed. `ts auth logout` revokes the saved session and removes its credential file.

`ts -scope read -expires 30d keys create reports example` requests a database-restricted key with `databases:read` and `documents:read`. Default `-scope read-write` also requests both write scopes. Server permissions still apply; `-allow-cidr` restricts client networks and `-expires` accepts `30d`, `90d`, `365d` or `never` (default).
Capture a newly created key once in a private file or secret store; exclude secrets from logs and shell history. `keys list` returns safe metadata, expiry and allowed networks; `keys revoke <id>` revokes access. `TERRASCALE_API_KEY` selects API-key authentication for database commands; organization keys also need `-project <id>`.

`ts context set my-org my-project example` resolves organization/project IDs and verifies the optional database before saving. `ts context show` and `ts context clear` work offline; saved selectors live beside credentials in `context.json`, without tokens.
`ts db info @` explicitly uses the saved database. An explicit database name wins. Explicit `-org` or `-project` before the command overrides saved selectors and discards dependent database defaults, so supply the database name with those flags. To use `@`, select a matching context and omit the override flags. API-key commands ignore sign-in context. Another account's saved defaults fail until cleared or explicitly overridden; every request still checks access.

`ts -project my-project db delete example` asks for the exact database name; collection drop and function delete ask for the resource name. Organization/project deletion asks for the resolved ID. Unattended commands require explicit `-yes` before the command; consent never grants permissions.

For automation, inspect `ts --json db info preview`, verify the project and branch lineage, then supply `-expected-id <ULID>` and `-yes` before `db delete`:

```sh
ts --json -yes -expected-id 01ARZ3NDEKTSV4RRFFQ69G5FA9 db delete preview
```

Successful JSON output is `{"name":"preview","deleted":true}`. The server requires the name to resolve to that immutable ID; a mismatch returns `409 database_identity_mismatch`. The CLI uses the dedicated `DELETE …/databases/{database}/if-id/{expectedId}` route without a placement PATCH. An unsupported server returns 404; the CLI never falls back to unconditional deletion. The ID must be a canonical uppercase ULID. An identity match does not validate branch lineage or make reset atomic.

Server `import` creates/reuses the destination and stages the source **before** preview confirmation. Refusal leaves that staged job for `import status` or `import cancel`; it does not roll back staging. `-yes` consents to starting the current preview, but blocking issues still stop it. Confirmed/running/completed reruns reattach; an unconfirmed new preview needs consent again.

`ts billing` prints https://dash.terrascale.tech/billing; `ts --json billing` returns `{"url":"https://dash.terrascale.tech/billing"}`. It works offline without opening a browser; the dashboard may require sign-in. Billing writes stay in the dashboard. Platform billing/usage API-key scopes and a platform Go SDK are planned; `ts usage [<database>]` continues to report TerraBase project/database usage.

See [automation and JSON](./automation) for output and exit contracts.

</div>
