import { AdminClient } from "./client.js";
import type { TerraScaleRuntimeConfig } from "./config.js";
import { TerraScaleHttp } from "./http.js";

export function createTerraScaleAdminClient(config: TerraScaleRuntimeConfig): AdminClient {
  return new AdminClient(new TerraScaleHttp(config));
}

export { AdminClient } from "./client.js";
export type { TerraScaleRuntimeConfig } from "./config.js";
export type * from "./models.js";
