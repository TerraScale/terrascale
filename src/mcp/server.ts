import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { createTerraScaleClient, type TerraScaleClient } from "../factory.js";
import { toUlid } from "../ids.js";
import type { JsonObject, JsonValue } from "../models.js";
import type { TerraScaleResult } from "../result.js";
import type { TerraScaleMcpConfig } from "./config.js";
import { z } from "zod";

const version = "0.1.0";
const ulid = z.string().trim().regex(/^[0-9A-HJKMNP-TV-Z]{26}$/i, "Expected a ULID.");
const page = z.object({ skip: z.number().int().min(0).max(10_000).default(0), take: z.number().int().min(1).max(100).default(25) });
const repository = z.object({ repositoryId: ulid });

export function createTerraScaleMcpServer(config: TerraScaleMcpConfig, client?: TerraScaleClient): McpServer {
  const sdk = client ?? createTerraScaleClient(config);
  const server = new McpServer({ name: "terrascale-mcp", version }, {
    instructions: "TerraScale MCP is read-only. Start with projects, databases, and repositories; inspect schema before TQL. Results are bounded. It never performs mutations, backups, restores, or schema changes.",
  });

  staticResources(server);
  prompts(server);

  server.registerTool("terrascale_list_projects", {
    title: "List TerraScale projects", description: "Lists projects in an organization. Omit organizationId to inspect the configured project.",
    inputSchema: { organizationId: ulid.optional(), ...page.shape }, annotations: { readOnlyHint: true },
  }, async ({ organizationId, skip, take }) => result(organizationId === undefined
    ? sdk.organizations.getProject()
    : sdk.organizations.listProjects(toUlid(organizationId), { skip, take })));

  server.registerTool("terrascale_list_databases", {
    title: "List databases", description: "Lists databases for a project.", inputSchema: { projectId: ulid.optional(), ...page.shape }, annotations: { readOnlyHint: true },
  }, async ({ projectId, skip, take }) => result(sdk.databases.list(projectId === undefined ? undefined : toUlid(projectId), { skip, take })));

  server.registerTool("terrascale_list_repositories", {
    title: "List repositories", description: "Lists repositories accessible to the caller.", annotations: { readOnlyHint: true },
  }, async () => result(sdk.repositories.list()));

  server.registerTool("terrascale_get_schema", {
    title: "Get repository schema", description: "Returns repository schema and whether validation is enabled.", inputSchema: repository.shape, annotations: { readOnlyHint: true },
  }, async ({ repositoryId }) => result(sdk.schema.get({ repositoryId: toUlid(repositoryId) })));

  server.registerTool("terrascale_list_documents", {
    title: "List documents", description: "Lists a bounded page of repository documents.", inputSchema: { ...repository.shape, ...page.shape }, annotations: { readOnlyHint: true },
  }, async ({ repositoryId, skip, take }) => result(sdk.documents.list({ repositoryId: toUlid(repositoryId) }, { skip, take })));

  server.registerTool("terrascale_get_document", {
    title: "Get document", description: "Gets one repository document by ULID.", inputSchema: { ...repository.shape, documentId: ulid }, annotations: { readOnlyHint: true },
  }, async ({ repositoryId, documentId }) => result(sdk.documents.get({ repositoryId: toUlid(repositoryId) }, toUlid(documentId))));

  server.registerTool("terrascale_query_tql", {
    title: "Run read-only TQL", description: "Runs a bounded read-only TQL query. Mutating statements are refused.", inputSchema: { ...repository.shape, query: z.string().trim().min(1).max(8_000), ...page.shape }, annotations: { readOnlyHint: true },
  }, async ({ repositoryId, query, skip, take }) => {
    if (!isReadOnlyTql(query)) return toolError("validation", "TQL mutations are not available through this MCP server.");
    return result(sdk.query.execute({ repositoryId: toUlid(repositoryId) }, { query, skip, take }));
  });

  server.registerTool("terrascale_backup_restore_status", {
    title: "Get backup and restore status", description: "Lists recent backups, restore points, and restore jobs for a database.", inputSchema: { databaseId: ulid, projectId: ulid.optional(), ...page.shape }, annotations: { readOnlyHint: true },
  }, async ({ databaseId, projectId, skip, take }) => {
    const id = toUlid(databaseId); const projectIdValue = projectId === undefined ? undefined : toUlid(projectId);
    const [backups, restorePoints, restores] = await Promise.all([sdk.backups.listBackups(id, { skip, take }, projectIdValue), sdk.backups.listRestorePoints(id, projectIdValue), sdk.backups.listRestores(id, { skip, take }, projectIdValue)]);
    return results({ backups, restorePoints, restores });
  });

  server.registerTool("terrascale_get_usage", {
    title: "Get project usage", description: "Gets current project usage and capacity summary.", inputSchema: { projectId: ulid.optional() }, annotations: { readOnlyHint: true },
  }, async ({ projectId }) => result(sdk.usage.getProjectUsage(projectId === undefined ? undefined : toUlid(projectId))));

  return server;
}

