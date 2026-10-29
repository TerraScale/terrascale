/** Remaining contract/qualification gaps; existing named management operations are usable. */
export const terraScaleReleaseBlockers = Object.freeze({
  management: Object.freeze({
    code: "management_contract_unavailable",
    owner: "owning public Database schema and Dashboard platform services",
    required: Object.freeze([
      "durable schema plan/apply/status",
      "public platform billing REST",
      "platform administration mutations",
    ]),
  }),
  betterAuth: Object.freeze({
    code: "security_authority_unavailable",
    owner: "public Database auth primitives and durable mutation receipts",
    required: Object.freeze([
      "unique insert",
      "compare-and-swap",
      "one-time consume",
      "bounded transactions",
      "native consumeOne/incrementOne",
      "callback read-your-writes and rollback",
      "durable mutation identities",
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
