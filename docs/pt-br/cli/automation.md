---
title: CLI automation and JSON
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# CLI automation and JSON

The Go `ts` CLI is the shipped distribution. Check `ts --version` and `ts help <command>` for your installed `0.0.X` release; see [installation](../cli.md).

Use `ts --json db list` for machine output. `--json` may follow the command; other global options go before it. Finite success is one JSON value plus newline; empty lists are `[]`.
Exit codes are **0** success, **1** operational failure and **2** invalid arguments. Stdout contains only payloads; prompts/progress use stderr. Only explicit `keys create` returns a one-time secret; login never prints tokens.

Errors use `{"error":{"code":"invalid_arguments","message":"…"}}`, with optional HTTP `status` and safe `requestId`. Inspect uncertain writes before retrying. A writer failure can leave incomplete output even after a mutation committed.

`doc list`, `export`, `db changes` and `shell` use NDJSON: `{"type":"record","data":…}` followed by exactly one `{"type":"complete"}` on successful termination, or `{"type":"error","error":…}` and nonzero exit. Missing complete means incomplete; JSON SIGINT exits 1. A live changes feed follows until interrupted.

The [JSON Schema](https://docs.terrascale.tech/cli-output.schema.json) maps individual commands in `x-commandResults` to success definitions. Validate finite results once and streams line by line; also check exit status and terminal order. Metadata may contain additional fields; preserve opaque versions/cursors and large integers.

| Commands | Successful JSON |
| --- | --- |
| `version`, `help`, `completion` | Version object; root/scoped help metadata; `{shell,script}` code asset |
| `auth login/logout`, `whoami` | `{signedIn}`; `{sub,email}` |
| `orgs`, `projects`, `keys list` | Metadata arrays; create/info/rename return metadata; key create returns `{api_key,key}`; revoke returns `{id,revoked}` |
| `db list/create/info/branch/regions/delete`, backups | Metadata arrays/objects; database delete returns `{name,deleted}`; restore returns database metadata |
| `collection list/create/drop`, `doc get/put/delete/import` | Collection arrays/metadata; document envelope; write receipt; `{imported}` count |
| `schema`, `ttl`, `index`, `usage` | Schema object; TTL path; index array/metadata/drop receipt; usage counters |
| `sql`, `exec` | `{matched,truncated,rows}`; `{columns,rows,rows_affected,last_insert_rowid}` |
| `functions`, server `import` | Metadata/list/log arrays; invoke `{body}` or `{bodyBase64}`; delete receipt; final import job |
| `migrate`, `context`, backup settings | Path/run/status/diff objects or arrays; saved selectors/clear receipt; policy/window/plan/destination metadata |

See [access and execution context](./access) for scopes, consent and dashboard billing.

</div>
