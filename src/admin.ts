import { AdminClient } from "./client.js";
import type { TerraScaleRuntimeConfig } from "./config.js";
import { TerraScaleHttp } from "./http.js";

/**
 * Creates the role-protected platform administration client. Calls still require
 * a TerraScale user token carrying the reserved `terrascale_admin` global role.
 */
export function createTerraScaleAdminClient(config: TerraScaleRuntimeConfig): AdminClient {
  return new AdminClient(new TerraScaleHttp(config));
}

export { AdminClient } from "./client.js";
export type { TerraScaleRuntimeConfig } from "./config.js";
export type * from "./models.js";
