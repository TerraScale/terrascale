export {
  terraScaleReleaseBlockers,
  TerraScaleCapabilityUnavailableError,
} from "./management-contracts.js";
import { requireTerraScaleCapability } from "./management-contracts.js";
/**
 * Better Auth 1.7.2 persistence awaits public native auth primitives and durable
 * mutation receipts. Local conditional writes and convergent uniqueness do not
 * satisfy its consume/increment and callback transaction semantics.
 * @returns {never}
 */
export function requireTerraScaleBetterAuth() {
  return requireTerraScaleCapability("betterAuth");
}
