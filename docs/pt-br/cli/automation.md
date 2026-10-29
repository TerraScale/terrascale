---
title: CLI automation and JSON
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# CLI automation and JSON

The Go `ts` CLI is the shipped distribution. Check `ts --version` and `ts help <command>` for your installed `0.0.X` release; see [installation](../cli.md).

Use `ts --json db list` for machine output. `--json` may follow the command; other global options go before it. Finite success is one JSON value plus newline; empty lists are `[]`.
Exit codes are **0** success, **1** operational failure and **2** invalid arguments. Stdout contains only payloads; prompts/progress use stderr. Only explicit `keys create` and `keys rotate` return a one-time secret; login never prints tokens.

Errors use `{"error":{"code":"invalid_arguments","message":"…"}}`, with optional HTTP `status` and safe `requestId`. Inspect uncertain writes before retrying. A writer failure can leave incomplete output even after a mutation committed.

`shell` uses NDJSON: `{"type":"record","data":…}` followed by exactly one `{"type":"complete"}` on successful termination, or `{"type":"error","error":…}` and nonzero exit. Missing complete means incomplete; JSON SIGINT exits 1.


| Commands | Successful JSON |
| --- | --- |
| `version`, `help`, `completion` | Version object; root/scoped help metadata; `{shell,script}` code asset |
| `auth login/logout`, `whoami` | `{signedIn}`; `{sub,email}` |
| `orgs`, `projects`, `keys list` | Metadata arrays; create/info/rename return metadata; key create/rotate return `{api_key,key}`; revoke returns `{id,revoked}` |
| `db list/create/info/branch/regions/delete`, backups | Metadata arrays/objects; database delete returns `{name,deleted}`; restore returns database metadata |
| `usage` | Usage counters |
| `migrate`, `context`, backup settings | Path/run/status/diff objects or arrays; saved selectors/clear receipt; policy/window/plan/destination metadata |

See [access and execution context](./access) for keys, consent and dashboard billing.

See the [management command reference](./management) for lifecycle, backup policy and placement.

</div>
