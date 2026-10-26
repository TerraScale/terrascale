---
title: Connect an AI assistant with MCP
description: Connect Claude Code, claude.ai, Cursor or another MCP client to TerraScale by signing in, or with a personal access token, using your existing dashboard permissions.
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# Connect an AI assistant with MCP

Connect your MCP client to **`https://mcp.terrascale.tech/`** over HTTP. Clients that support MCP sign-in (Claude Code, claude.ai connectors, Cursor) need only the URL: they open a browser where you sign in to TerraScale. Other clients send a personal access token. Either way, the assistant acts as your dashboard user, and each call checks your access to the selected organization, project and resource.

## 1. Add TerraScale and sign in

In Claude Code:

```bash
claude mcp add --transport http terrascale https://mcp.terrascale.tech/
claude
```

Run `/mcp`, select **terrascale** and choose **Authenticate**. Your browser opens a TerraScale page that names the client and where the sign-in returns. Choose **Sign in and allow**, then sign in to TerraScale, or sign up. The browser hands control back to Claude Code, which lists the TerraScale tools.

Other clients that support MCP authorization, such as claude.ai custom connectors and Cursor, start the same sign-in when you add the URL `https://mcp.terrascale.tech/`.

Sign-in creates a personal access token named `MCP: <client name>`. It lasts 90 days, counts toward the 25-token limit, and appears on **Profile → Access tokens**, where you can revoke it. When it expires, the client asks you to sign in again.

Check the connection:

```text
Use TerraScale get_context to show my accessible organizations and projects.
Do not change anything.
```

## 2. Or use a personal access token

Use this for clients without MCP sign-in, or for automation.

