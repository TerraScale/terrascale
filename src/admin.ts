import { AdminClient, TerraScaleClient } from "./client.js";
import type { TerraScaleRuntimeConfig } from "./config.js";

/**
 * Creates the role-protected platform administration client. Calls still require
 * a TerraScale user token carrying the reserved `terrascale_admin` global role.
 */
export function createTerraScaleAdminClient(config: TerraScaleRuntimeConfig): AdminClient {
  return new TerraScaleClient(config).admin;
}

export { AdminClient } from "./client.js";
export type { TerraScaleRuntimeConfig } from "./config.js";
export type * from "./models.js";
