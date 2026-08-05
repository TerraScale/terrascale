import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { createTerraScaleClient, toUlid, type TerraScaleResult } from "@terrascale/sdk";
import { AsyncLocalStorage } from "node:async_hooks";
import type { RequestHandlerExtra } from "@modelcontextprotocol/sdk/shared/protocol.js";
import type { ServerNotification, ServerRequest } from "@modelcontextprotocol/sdk/types.js";
import { z } from "zod";
import type { McpRuntimeConfig } from "./config.js";
import { timeoutFetch } from "./config.js";
import { createStructuredLogger, type McpLogger } from "./logger.js";

const ulid = z.string().regex(/^[0-9A-HJKMNP-TV-Z]{26}$/);
const readOnly = { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false } as const;

export const serverInstructions =
  "TerraScale MCP v1 is read-only. Use explicit project identifiers for project data. Results are scoped and authorized by the caller's public TerraScale credential; do not infer or switch tenants. Mutations, credentials, billing, and administration are intentionally unavailable.";

export type McpServerConfig = Omit<McpRuntimeConfig, "accessToken"> & { readonly accessToken?: string };
type RequestExtra = RequestHandlerExtra<ServerRequest, ServerNotification>;
export type McpCredentialProvider = (extra: RequestExtra) => string | undefined;

export function createMcpServer(config: McpServerConfig, logger: McpLogger = createStructuredLogger(), credentialProvider: McpCredentialProvider = () => config.accessToken): McpServer {
  const context = new AsyncLocalStorage<{ readonly signal: AbortSignal; readonly accessToken: string | undefined }>();
  const client = createTerraScaleClient({
    apiBaseUrl: config.apiBaseUrl,
    accessToken: () => context.getStore()?.accessToken,
    fetch: timeoutFetch(config.requestTimeoutMs, () => context.getStore()?.signal),
    requestMetricRecorder: ({ routeName, status, durationMs }) =>
      logger.info("terrascale.public.request", { routeName, status: status ?? 0, durationMs: Math.round(durationMs) }),
  });
  const server = new McpServer({ name: "terrascale-mcp", version: "0.1.0" }, { instructions: serverInstructions });
  const run = <T>(tool: string, extra: RequestExtra, operation: () => Promise<TerraScaleResult<T>>, map: (value: T) => unknown) =>
    context.run({ signal: extra.signal, accessToken: credentialProvider(extra) }, () => invoke(tool, operation, map, config.requestTimeoutMs, logger));

  server.registerResource("read-only-guide", "terrascale://guide", { mimeType: "text/markdown", description: "TerraScale MCP v1 scope." }, async () => ({
    contents: [{ uri: "terrascale://guide", mimeType: "text/markdown", text: `# TerraScale MCP v1\n\n${serverInstructions}` }],
  }));
  server.registerResource("withheld-capabilities", "terrascale://withheld", { mimeType: "text/markdown", description: "Intentionally unavailable capabilities." }, async () => ({
    contents: [{ uri: "terrascale://withheld", mimeType: "text/markdown", text: "Unavailable in v1: all mutations, API-key/M2M credential operations, billing/payment operations, role/access changes, and platform administration." }],
  }));
  server.registerPrompt("read-only-scope", { description: "Explains the safe v1 operating scope." }, async () => ({
    messages: [{ role: "user", content: { type: "text", text: serverInstructions } }],
  }));

  server.registerTool("identity_get_context", { description: "Get the caller's public TerraScale identity and active organization context.", annotations: readOnly }, (extra) =>
    run("identity_get_context", extra, () => client.auth.getSession(), (value) => ({ userId: value.userId, name: value.name, activeOrganizationId: value.currentOrgId ?? value.activeOrgId, roles: value.roles })));
  server.registerTool("organizations_list", { description: "List organizations visible to the caller.", inputSchema: { limit: z.number().int().min(1).max(100).optional() }, annotations: readOnly }, ({ limit }, extra) =>
    run("organizations_list", extra, () => client.organizations.list({ take: limit ?? 50 }), (value) => value.map((organization) => ({ organizationId: organization.organizationId, name: organization.name, status: organization.status, createdAt: organization.createdAt }))));
  server.registerTool("organization_list_projects", { description: "List projects in one explicit organization.", inputSchema: { organizationId: ulid, limit: z.number().int().min(1).max(100).optional() }, annotations: readOnly }, ({ organizationId, limit }, extra) =>
    run("organization_list_projects", extra, () => client.organizations.listProjects(toUlid(organizationId), { take: limit ?? 50 }), (value) => value.map(projectSummary)));
  server.registerTool("project_get", { description: "Get one explicit project.", inputSchema: { projectId: ulid }, annotations: readOnly }, ({ projectId }, extra) =>
    run("project_get", extra, () => client.organizations.getProject(toUlid(projectId)), projectSummary));
  server.registerTool("project_list_region_placements", { description: "List operational region placement status for one explicit project.", inputSchema: { projectId: ulid }, annotations: readOnly }, ({ projectId }, extra) =>
    run("project_list_region_placements", extra, () => client.organizations.listProjectRegionPlacements(toUlid(projectId)), (value) => value.map((placement) => ({ region: placement.region, status: placement.status, availability: placement.availability, isBackupRegion: placement.isBackupRegion, replicationLagMilliseconds: placement.replicationLagMilliseconds ?? undefined, updatedAt: placement.updatedAt }))));
  server.registerTool("project_list_databases", { description: "List databases in one explicit project.", inputSchema: { projectId: ulid, limit: z.number().int().min(1).max(100).optional() }, annotations: readOnly }, ({ projectId, limit }, extra) =>
    run("project_list_databases", extra, () => client.databases.list(toUlid(projectId), { take: limit ?? 50 }), (value) => value.map(databaseSummary)));
  server.registerTool("database_get", { description: "Get one database in one explicit project.", inputSchema: { projectId: ulid, databaseId: ulid }, annotations: readOnly }, ({ projectId, databaseId }, extra) =>
    run("database_get", extra, () => client.databases.get(toUlid(databaseId), toUlid(projectId)), databaseSummary));
  server.registerTool("database_list_backups", { description: "List backup jobs for one database in one explicit project.", inputSchema: { projectId: ulid, databaseId: ulid, limit: z.number().int().min(1).max(100).optional() }, annotations: readOnly }, ({ projectId, databaseId, limit }, extra) =>
    run("database_list_backups", extra, () => client.backups.listBackups(toUlid(databaseId), { take: limit ?? 50 }, toUlid(projectId)), (value) => value.map((backup) => ({ backupId: backup.backupId, databaseId: backup.databaseId, status: backup.status, hasRestorePoint: backup.hasRestorePoint, requestedAt: backup.requestedAt, completedAt: backup.completedAt ?? undefined }))));
  server.registerTool("database_list_restore_points", { description: "List recovery restore points for one database in one explicit project.", inputSchema: { projectId: ulid, databaseId: ulid }, annotations: readOnly }, ({ projectId, databaseId }, extra) =>
    run("database_list_restore_points", extra, () => client.backups.listRestorePoints(toUlid(databaseId), toUlid(projectId)), (value) => value.map((point) => ({ name: point.name, backupId: point.backupId ?? undefined, createdAt: point.createdAt, isSnapshotRestore: point.isSnapshotRestore, isEncrypted: point.isEncrypted, isIncremental: point.isIncremental }))));
  server.registerTool("project_get_usage", { description: "Get concise current usage for one explicit project.", inputSchema: { projectId: ulid }, annotations: readOnly }, ({ projectId }, extra) =>
    run("project_get_usage", extra, () => client.usage.getProjectUsage(toUlid(projectId)), (value) => ({ projectId: value.projectId, periodStart: value.periodStart, periodEnd: value.periodEnd, operationUnits: value.operationUnits, includedOperationUnits: value.includedOperationUnits, overageOperationUnits: value.overageOperationUnits, egressBytes: value.egressBytes, storage: value.storage, provisional: value.provisional, updatedAt: value.updatedAt })));
  server.registerTool("project_get_usage_history", { description: "Get usage history for one explicit project.", inputSchema: { projectId: ulid, range: z.enum(["24h", "7d", "30d", "90d", "365d"]) }, annotations: readOnly }, ({ projectId, range }, extra) =>
    run("project_get_usage_history", extra, () => client.usage.getProjectUsageHistory(range, toUlid(projectId)), (value) => ({ projectId: value.projectId, range: value.range, limitedByRetention: value.limitedByRetention, hasData: value.hasData, points: value.points })));
  server.registerTool("project_get_latency", { description: "Get operational latency estimates for one explicit project.", inputSchema: { projectId: ulid }, annotations: readOnly }, ({ projectId }, extra) =>
    run("project_get_latency", extra, () => client.usage.getProjectLatency(toUlid(projectId)), (value) => ({ projectId: value.projectId, capturedAt: value.capturedAt, source: value.source, interRegion: value.interRegion, nearestRegion: value.nearestRegion ?? undefined })));

  return server;
}

