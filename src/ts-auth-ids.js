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

const requiredRootReferences = new Map([
  ["tsauth.oidc.userinfo", "sub"],
  ["tsauth.organizations.enrollDomain", "org_id"],
]);

/** @type {ReadonlyMap<string, readonly string[]>} */
const entityReferenceFields = new Map([
  ["tsauth.organizations.createMembership", ["organization_id", "user_id"]],
  ["tsauth.organizations.updateMembership", ["organization_id", "user_id"]],
  ["tsauth.organizations.listMemberships", ["organization_id", "user_id"]],
  ["tsauth.organizations.createInvitation", ["organization_id"]],
  ["tsauth.organizations.revokeInvitation", ["organization_id"]],
  ["tsauth.organizations.listInvitations", ["organization_id"]],
  ["tsauth.organizations.createDomain", ["organization_id"]],
  ["tsauth.organizations.listDomains", ["organization_id"]],
  ["tsauth.auditEvents.list", ["instance_id"]],
  ["tsauth.webhooks.deadLetter", ["endpoint_id", "event_id"]],
  ["tsauth.oauthClients.create", ["client_id"]],
  ["tsauth.oauthClients.update", ["client_id"]],
  ["tsauth.oauthClients.list", ["client_id"]],
  ["tsauth.oauthResources.create", ["introspection_client_id"]],
  ["tsauth.oauthResources.update", ["introspection_client_id"]],
  ["tsauth.oauthResources.list", ["introspection_client_id"]],
  ["tsauth.connections.create", ["org_id"]],
  ["tsauth.connections.update", ["org_id"]],
  ["tsauth.connections.list", ["org_id"]],
  ["tsauth.directories.create", ["org_id"]],
  ["tsauth.directories.rotate", ["org_id"]],
  ["tsauth.directories.list", ["org_id"]],
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
  const requiredReference = requiredRootReferences.get(route);
  if ((entity || requiredReference !== undefined) && (typeof value !== "object" || value === null || Array.isArray(value)))
    throw new TypeError("Hosted-auth response must be an object with its required ULID.");
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
  if (requiredReference !== undefined) requireId(Reflect.get(value, requiredReference), requiredReference);
  const deadLetter = route === "tsauth.webhooks.deadLetter";
  if (entity) {
    if (deadLetter) requireDeadLetterId(Reflect.get(value, "id"));
    else requireId(Reflect.get(value, "id"), "entity id");
  }
  const fields = ["id", "user_id", "organization_id", "org_id", "endpoint_id", "event_id", "instance_id", "actor_id"];
  const nullableOrgId = route.startsWith("tsauth.connections.") || route.startsWith("tsauth.directories.");
  if (/^tsauth\.(oauthClients|oauthResources|oidc)\./u.test(route))
    fields.push("client_id", "introspection_client_id", "sub", "sid", "jti");
  const requiredFields = entityReferenceFields.get(route) ?? [];
  const validateReferences = entity || Object.hasOwn(value, "id");
  for (const field of fields) {
    if (Object.hasOwn(value, field) || (validateReferences && requiredFields.includes(field))) {
      const id = Reflect.get(value, field);
      if (field === "id" && deadLetter) requireDeadLetterId(id);
      else if (id !== null || !(field === "sid" || (field === "org_id" && nullableOrgId))) requireId(id, field);
    }
  }
  // `data` objects in audit events are customer payloads; only page arrays contain DTOs.
  if (Object.hasOwn(value, "data") && Array.isArray(Reflect.get(value, "data")))
    validateTsAuthIds(Reflect.get(value, "data"), route);
  for (const field of ["passkeys", "clients", "resources"])
    if (Object.hasOwn(value, field)) validateTsAuthIds(Reflect.get(value, field), route);
  for (const field of ["organization", "user"]) {
    if (Object.hasOwn(value, field) && Reflect.get(value, field) !== null) {
      const relatedRoute = field === "organization" ? "tsauth.organizations.get" : "tsauth.users.get";
      validateTsAuthIds(Reflect.get(value, field), relatedRoute, true);
    }
  }
  let oauthEntity;
  if (route.startsWith("tsauth.oauthClients.")) oauthEntity = "client";
  else if (route.startsWith("tsauth.oauthResources.")) oauthEntity = "resource";
  if (oauthEntity !== undefined && Object.hasOwn(value, oauthEntity))
    validateTsAuthIds(Reflect.get(value, oauthEntity), route, true);
  if (Object.hasOwn(value, "allowed_client_ids")) {
    const ids = Reflect.get(value, "allowed_client_ids");
    if (!Array.isArray(ids)) throw new TypeError("allowed_client_ids must be an array of ULIDs.");
    for (const id of ids) requireId(id, "allowed_client_ids");
  }
}
