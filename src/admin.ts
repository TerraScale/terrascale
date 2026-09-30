export { TerraScaleCapabilityUnavailableError } from "./management-contracts.js";
import { requireTerraScaleCapability } from "./management-contracts.js";
/** Administrative operations require the admitted regional management contract. */
export function requireTerraScaleAdministration(): never {
  return requireTerraScaleCapability("management");
}
