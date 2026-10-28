import { requireId } from "./ids.js";
import { flag, list, nullable, record, text } from "./v1/management-decode.js";
/** @import { JsonValue } from "./v1/json.js" */
/** @import { Decoder } from "./v1/management-decode.js" */
/** @template {string} T @param {readonly T[]} values @returns {Decoder<T>} */
function oneOf(values) {
  return value => {
    const found = values.find(item => item === value);
    if (found === undefined) throw new TypeError("Invalid platform enum value.");
    return found;
  };
}
export const organizationRole = oneOf(/** @type {const} */ (["viewer", "member", "admin", "owner"]));
export const projectRole = oneOf(/** @type {const} */ (["viewer", "member", "admin"]));
export const scope = oneOf(/** @type {const} */ (["documents:read", "documents:write", "databases:read", "databases:write"]));
/** @param {JsonValue | undefined} value */
export function instant(value) {
  const result = text(value);
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/u.test(result) || !Number.isFinite(Date.parse(result)))
    throw new TypeError("Expected an RFC 3339 instant.");
  return result;
}
export const organization = record({ id: requireId, name: text, created_by: requireId, created_at: instant,
  role: nullable(organizationRole), require_mfa: flag });
export const project = record({ id: requireId, organization_id: requireId, name: text, issuer: nullable(text),
  created_by: requireId, created_at: instant, role: projectRole, allowed_cidrs: list(text) });
export const member = record({ user_id: requireId, email: text, role: organizationRole, joined_at: instant });
export const projectMember = record({ user_id: requireId, email: text, role: projectRole, granted_at: instant });
export const invitation = record({ id: requireId, email: text, role: organizationRole, project_id: nullable(requireId),
  invited_by: requireId, created_at: instant, expires_at: instant,
  status: oneOf(/** @type {const} */ (["pending", "accepted", "declined", "revoked", "expired"])) });
export const invitationCreated = record({ invitation, token: text });
const namedResource = record({ id: requireId, name: text });
export const invitationLookup = record({ organization: namedResource, project: nullable(namedResource), email: text,
  role: organizationRole, expires_at: instant });
export const acceptedInvitation = record({ organization_id: requireId, project_id: nullable(requireId), role: organizationRole });
export const ownershipTransfer = record({ from_user_id: requireId, to_user_id: requireId, created_at: instant, expires_at: instant });
export const auditEvent = record({ id: requireId, actor: text, action: text, target: nullable(text), detail: nullable(text), occurred_at: instant });
export const ssoConnection = record({ organization_id: requireId, domain: text, domain_verified: flag, verified_at: nullable(instant),
  verification: record({ type: oneOf(/** @type {const} */ (["TXT"])), name: text, value: text }),
  protocol: oneOf(/** @type {const} */ (["oidc"])), default_role: projectRole,
  oidc: nullable(record({ issuer: nullable(text), client_id: nullable(text), client_secret_set: flag, redirect_uri: text })),
  created_at: instant, updated_at: instant });
/** @param {JsonValue | undefined} value @param {number} maximum */
function nonnegativeInteger(value, maximum) {
  const result = typeof value === "string" && /^(?:0|[1-9]\d*)$/u.test(value) ? Number(value) : value;
  if (typeof result !== "number" || !Number.isSafeInteger(result) || result < 0 || result > maximum)
    throw new TypeError("Expected a bounded nonnegative integer.");
  return result;
}
export const accountMfa = record({ enabled: flag, recovery_codes_remaining: value => nonnegativeInteger(value, 2147483647), manage_url: text });
export const linkedProviders = record({ providers: list(text) });
export const deletionHistory = record({ erasureId: text, requestedAt: instant, authLogicalVerifiedAt: nullable(instant),
  historyStatus: nullable(text), historyCompletedAt: nullable(instant) });
const tokenBody = record({ access_token: text, token_type: oneOf(/** @type {const} */ (["Bearer"])),
  expires_in: value => nonnegativeInteger(value, 900) });
/** @param {JsonValue | undefined} value */
export function endUserToken(value) {
  const result = tokenBody(value);
  if (result.expires_in === 0 || result.access_token.length > 8192 ||
      !/^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/u.test(result.access_token))
    throw new TypeError("Expected a bounded short-lived end-user JWT.");
  return result;
}
export const apiKey = record({ id: requireId, name: text, organization_id: requireId, project_id: nullable(requireId),
  database: nullable(requireId), scopes: list(scope), created_by: requireId, created_at: instant,
  expires_at: nullable(instant), previous_secret_expires_at: nullable(instant), last_used_at: nullable(instant), allowed_cidrs: list(text) });
const secretBody = record({ api_key: apiKey, key: text });
/** @param {JsonValue | undefined} value */
export function keySecret(value) {
  const result = secretBody(value);
  // ApiKeys.Generate: canonical ULID, separator, 52 random lowercase symbols.
  if (result.key.length !== 79 || result.key.slice(0, 26) !== result.api_key.id ||
      !/^_[0123456789abcdefghjkmnpqrstvwxyz]{52}$/u.test(result.key.slice(26)))
    throw new TypeError("Malformed platform key secret.");
  return result;
}
/** @template T @param {Decoder<T>} item */
export const page = item => record({ items: list(item), next: nullable(text) });
/** @param {JsonValue | undefined} value */
export function empty(value) {
  if (value !== undefined) throw new TypeError("Expected an empty platform response.");
}
