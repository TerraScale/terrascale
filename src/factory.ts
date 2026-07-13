import { TerraScaleClient as InternalTerraScaleClient } from "./client.js";
import type { TerraScaleRuntimeConfig } from "./config.js";

export type TerraScaleClient = Omit<InternalTerraScaleClient, "admin">;

export function createTerraScaleClient(config: TerraScaleRuntimeConfig): TerraScaleClient {
  const internal = new InternalTerraScaleClient(config);

  return {
    auth: internal.auth,
    organizations: internal.organizations,
    regions: internal.regions,
    queues: internal.queues,
    databases: internal.databases,
    repositories: internal.repositories,
    backups: internal.backups,
    documents: internal.documents,
    query: internal.query,
    schema: internal.schema,
    apiKeys: internal.apiKeys,
    m2m: internal.m2m,
    access: internal.access,
    usage: internal.usage,
    billing: internal.billing,
    pricing: internal.pricing,
    audit: internal.audit,
    notifications: internal.notifications,
    security: internal.security,
    get config() {
      return internal.config;
    },
    request: internal.request.bind(internal) as InternalTerraScaleClient["request"],
    dashboardDatabaseId: internal.dashboardDatabaseId.bind(
      internal,
    ) as InternalTerraScaleClient["dashboardDatabaseId"],
  };
}
