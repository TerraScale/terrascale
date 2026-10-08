import { requireId } from "./ids.js";

/**
 * Validate identity fields in the hosted-auth DTOs without inspecting customer metadata.
 * @param {unknown} value
 * @param {string} route
 * @param {boolean} [entity]
 * @returns {void}
 */
export function validateTsAuthIds(value, route, entity = false) {
  if (Array.isArray(value)) {
    for (const entry of value) validateTsAuthIds(entry, route, true);
    return;
  }
  if (typeof value !== "object" || value === null) return;
  if (entity) requireId(Reflect.get(value, "id"), "entity id");
  const fields = ["id", "user_id", "organization_id", "org_id", "endpoint_id", "event_id", "instance_id", "actor_id"];
  if (/^tsauth\.(oauthClients|oauthResources|oidc)\./u.test(route))
    fields.push("client_id", "introspection_client_id", "sub", "sid", "jti");
  for (const field of fields) {
    if (Object.hasOwn(value, field)) {
      const id = Reflect.get(value, field);
      if (id !== null || !["org_id", "sid"].includes(field)) requireId(id, field);
    }
  }
  // `data` objects in audit events are customer payloads; only page arrays contain DTOs.
  if (Object.hasOwn(value, "data") && Array.isArray(Reflect.get(value, "data")))
    validateTsAuthIds(Reflect.get(value, "data"), route);
  for (const field of ["passkeys", "clients", "resources", "organization", "user"])
    if (Object.hasOwn(value, field)) validateTsAuthIds(Reflect.get(value, field), route, true);
  if (Object.hasOwn(value, "allowed_client_ids")) {
    const ids = Reflect.get(value, "allowed_client_ids");
    if (!Array.isArray(ids)) throw new TypeError("allowed_client_ids must be an array of ULIDs.");
    for (const id of ids) requireId(id, "allowed_client_ids");
  }
  if (/^tsauth\.users\.(get|create|update)$/u.test(route)) requireId(Reflect.get(value, "id"), "user id");
  if (route === "tsauth.oidc.userinfo") requireId(Reflect.get(value, "sub"), "subject id");
}
