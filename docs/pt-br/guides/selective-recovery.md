---
title: Selective recovery
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# Selective recovery

Preview historical documents, compare them with current records, then approve individual IDs to recover into the **original database**. Unlike a [full restore](/pt-br/guides/backups-and-restore), selective recovery uses isolated temporary service scratch, creates no customer database and needs no spare database slot. It restores document content, not historical schemas, access rules or collection settings.

Use the same project and original database throughout. Preview requires `databases:write` and `documents:read`; apply requires `databases:write` and `documents:write`, with access rechecked. Only customer-owned live writable source databases qualify; platform-managed identity and dashboard databases refuse recovery with `409 recovery_erasure_fenced`. Each selected collection must currently exist. If one was dropped, explicitly recreate it with the intended current schema and permissions before previewing again. Select up to 16 collections and 100 historical live records, with at most 1 MiB of combined historical/current JSON. Omitting `ids` selects the collection's historical live records; use explicit ID batches when the limit is exceeded.

## CLI: preview, review, approve

Install the CLI revision containing `recovery` with `go install terrascale.tech/sdk/cmd/ts@main`. Set `PROJECT` to your project ID and `DATABASE` to its database name. Use `ts auth login` or supply `TERRASCALE_API_KEY` through your secret manager. The CLI uses the platform backup destination.

<<< @/snippets/selective-recovery-cli.sh{bash}

Replace the example time and IDs with a proven [restore point](/pt-br/guides/backups-and-restore#read-the-restore-window) and your document IDs. Review each record's `historical`, `current` and `currentVersion` in `preview.json` before creating `approved.json`. `current: null` can still have a nonnull version for a tombstone. The CLI never approves all records automatically. Editing historical JSON or versions in the file cannot change the server's approved historical content.

## HTTP: the same approval boundary

Use Bash, `curl` and `jq`, with `PROJECT`, `DATABASE` and `TERRASCALE_ACCESS_TOKEN` supplied through your environment or secret manager; credentials stay out of command arguments. Replace the example time and IDs, then independently review this HTTP preview before writing an approval list.

<<< @/snippets/selective-recovery-http.sh{bash}

Preview returns `{approval,expiresAt,records}`; each record has `{collection,id,historical,current,currentVersion}`. For a customer backup destination, add `"destination":"customer"` to the preview body; it defaults to `platform`, with no fallback. Apply accepts only `{approval,records:[{collection,id}]}` and returns the transaction acknowledgement `{offset,durability,results}` with new document versions.

## Conflicts, retention and cleanup

Approval expires after 10 minutes and is bound to the actor, source database and service process. A different node or service restart requires a new preview. The service rebuilds scratch and checks historical content and current versions against the accepting region's applied state, like `If-Match`. If any approved record is stale there, the whole apply is refused; re-preview and review again. Apply is one atomic document write in that region, not global serialization: unseen concurrent regional writes follow normal [conflict rules](/pt-br/concepts/replication). Current schema, access and document/storage quotas still apply; preview reserves no quota. An ongoing recovery for the same database/node can answer `503 capacity` with `Retry-After`; wait before retrying. After an uncertain apply response, read the affected records before retrying.

Recovery stays within the existing backup destination and plan's [PITR retention](/pt-br/limits#plan-entitlements); it does not extend retention or bypass completed erasure. Service scratch is temporary and is cleaned up after the call. The local preview contains customer data and a short-lived approval token: keep it private, never commit it, and remove preview, selection and approval files after verifying the recovered records. Local copies require your own deletion.

</div>
