export { TerraScaleCapabilityUnavailableError } from "./management-contracts.js";
import { createTerraBaseSystemClient } from "./v1/system.js";

/** @typedef {import("./v1/system.js").TerraBaseSystemClientConfig & {readonly token: string}} TerraScaleAdministrationClientConfig */
/** @typedef {import("./v1/system.js").V1Platform} TerraScaleAdministrationClient */

/**
 * Creates the public read-only platform administration operations.
 * Every request requires a user JWT carrying platform_role=admin, enforced by
 * the producer. Constructing a client does not grant or assert that role.
 * Credentials, cancellation, errors and pagination use the root SDK semantics.
 * @param {TerraScaleAdministrationClientConfig} config
 * @returns {TerraScaleAdministrationClient}
 */
export function createTerraScaleAdministrationClient(config) {
  if (typeof config?.token !== "string") throw new TypeError("Administration requires an explicit access JWT.");
  return createTerraBaseSystemClient(config).platform;
}

/**
 * @deprecated Use createTerraScaleAdministrationClient with explicit administrator credentials.
 * @param {TerraScaleAdministrationClientConfig} config
 */
export function requireTerraScaleAdministration(config) {
  return createTerraScaleAdministrationClient(config);
}
