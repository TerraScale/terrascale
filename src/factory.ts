import { TerraScaleClient as InternalTerraScaleClient } from "./client.js";
import type { TerraScaleRuntimeConfig } from "./config.js";

export type TerraScaleClient = Omit<InternalTerraScaleClient, "admin" | "dashboard">;

export function createTerraScaleClient(config: TerraScaleRuntimeConfig): TerraScaleClient {
  return new InternalTerraScaleClient(config);
}
