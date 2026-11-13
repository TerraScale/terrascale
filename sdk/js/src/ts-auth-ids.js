import { requireId } from "./ids.js";

const requiredRootReferences = new Map([
  ["tsauth.oidc.userinfo", "sub"],
]);

/**
 * Validate identity fields in the hosted-auth DTOs without inspecting customer metadata.
 * @param {unknown} value
 * @param {string} route
 * @param {boolean} [entity]
 * @returns {void}
 */
export function validateTsAuthIds(value, route, entity = false) {
  const requiredReference = requiredRootReferences.get(route);
  if ((entity || requiredReference !== undefined) && (typeof value !== "object" || value === null || Array.isArray(value)))
    throw new TypeError("Hosted-auth response must be an object with its required ULID.");
  if (Array.isArray(value)) {
    for (const entry of value) validateTsAuthIds(entry, route, true);
    return;
  }
  if (typeof value !== "object" || value === null) return;
  if (route === "tsauth.projects.verify_user_api_key") {
    const valid = Reflect.get(value, "valid");
    if (typeof valid !== "boolean") throw new TypeError("User key verification requires valid.");
    if (!valid) {
      if (["user_id", "key_id", "scopes", "expires_at"].some(field => Reflect.get(value, field) !== null))
        throw new TypeError("An invalid user key must have null identity fields.");
      return;
    }
    requireId(Reflect.get(value, "key_id"), "key_id");
  }
  if (requiredReference !== undefined) requireId(Reflect.get(value, requiredReference), requiredReference);
  if (entity) {
    requireId(Reflect.get(value, "id"), "entity id");
  }
  const fields = ["id", "user_id", "organization_id", "org_id", "endpoint_id", "event_id", "instance_id", "actor_id"];
  if (route.startsWith("tsauth.oidc."))
    fields.push("client_id", "sub", "sid", "jti");
  for (const field of fields) {
    if (Object.hasOwn(value, field)) {
      const id = Reflect.get(value, field);
      if (id !== null || field !== "sid") requireId(id, field);
    }
  }
  // `data` objects in audit events are customer payloads; only page arrays contain DTOs.
  if (Object.hasOwn(value, "data") && Array.isArray(Reflect.get(value, "data")))
    validateTsAuthIds(Reflect.get(value, "data"), route);
  for (const field of ["organization", "user"]) {
    if (Object.hasOwn(value, field) && Reflect.get(value, field) !== null)
      validateTsAuthIds(Reflect.get(value, field), field, true);
  }
}
