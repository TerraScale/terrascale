/** Required capabilities whose producer/runtime admission is still open. */
export const terraScaleReleaseBlockers = Object.freeze({
  management: Object.freeze({
    code: "management_contract_unavailable",
    owner: "C06/C07 regional management service",
    required: Object.freeze([
      "resource provisioning",
      "schema/catalog plan/apply/status",
      "credential lifecycle",
      "backups/restore",
      "imports/exports",
      "usage",
      "administration",
    ]),
  }),
  betterAuth: Object.freeze({
    code: "security_authority_unavailable",
    owner: "native single-writer security admission and C06/C07",
    required: Object.freeze([
      "unique insert",
      "compare-and-swap",
      "one-time consume",
      "bounded transactions",
      "pagination/joins",
      "indexes/migrations",
      "revocation/denial",
      "region concurrency",
    ]),
  }),
  qualification: Object.freeze({
    code: "runtime_qualification_unavailable",
    owner: "real producer allocation and deployed customer application",
    required: Object.freeze([
      "installed package against real producer",
      "deployed authenticated account/browser journey",
    ]),
  }),
});
/** @typedef {keyof typeof terraScaleReleaseBlockers} TerraScaleBlockedCapability */
export class TerraScaleCapabilityUnavailableError extends Error {
  /**
   * @readonly
   * @type {string}
   */
  code;
  /**
   * @readonly
   * @type {TerraScaleBlockedCapability}
   */
  capability;
  /**
   * @readonly
   * @type {readonly string[]}
   */
  required;
  /** @param {TerraScaleBlockedCapability} capability */
  constructor(capability) {
    const status = terraScaleReleaseBlockers[capability];
    super(`${capability} requires admitted ${status.owner}.`);
    this.name = "TerraScaleCapabilityUnavailableError";
    this.code = status.code;
    this.capability = capability;
    this.required = status.required;
  }
}
/**
 * @param {TerraScaleBlockedCapability} capability
 * @returns {never}
 */
export function requireTerraScaleCapability(capability) {
  throw new TerraScaleCapabilityUnavailableError(capability);
}
