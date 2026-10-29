/** @import { V1DatabaseListRequest } from "./catalog.js" */
/** @param {unknown} request @param {readonly string[]} allowed */
export function members(request, allowed) {
  if (request === null || typeof request !== "object" || Array.isArray(request) ||
      ![Object.prototype, null].includes(Object.getPrototypeOf(request))) throw new TypeError("Expected a plain request object.");
  for (const key of Object.keys(request)) if (!allowed.includes(key)) throw new TypeError(`Unknown request member ${key}.`);
}
/** @param {object} options */
export function mutationOptions(options) {
  if ("idempotencyKey" in options) throw new TypeError("The producer does not support Idempotency-Key; mutations are never replayed.");
}
/** @param {V1DatabaseListRequest} request */
export function pageQuery(request) {
  members(request, ["after", "limit"]);
  const query = new URLSearchParams();
  if (request.after !== undefined) {
    if (typeof request.after !== "string") throw new TypeError("after must be an opaque string.");
    query.set("after", request.after);
  }
  if (request.limit !== undefined) {
    if (!Number.isInteger(request.limit) || request.limit < 1 || request.limit > 100) throw new TypeError("limit must be 1-100.");
    query.set("limit", String(request.limit));
  }
  return query;
}
