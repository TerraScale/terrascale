export {
  terraScaleReleaseBlockers,
  TerraScaleCapabilityUnavailableError,
} from "./management-contracts.js";
import { createTerraBaseV1Client } from "./v1/terrabase-v1.js";
export { createTerraScaleIdentityManagementClient, createTerraScaleAccountDeletionClient, createTerraScaleProjectIdentityClient } from "./platform-management.js";
/** @typedef {import("./platform-management.js").TerraScaleIdentityManagementClientConfig} TerraScaleIdentityManagementClientConfig */
/** @typedef {import("./platform-management.js").TerraScaleIdentityManagementClient} TerraScaleIdentityManagementClient */
/** @typedef {import("./platform-management.js").TerraScaleAccountDeletionClientConfig} TerraScaleAccountDeletionClientConfig */
/** @typedef {import("./platform-management.js").TerraScaleAccountDeletionClient} TerraScaleAccountDeletionClient */
/** @typedef {import("./platform-management.js").TerraScaleProjectIdentityClientConfig} TerraScaleProjectIdentityClientConfig */
/** @typedef {import("./platform-management.js").TerraScaleProjectIdentityClient} TerraScaleProjectIdentityClient */

/** @typedef {import("./v1/transport.js").TerraBaseV1ClientConfig} TerraScaleManagementClientConfig */
/** @typedef {ReturnType<typeof createTerraScaleManagementClient>} TerraScaleManagementClient */

/**
 * Creates the published project-scoped management operations using the root SDK.
 * Database handles expose backups, imports, indexes, schema, usage and exports.
 * The producer enforces project/database scope and permissions on every request.
 * Credentials stay private; cancellation, pagination and errors retain root semantics.
 * Mutations are never automatically retried: these routes have no idempotency contract.
 * @param {TerraScaleManagementClientConfig} config
 */
export function createTerraScaleManagementClient(config) {
  const client = createTerraBaseV1Client(config);
  return Object.freeze({
    databases: client.databases,
    /** @param {string} name */
    database: (name) => client.database(name),
    metrics: client.metrics,
    backupDestination: client.backupDestination,
    savedQueries: client.savedQueries,
  });
}

/**
 * @deprecated Use createTerraScaleManagementClient with explicit project credentials.
 * @param {TerraScaleManagementClientConfig} config
 */
export function requireTerraScaleManagement(config) {
  return createTerraScaleManagementClient(config);
}