function staticResources(server: McpServer): void {
  resource(server, "api-overview", "terrascale://guides/api-overview", "TerraScale public API overview", "Use the typed public REST SDK only. This MCP server exposes selected read operations for projects, databases, repositories, documents, TQL, schema, backup/restore status, and usage. It has no private, Dashboard, TerraBase, or internal RPC access.");
  resource(server, "safe-mutations", "terrascale://guides/safe-mutations", "Safe mutation practice", "This server intentionally excludes all mutations. Before a change, inspect the target, validate against schema, use a narrowly-scoped typed SDK mutation with an idempotency key where supported, verify the result, and retain an approved rollback plan.");
  resource(server, "tql-guidance", "terrascale://guides/tql-guidance", "TQL guidance", "Inspect repository schema first. Keep TQL read-only and bounded with skip/take. Do not use TQL to infer cross-tenant data or to perform changes; this server refuses common mutation keywords.");
}

function resource(server: McpServer, name: string, uri: string, title: string, text: string): void {
  server.registerResource(name, uri, { title, mimeType: "text/plain" }, async () => ({ contents: [{ uri, text, mimeType: "text/plain" }] }));
}

function prompts(server: McpServer): void {
  prompt(server, "explore-project", "Explore a project safely", "Start by listing projects/databases/repositories. Inspect schema before documents or TQL. Summarize identifiers, status, and gaps; do not mutate.");
  prompt(server, "investigate-data-issue", "Investigate a data issue", "Identify the repository, inspect its schema, retrieve a bounded document page or document, then run a bounded read-only TQL query. State evidence and uncertainty; do not modify data.");
  prompt(server, "plan-schema-change", "Plan a schema change", "Inspect the current schema and representative bounded documents. Propose compatibility, validation, rollout, rollback, and verification steps. Do not apply the change.");
  prompt(server, "backup-and-restore-plan", "Plan backup and restore", "Inspect backup/restore status and restore points. Identify target, recovery point, verification, impact, rollback, and approval steps. Do not trigger backup or restore.");
  prompt(server, "safe-data-change", "Plan a safe data change", "Inspect schema and bounded target data. Propose a typed SDK operation, idempotency, validation, scope, approval, audit, and rollback plan. Do not perform the change.");
}

function prompt(server: McpServer, name: string, title: string, text: string): void {
  server.registerPrompt(name, { title, description: text }, async () => ({ messages: [{ role: "user", content: { type: "text", text } }] }));
}

function isReadOnlyTql(query: string): boolean {
  return !/\b(insert|update|delete|upsert|merge|create|drop|alter|truncate)\b/i.test(query);
}

async function result(value: Promise<TerraScaleResult<unknown>>) { return formatResult(await value); }

function results(values: Readonly<Record<string, TerraScaleResult<unknown>>>) {
  const output: Record<string, unknown> = {};
  const failures: Record<string, JsonObject> = {};
  for (const [name, value] of Object.entries(values)) {
    if (value.ok) output[name] = value.value;
    else failures[name] = safeFailure(value);
  }
  return Object.keys(failures).length > 0
    ? toolError("partial_failure", "One or more status reads failed.", failures)
    : toolSuccess(output);
}

function formatResult(value: TerraScaleResult<unknown>) { return value.ok ? toolSuccess(value.value) : toolError(value.error.code, "TerraScale request failed.", safeFailure(value)); }

function safeFailure(value: Extract<TerraScaleResult<unknown>, { ok: false }>): JsonObject {
  const retryAfter = value.headers?.get("retry-after");
  return { code: value.error.code, kind: value.error.kind, ...(value.status === undefined ? {} : { status: value.status }), ...(value.error.requestId === undefined ? {} : { requestId: value.error.requestId }), ...(retryAfter === null || retryAfter === undefined ? {} : { retryAfter }) };
}

function toolSuccess(value: unknown) { const bounded = bound(value); return { content: [{ type: "text" as const, text: JSON.stringify(bounded) }], structuredContent: { data: bounded } }; }
function toolError(code: string, message: string, metadata?: JsonObject) { return { isError: true, content: [{ type: "text" as const, text: JSON.stringify({ error: { code, message, ...(metadata === undefined ? {} : { metadata }) } }) }], structuredContent: { error: { code, message, ...(metadata === undefined ? {} : { metadata }) } } }; }
function bound(value: unknown): JsonValue { const text = JSON.stringify(value); return text.length <= 200_000 ? JSON.parse(text) as JsonValue : { truncated: true, message: "Result exceeded the MCP response limit; narrow the request." }; }