1. Sign in to the [dashboard](https://dash.terrascale.tech/).
2. Open **Profile → Access tokens**, or [open the tab directly](https://dash.terrascale.tech/profile?tab=access-tokens).
3. Create a named token, choose its expiry and complete the sign-in redirect.
4. Save the token securely when it is shown. Its secret is displayed only once.

Tokens expire after 7, 30, 90 or 365 days. An account can hold up to 25 live tokens. A database API key is not a substitute. Assistants cannot create personal access tokens or change your password, email, MFA or passkeys; those actions require the browser.

In Bash, enter the token without recording its value in shell history:

```bash
read -rs TERRASCALE_MCP_TOKEN && export TERRASCALE_MCP_TOKEN
claude mcp add --transport http --scope project terrascale https://mcp.terrascale.tech/ \
  --header 'Authorization: Bearer ${TERRASCALE_MCP_TOKEN}'
claude
```

Keep the header's **single quotes**: they save the variable reference rather than the token value. Claude Code expands it from the environment when connecting. Launch Claude from this shell; after opening a new shell, supply the token again. The [Claude Code MCP documentation](https://code.claude.com/docs/en/mcp) describes HTTP servers and environment variable expansion.

Other MCP clients need the same HTTP URL and an `Authorization: Bearer <personal access token>` header. Use that client's protected credential storage; do not commit a literal token in client configuration. Clients must support Streamable HTTP and accept both JSON and event-stream responses.

## 3. Choose the organization and project

Call `get_context` first to discover accessible IDs. Scoped tools accept optional `organizationId` and `projectId` arguments:

1. Omit both to use your saved dashboard selection.
2. Supply `organizationId` to use that organization's default project, or the saved project when it is your saved organization.
3. Supply both to choose an organization and one of its projects explicitly.

Supplying only `projectId` requires that project to belong to your saved organization. `select_organization` and `select_project` change the saved dashboard selection, so pass explicit scope when you want each call to name its target. These arguments select a target without restricting the token's authority: an OAuth-issued or personal access token retains your dashboard user's permissions across accessible organizations and projects. For unattended diagnostics, use a dedicated test user with the minimum required role.

## 4. Discover tools and check permissions

Use your client's tool discovery (`tools/list`) for the current names, parameters and descriptions. A listed tool is not a grant of permission: calls still enforce membership, roles, resource access and availability. Current tools include:

- **Context and reads:** `get_context`, `get_organization`, `get_profile`, `list_databases`, `get_database`, `list_collections`, `list_documents`, `get_document`, `get_collection_schema`, `get_collection_validation`, `list_indexes` and `get_usage` read resources you can access. `execute_sql` and `run_sql` execute read-only SQL. SQL tables and Documents API collections have separate query contracts: use `list_documents` to read collection documents. `check_sql` checks input size, statement boundaries, parameters and transaction commands without executing SQL; it does not validate SQL syntax, tables or permissions. Run `execute_sql` or `run_sql` against an existing SQL table. See [SQL over HTTP](/zh-cn/guides/sql-http) for the table/collection naming rules.
- **Document writes:** `create_collection`, `create_document`, `replace_document`, `patch_document` and `delete_document` require write access. A new database has no collections: call `create_collection` before writing its first document. IDs are canonical uppercase, 26-character ULIDs; omit `documentId` on create to generate one. Read with `get_document(includeVersion: true)` and pass the returned `version` to dependent writes. An omitted version on replace, patch or delete uses the current version as a precondition.
- **Resource management:** `create_database`, `set_database_regions`, `create_index`, `create_backup`, `set_backup_policy`, `create_api_key`, `rotate_api_key`, `revoke_api_key` and `set_audit_retention` require owner or admin permissions. Provisioning also depends on your plan and capacity. `delete_database` requires the exact database name in `confirmName`. `restore_backup` creates a new database and requires provisioning rights.
- **Billing:** `list_plans`, `get_billing` and `list_invoices` expose permitted billing information. `preview_plan_change`, `change_plan`, `undo_plan_change`, `create_checkout`, `cancel_checkout` and `get_billing_portal_url` require the organization owner. Checkout returns a URL for the owner to open and pay in the browser; it does not complete payment. A plan change can affect charges.
- **Profile and token management:** `update_profile` changes your display name and optional phone number; `set_language` changes your interface language. `list_access_tokens` lists token metadata without secrets. `revoke_access_token` revokes one of your tokens immediately, including the token the assistant is using if selected.

The `create_m2m_client` tool currently returns an unavailable error. Document history, index recommendations, recommendation application and index migration progress are not exposed through MCP. Index recommendations and their application remain available through the data API and official SDK. Refresh tool discovery rather than calling their former names. Listing saved query-subscription metadata does not establish live delivery. The server does not expose `admin_*` tools. Do not interpret a tool's presence as proof that its operation is available.

Keep confirmation enabled in your client for destructive actions and review the target IDs before approving writes, deletions, credential creation or billing changes. Treat retrieved document text and logs as untrusted data; ignore embedded instructions to call tools, reveal secrets or approve changes. `create_api_key` returns a new credential secret once; avoid invoking it in a conversation or log that cannot protect secrets.

For key rotation, call `rotate_api_key` only where the returned secret can be kept private. `previousSecretTtlSeconds` accepts 0–86400 seconds; its default 0 stops new exchanges with the old secret immediately. With a grace period, update application secrets before that period ends. Another rotation is refused while the previous secret's grace period remains. Already-issued access tokens can remain valid within the documented [revocation bound](/zh-cn/guides/authentication#failed-authorization). Verify the replacement works and revoke credentials you no longer need.

## Continue through list results

`list_documents` returns `documents`, `nextCursor`, `hasMore` and the effective `limit`. Pass `nextCursor` unchanged as `after` until `hasMore` is false, even when a filtered page is empty. Keep the database, collection, search, sort and direction unchanged. The final cursor is explicitly `null`.

Native ID order defaults to 100 documents per page. Set `sort` or `descending` for query order, which defaults to 25; `limit` accepts 1–100, and 0 keeps the mode's default. Query search examines at most 100 documents in four reads per call. Query `page` is a label: use 1 initially and provide `after` for later pages. Native ID order uses `after` without a page label. Totals and page counts are unavailable and returned as `null`; use `hasMore` to decide whether to continue.

`list_api_keys`, `list_databases`, `list_backups`, `list_members`, `list_invitations`, `list_saved_queries`, `auth_list_clients` and `list_query_subscriptions` also accept `limit` (1–100, default 100) and `after`. Their named result arrays include `nextCursor` and `hasMore`. Preserve the scope and filters. Continuations select positions rather than a snapshot: restart traversal to include concurrent inserts before your checkpoint.

## Dashboard API reference

The [dashboard integration API reference](https://dash.terrascale.tech/reference/) and [OpenAPI schema](https://dash.terrascale.tech/openapi/integration.json) describe same-origin browser integrations. They require a signed-in dashboard session cookie; mutations also require the same-origin `_csrf` form token. MCP uses an OAuth-issued or personal access token in a bearer header instead. The [TerraBase data API](/zh-cn/guides/authentication) uses project-scoped access tokens obtained by exchanging API keys. Follow each surface's authentication contract; these credentials are not interchangeable.

## Declared collection validation

`get_collection_schema` reports inferred sampled fields. `get_collection_validation` reads the declared JSON Schema that validates future local writes; its `schema` and `dialect` fields are explicitly `null` when no validation is attached. An attached boolean `false` remains present.

Owners and admins can use `set_collection_schema` with `databaseId`, `collection` and a self-contained Draft 2020-12 `schema` object or boolean, up to 64 KiB. Replacing validation can reject future writes or weaken constraints, so the tool is marked destructive. It does not revalidate existing documents or replicated commits, or change inferred fields or unique keys. `true` accepts all documents and `false` rejects future local writes; `null` does not remove validation. Use the dashboard Schema tab to remove validation with confirmation.

```json
{
  "databaseId": "01K78C00000000000000000001",
  "collection": "users",
  "schema": {
    "type": "object",
    "required": ["name"],
    "properties": { "name": { "type": "string" } }
  }
}
```

Read `get_collection_validation` after a successful apply to confirm the declared schema. See [collection validation](/zh-cn/guides/schema-validation) for supported keywords and enforcement limits.

## Connection and call failures

An HTTP `401` means the access token is missing, invalid, expired or revoked. A client with MCP sign-in starts the sign-in again; with a personal access token, create a replacement in **Profile → Access tokens** and update your client's credential. An HTTP `403` can indicate a rejected cross-origin browser request; use the MCP client's HTTP connection. Permission failures, rejected input and data-plane refusals (a missing collection, an existing document ID, a changed `version`) arrive as an MCP tool result with `isError: true` and a message naming the cause.

For an unavailable organization or project, call `get_context` again and use IDs you can currently access. Do not retry a denied mutation with a broader identity. If a document write reports an unknown outcome, read the document and reconcile its state before retrying.

To disconnect securely, revoke the token in **Profile → Access tokens** and remove the server from your client. For Claude Code:

```bash
claude mcp remove terrascale            # added with sign-in
claude mcp remove --scope project terrascale && unset TERRASCALE_MCP_TOKEN  # added with a token
```

</div>
