export {
  terraScaleReleaseBlockers,
  TerraScaleCapabilityUnavailableError,
} from "./management-contracts.js";
import { requireTerraScaleCapability } from "./management-contracts.js";
/** Required public management support remains closed until C06/C07 route admission. */
export function requireTerraScaleManagement(): never {
  return requireTerraScaleCapability("management");
}
