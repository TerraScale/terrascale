import { TerraScaleClient as InternalTerraScaleClient } from "./client.js";
import type { TerraScaleRuntimeConfig } from "./config.js";

export type TerraScaleClient = InternalTerraScaleClient;

export function createTerraScaleClient(config: TerraScaleRuntimeConfig): TerraScaleClient {
  return new InternalTerraScaleClient(config);
}
