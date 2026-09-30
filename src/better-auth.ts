export {
  terraScaleReleaseBlockers,
  TerraScaleCapabilityUnavailableError,
} from "./management-contracts.js";
import { requireTerraScaleCapability } from "./management-contracts.js";
/**
 * Production Better Auth persistence is required but blocked by security authority
 * and catalog/credential admission. Local development uses the explicit ./local
 * SQLite factory pinned to Better Auth 1.7.2.
 */
export function requireTerraScaleBetterAuth(): never {
  return requireTerraScaleCapability("betterAuth");
}