function projectSummary(project: { readonly projectId: string; readonly organizationId: string; readonly name: string; readonly regions: readonly string[]; readonly backupRegion: string; readonly placementStatus: string; readonly createdAt: string }): object {
  return { projectId: project.projectId, organizationId: project.organizationId, name: project.name, regions: project.regions, backupRegion: project.backupRegion, placementStatus: project.placementStatus, createdAt: project.createdAt };
}

function databaseSummary(database: { readonly databaseId: string; readonly projectId: string; readonly name: string; readonly regions: readonly string[]; readonly backupRegion: string; readonly createdAt: string; readonly version: number }): object {
  return { databaseId: database.databaseId, projectId: database.projectId, name: database.name, regions: database.regions, backupRegion: database.backupRegion, createdAt: database.createdAt, version: database.version };
}

async function invoke<T>(tool: string, operation: () => Promise<TerraScaleResult<T>>, map: (value: T) => unknown, timeoutMs: number, logger: McpLogger) {
  const startedAt = performance.now();
  try {
    const result = await within(operation(), timeoutMs);
    logger.info("mcp.tool.completed", { tool, durationMs: Math.round(performance.now() - startedAt), ok: result.ok });
    if (!result.ok) {
      return errorResult("public_api_error", result.error.status);
    }
    return { content: [{ type: "text" as const, text: JSON.stringify(map(result.value)) }] };
  } catch {
    logger.warn("mcp.tool.failed", { tool, durationMs: Math.round(performance.now() - startedAt) });
    return errorResult("request_failed");
  }
}

function within<T>(promise: Promise<T>, timeoutMs: number): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("timeout")), timeoutMs);
    promise.then(
      (value) => { clearTimeout(timer); resolve(value); },
      (error: unknown) => { clearTimeout(timer); reject(error); },
    );
  });
}

function errorResult(code: "public_api_error" | "request_failed", status?: number) {
  return { isError: true, content: [{ type: "text" as const, text: JSON.stringify({ error: { code, status } }) }] };
}
