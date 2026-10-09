import { requireId } from "./ids.js";

const singleEntityRoutes = new Set([
  "tsauth.users.get", "tsauth.users.create", "tsauth.users.update",
  "tsauth.apiKeys.create",
  "tsauth.organizations.get", "tsauth.organizations.create", "tsauth.organizations.update",
  "tsauth.organizations.createMembership", "tsauth.organizations.updateMembership",
  "tsauth.organizations.createInvitation", "tsauth.organizations.revokeInvitation", "tsauth.organizations.createDomain",
  "tsauth.connections.create", "tsauth.connections.update",
  "tsauth.directories.create", "tsauth.directories.rotate",
  "tsauth.jwtTemplates.create", "tsauth.jwtTemplates.update",
  "tsauth.webhooks.create",
  "tsauth.instance.get", "tsauth.instance.update",
]);

/**
 * Validate the documented endpointId:eventId delivery selector.
 * @param {unknown} value
 * @returns {string}
 */
export function requireDeadLetterId(value) {
  if (typeof value !== "string" || value.split(":").length !== 2)
    throw new TypeError("Dead-letter selector must contain endpoint and event ULIDs separated by a colon.");
  const [endpoint, event] = value.split(":");
  requireId(endpoint, "endpoint id");
  requireId(event, "event id");
  return value;
}

/**
 * Validate identity fields in the hosted-auth DTOs without inspecting customer metadata.
 * @param {unknown} value
 * @param {string} route
 * @param {boolean} [entity]
 * @returns {void}
 */
export function validateTsAuthIds(value, route, entity = singleEntityRoutes.has(route)) {
  if (entity && (typeof value !== "object" || value === null || Array.isArray(value)))
    throw new TypeError("Hosted-auth entity must be an object with a ULID.");
  if (Array.isArray(value)) {
    if (route === "tsauth.users.regenerateRecoveryCodes") {
      if (!value.every(code => typeof code === "string"))
        throw new TypeError("Recovery codes must be strings.");
      return;
    }
    for (const entry of value) validateTsAuthIds(entry, route, true);
    return;
  }
  if (typeof value !== "object" || value === null) return;
  const deadLetter = route === "tsauth.webhooks.deadLetter";
  if (entity) {
    if (deadLetter) requireDeadLetterId(Reflect.get(value, "id"));
    else requireId(Reflect.get(value, "id"), "entity id");
  }
  const fields = ["id", "user_id", "organization_id", "org_id", "endpoint_id", "event_id", "instance_id", "actor_id"];
  if (/^tsauth\.(oauthClients|oauthResources|oidc)\./u.test(route))
    fields.push("client_id", "introspection_client_id", "sub", "sid", "jti");
  for (const field of fields) {
    if (Object.hasOwn(value, field)) {
      const id = Reflect.get(value, field);
      if (field === "id" && deadLetter) requireDeadLetterId(id);
      else if (id !== null || !["org_id", "sid"].includes(field)) requireId(id, field);
    }
  }
  // `data` objects in audit events are customer payloads; only page arrays contain DTOs.
  if (Object.hasOwn(value, "data") && Array.isArray(Reflect.get(value, "data")))
    validateTsAuthIds(Reflect.get(value, "data"), route);
  for (const field of ["passkeys", "clients", "resources"])
    if (Object.hasOwn(value, field)) validateTsAuthIds(Reflect.get(value, field), route);
  for (const field of ["organization", "user"]) {
    if (Object.hasOwn(value, field) && Reflect.get(value, field) !== null)
      validateTsAuthIds(Reflect.get(value, field), route, true);
  }
  const oauthEntity = route.startsWith("tsauth.oauthClients.") ? "client"
    : route.startsWith("tsauth.oauthResources.") ? "resource" : undefined;
  if (oauthEntity !== undefined && Object.hasOwn(value, oauthEntity))
    validateTsAuthIds(Reflect.get(value, oauthEntity), route, true);
  if (Object.hasOwn(value, "allowed_client_ids")) {
    const ids = Reflect.get(value, "allowed_client_ids");
    if (!Array.isArray(ids)) throw new TypeError("allowed_client_ids must be an array of ULIDs.");
    for (const id of ids) requireId(id, "allowed_client_ids");
  }
  if (route === "tsauth.oidc.userinfo") requireId(Reflect.get(value, "sub"), "subject id");
}
