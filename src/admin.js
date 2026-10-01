export { TerraScaleCapabilityUnavailableError } from "./management-contracts.js";
import { requireTerraScaleCapability } from "./management-contracts.js";
/**
 * Administrative operations require the admitted regional management contract.
 * @returns {never}
 */
export function requireTerraScaleAdministration() {
  return requireTerraScaleCapability("management");
}
