import { TerraScaleClient } from "./client.js";
import type { TerraScaleRuntimeConfig } from "./config.js";

export function createTerraScaleClient(config: TerraScaleRuntimeConfig): TerraScaleClient {
  return new TerraScaleClient(config);
}
