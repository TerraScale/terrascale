import { TerraScaleClient as InternalTerraScaleClient } from "./client.js";
import type { TerraScaleRuntimeConfig } from "./config.js";

export type TerraScaleClient = InternalTerraScaleClient;

export function createTerraScaleClient(config: TerraScaleRuntimeConfig): TerraScaleClient {
  const client = new InternalTerraScaleClient(config);
  client.request = client.request.bind(client) as InternalTerraScaleClient["request"];
  return client;
}
