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

Sign-in creates a personal access token named `MCP: <client name>`. It lasts up to 90 days, counts toward the 25-token limit, and appears on **Profile → Access tokens**, where you can revoke it. The client's registration can expire sooner after inactivity, as described below. When access expires, the client asks you to sign in again.

Check the connection:

```text
Use TerraScale get_context to show my accessible organizations and projects.
Do not change anything.
```

### Registration expiry and reconnecting

A new OAuth client registration expires after 24 hours if sign-in never completes. After sign-in completes, the registration expires after 30 days without a successful sign-in, token issuance or authenticated request using one of its tokens. Opening the consent page, denying access or sending an invalid token does not extend that period.

An expired registration stops its associated tokens from authenticating, even if their 90-day token deadline has not arrived. Remove and add TerraScale again in your client, then authenticate to create a new registration. Personal access tokens created separately in the dashboard keep their selected expiry.

For custom MCP client integrations, use authorization-server discovery rather than constructing client IDs. Dynamic registration returns a canonical uppercase, 26-character ULID as `client_id`; keep it unchanged. Registration JSON is limited to 16 KiB, including whitespace and UTF-8 encoding. Oversized bodies return HTTP `413`; malformed or unsupported registration metadata returns an OAuth JSON error with HTTP `400`. Registration management endpoints are not advertised; knowing a public client ID does not authorize changing or deleting it.

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

Database content tools are being updated for SQL. Use your client's tool discovery to see which tools your key can call.

</div>
