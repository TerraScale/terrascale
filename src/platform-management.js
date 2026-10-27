import { TsAuthHttp } from "./http.js";
import { err } from "./result.js";
import { requireId } from "./ids.js";
import { requireName } from "./v1/ids.js";
import { requireServiceUrl } from "./v1/transport.js";
import { organizationRole, projectRole, scope, instant, organization, project, member, projectMember, apiKey, keySecret,
  invitation, invitationCreated, invitationLookup, acceptedInvitation, ownershipTransfer, ssoConnection, auditEvent,
  accountMfa, linkedProviders, deletionHistory, endUserToken, page, empty } from "./platform-management-decode.js";
import { members } from "./v1/management-validation.js";
import { platformIdentityRoutes } from "./platform-management-routes.js";

/** @import { JsonValue } from "./v1/json.js" */
/** @import { Decoder } from "./v1/management-decode.js" */
/** @import { TerraScaleResult } from "./result.js" */
/**
 * The caller's platform sign-in token, distinct from project access tokens and
 * tenant management keys. The producer checks the live user, session and role.
 * @typedef {{readonly apiBaseUrl: string; readonly signInToken: string | (() => string | Promise<string>); readonly fetch?: import("./config.js").FetchLike}} TerraScaleIdentityManagementClientConfig
 */
/** @typedef {{readonly signal?: AbortSignal}} IdentityRequestOptions */
/** @typedef {{readonly apiBaseUrl: string; readonly fetch?: import("./config.js").FetchLike}} IdentityTransportConfig */
/** @typedef {IdentityTransportConfig} TerraScaleAccountDeletionClientConfig */
/** @typedef {IdentityTransportConfig & {readonly project: string; readonly projectApiKey: string | (() => string | Promise<string>)}} TerraScaleProjectIdentityClientConfig */
/** @typedef {{readonly receipt: string; readonly organization?: string}} IdentityDeletionStatusRequest */
/** @typedef {{readonly token: string}} IdentityInvitationTokenRequest */
/** @typedef {{readonly user_id: string; readonly claims?: {readonly roles?: readonly string[] | null} | null}} IdentityEndUserTokenRequest */
/** @typedef {{readonly limit?: number; readonly after?: string}} IdentityPageRequest */
/** @typedef {{readonly name: string}} IdentityNameRequest */
/** @typedef {"viewer" | "member" | "admin" | "owner"} IdentityOrganizationRole */
/** @typedef {Exclude<IdentityOrganizationRole, "owner">} IdentityProjectRole */
/** @typedef {"documents:read" | "documents:write" | "databases:read" | "databases:write"} IdentityKeyScope */
/** @typedef {{readonly name: string; readonly scopes: readonly IdentityKeyScope[]; readonly expires_at?: string | null; readonly allowed_cidrs?: readonly string[] | null}} IdentityOrganizationKeyRequest */
/** Database restrictions use immutable database ULIDs, never names.
 * @typedef {IdentityOrganizationKeyRequest & {readonly database?: string | null}} IdentityProjectKeyRequest */
/** @typedef {{readonly previous_secret_ttl?: number | null}} IdentityRotateKeyRequest */
/** @typedef {{readonly allowed_cidrs: readonly string[]}} IdentityAllowedCidrsRequest */
/** @typedef {{readonly email: string; readonly role: IdentityOrganizationRole; readonly project_id?: string | null}} IdentityInvitationRequest */
/** @typedef {IdentityPageRequest & {readonly project_id?: string}} IdentityInvitationPageRequest */
/** @typedef {{readonly issuer: string; readonly client_id: string; readonly client_secret?: string | null}} IdentitySsoOidcRequest */
/** @typedef {{readonly domain: string; readonly protocol: "oidc" | null; readonly default_role: IdentityProjectRole | null; readonly oidc: IdentitySsoOidcRequest}} IdentitySsoConnectionRequest */
/** @typedef {ReturnType<typeof organization>} IdentityOrganization */
/** @typedef {ReturnType<typeof project>} IdentityProject */
/** @typedef {ReturnType<typeof member>} IdentityMember */
/** @typedef {ReturnType<typeof projectMember>} IdentityProjectMember */
/** @typedef {ReturnType<typeof apiKey>} IdentityApiKey */
/** @typedef {ReturnType<typeof keySecret>} IdentityApiKeySecret */
/** @typedef {ReturnType<typeof createTerraScaleIdentityManagementClient>} TerraScaleIdentityManagementClient */
/** @typedef {ReturnType<typeof createTerraScaleAccountDeletionClient>} TerraScaleAccountDeletionClient */
/** @typedef {ReturnType<typeof createTerraScaleProjectIdentityClient>} TerraScaleProjectIdentityClient */
/** @typedef {keyof typeof platformIdentityRoutes} IdentityOperation */
/** @typedef {Partial<Record<"organization" | "project" | "user" | "key" | "invitation" | "provider", string>>} IdentitySelectors */

/** @param {IdentityPageRequest} request */
function paging(request) {
  members(request, ["limit", "after"]);
  if (request.limit !== undefined && (!Number.isInteger(request.limit) || request.limit < 1 || request.limit > 100))
    throw new TypeError("limit must be 1-100.");
  if (request.after !== undefined && (typeof request.after !== "string" || request.after === ""))
    throw new TypeError("after must be a non-empty opaque cursor.");
  return request;
}
/** @param {IdentityNameRequest} request */
function named(request) {
  members(request, ["name"]);
  if (typeof request.name !== "string" || request.name.trim().length < 1 || request.name.trim().length > 128 ||
      /\p{Cc}/u.test(request.name) || !/[\p{L}\p{N}]/u.test(request.name))
    throw new TypeError("name must contain a letter or digit and 1-128 characters after trimming, without controls.");
  return request;
}
/** @param {IdentityAllowedCidrsRequest} request */
function allowedCidrs(request) {
  members(request, ["allowed_cidrs"]);
  const values = request.allowed_cidrs;
  if (!Array.isArray(values) || values.length > 32 || values.some(value => typeof value !== "string" ||
      value.length > 64 || !/^[0-9a-fA-F:.]+(?:\/\d{1,3})?$/u.test(value)))
    throw new TypeError("allowed_cidrs must contain at most 32 IPv4/IPv6 addresses or CIDRs.");
  return request;
}
/** @param {IdentityProjectKeyRequest} request @param {boolean} projectKey */
function newKey(request, projectKey) {
  members(request, projectKey ? ["name", "scopes", "expires_at", "allowed_cidrs", "database"] : ["name", "scopes", "expires_at", "allowed_cidrs"]);
  named({ name: request.name });
  if (!Array.isArray(request.scopes) || request.scopes.length === 0 || new Set(request.scopes).size !== request.scopes.length)
    throw new TypeError("scopes must be a non-empty set of TerraBase scopes.");
  request.scopes.forEach(scope);
  if (request.database !== undefined && request.database !== null) requireId(request.database, "Database ID");
  if (request.expires_at !== undefined && request.expires_at !== null) {
    instant(request.expires_at);
    if (Date.parse(request.expires_at) <= Date.now()) throw new TypeError("expires_at must be in the future.");
  }
  if (request.allowed_cidrs !== undefined && request.allowed_cidrs !== null) allowedCidrs({ allowed_cidrs: request.allowed_cidrs });
  return request;
}
/** @param {IdentityRotateKeyRequest} request */
function rotate(request) {
  members(request, ["previous_secret_ttl"]);
  if (request.previous_secret_ttl !== undefined && request.previous_secret_ttl !== null &&
      (!Number.isInteger(request.previous_secret_ttl) || request.previous_secret_ttl < 0 || request.previous_secret_ttl > 86400))
    throw new TypeError("previous_secret_ttl must be 0-86400 seconds.");
  return request;
}
/** @param {IdentityInvitationRequest} request */
function invite(request) {
  members(request, ["email", "role", "project_id"]);
  if (typeof request.email !== "string" || request.email.trim() === "") throw new TypeError("email must be a non-empty address.");
  if (request.project_id !== undefined && request.project_id !== null) {
    requireId(request.project_id, "project_id");
    projectRole(request.role);
  } else organizationRole(request.role);
  return request;
}
/** @param {IdentityInvitationPageRequest} request */
function invitationPaging(request) {
  members(request, ["limit", "after", "project_id"]);
  const { project_id, ...rest } = request;
  paging(rest);
  if (project_id !== undefined) requireId(project_id, "project_id");
  return request;
}
/** @param {IdentitySsoConnectionRequest} request */
function sso(request) {
  members(request, ["domain", "protocol", "default_role", "oidc"]);
  if (typeof request.domain !== "string" || request.domain.trim() === "" || request.domain.length > 253)
    throw new TypeError("domain must be an email domain.");
  if (request.protocol !== "oidc" && request.protocol !== null) throw new TypeError("protocol must be oidc or null (the producer default).");
  if (request.default_role !== null) projectRole(request.default_role);
  members(request.oidc, ["issuer", "client_id", "client_secret"]);
  const { issuer, client_id, client_secret } = request.oidc;
  let uri;
  try { uri = new URL(issuer); } catch { throw new TypeError("oidc.issuer must be an https URL."); }
  if (typeof issuer !== "string" || issuer.length > 512 || uri.protocol !== "https:" || uri.username || uri.password || uri.search || uri.hash)
    throw new TypeError("oidc.issuer must be an https URL without credentials, query or fragment.");
  if (typeof client_id !== "string" || client_id.length < 1 || client_id.length > 256 || /\p{Cc}/u.test(client_id))
    throw new TypeError("oidc.client_id must contain 1-256 characters without controls.");
  if (client_secret !== undefined && client_secret !== null && (typeof client_secret !== "string" || client_secret.length < 1 || client_secret.length > 1024))
    throw new TypeError("oidc.client_secret must contain 1-1024 characters, or be omitted to preserve the stored secret.");
  return request;
}
/** @param {unknown} token @returns {string} */
function signInToken(token) {
  if (typeof token !== "string" || !/^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/u.test(token))
    throw new TypeError("signInToken must be a compact platform sign-in JWT.");
  return token;
}
/** @param {unknown} value @returns {string} */
function projectApiKey(value) {
  if (typeof value !== "string" || !/^[0-7][0-9A-HJKMNP-TV-Z]{25}_[0123456789abcdefghjkmnpqrstvwxyz]{52}$/u.test(value))
    throw new TypeError("projectApiKey must be a canonical platform project API key.");
  return value;
}
/** @param {unknown} value @returns {string} */
function receipt(value) {
  if (typeof value !== "string" || !/^[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]$/u.test(value))
    throw new TypeError("receipt must be a canonical 32-byte base64url deletion receipt.");
  return value;
}
/** @param {unknown} value @returns {"github" | "google"} */
function providerName(value) {
  if (value !== "github" && value !== "google") throw new TypeError("provider must be github or google.");
  return value;
}
/** @param {IdentityInvitationTokenRequest} request */
function invitationToken(request) {
  members(request, ["token"]);
  if (typeof request.token !== "string" || request.token === "") throw new TypeError("token must be a non-empty invitation token.");
  return request;
}

/**
 * Private typed transport shared only by the finite identity clients below.
 * @param {IdentityTransportConfig} config
 * @param {TerraScaleIdentityManagementClientConfig["signInToken"] | undefined} provider
 * @param {(value: unknown) => string} [credential]
 */
function platformCaller(config, provider, credential = signInToken) {
  const origin = requireServiceUrl(config.apiBaseUrl, "apiBaseUrl");
  if (origin.pathname !== "/" || origin.search !== "") throw new TypeError("apiBaseUrl must be a bare auth origin.");
  if (provider !== undefined && typeof provider !== "function") credential(provider);
  const base = { apiBaseUrl: origin.origin, ...(config.fetch === undefined ? {} : { fetch: config.fetch }) };
  // Validate the shared transport's origin policy before the first request.
  new TsAuthHttp(base);

  /**
   * Only finite, producer-published operations reach this private transport.
   * @template T
   * @param {IdentityOperation} operation
   * @param {IdentitySelectors} selectors
   * @param {Decoder<T>} decode
   * @param {IdentityRequestOptions} options
   * @param {object | undefined} [body]
   * @param {IdentityInvitationPageRequest | {readonly organization?: string} | undefined} [query]
   * @param {string | undefined} [deletionReceipt]
   * @returns {Promise<TerraScaleResult<T>>}
   */
  async function call(operation, selectors, decode, options, body, query, deletionReceipt) {
    members(options, ["signal"]);
    const route = platformIdentityRoutes[operation];
    const path = route.pathTemplate.replace(/\{(organization|project|user|key|invitation|provider)\}/gu,
      (_match, name) => encodeURIComponent(name === "provider" ? providerName(selectors.provider) : requireId(Reflect.get(selectors, name), name)));
    // Resolve once per request, under TsAuthHttp's deadline/cancellation.
    // The shared transport does not refresh credentials or replay auth failures.
    /** @type {Promise<string> | undefined} */
    let token;
    const http = new TsAuthHttp({ ...base, ...(provider === undefined ? {} : { accessToken: () => token ??= Promise.resolve().then(async () => {
      try {
        return credential(typeof provider === "function" ? await provider() : provider);
      } catch {
        // A caller's provider can throw a credential-bearing diagnostic. Never
        // forward that exception or its cause into a public transport result.
        throw new Error(credential === signInToken ? "Could not resolve the platform sign-in token." : "Could not resolve the project API key.");
      }
    }) }) });
    /** @type {TerraScaleResult<JsonValue | undefined>} */
    const result = await http.request({ method: route.method, path, routeName: `tsauth.platform.${operation}`,
      ...(body === undefined ? {} : { body }), ...(query === undefined ? {} : { query }),
      ...(deletionReceipt === undefined ? {} : { deletionReceipt }),
      ...(options.signal === undefined ? {} : { signal: options.signal }) });
    if (!result.ok) {
      // Provider and fetch implementations are caller code. Transport exceptions
      // can echo request credentials; retain only our fixed provider diagnostics.
      if (result.error.kind === "transport") return err(result.routeName, { kind: "transport", code: result.error.code,
        message: ["Could not resolve the platform sign-in token.", "Could not resolve the project API key."].includes(result.error.message)
          ? result.error.message : "The platform identity request failed before a response was received." });
      return result;
    }
    try {
      if (!/** @type {readonly number[]} */ (route.successStatuses).includes(result.status))
        throw new TypeError("Unexpected platform success status.");
      return { ...result, value: decode(result.value) };
    } catch {
      return err(result.routeName, { kind: "invalid-response", code: "invalid_response",
        message: "The platform identity response does not match its published contract.", status: result.status,
        requestId: result.headers.get("x-request-id") ?? undefined }, result.status, result.headers);
    }
  }
  return call;
}

/**
 * Uses a live platform sign-in JWT, distinct from project credentials. Producer
 * role checks apply on every request. Mutations are never replayed. Pages use
 * limit/after -> items/next. Deletion 202 is pending; 204 proves logical deletion
 * only, never physical erasure. Invitation tokens are returned once.
 * @param {TerraScaleIdentityManagementClientConfig} config
 */
export function createTerraScaleIdentityManagementClient(config) {
  members(config, ["apiBaseUrl", "signInToken", "fetch"]);
  if (typeof config.signInToken !== "function") signInToken(config.signInToken);
  const call = platformCaller(config, config.signInToken);
  return Object.freeze({
    /** @param {IdentityInvitationTokenRequest} request @param {IdentityRequestOptions} [options] */
    lookupInvitation: (request, options = {}) => call("lookup_invitation", {}, invitationLookup, options, invitationToken(request)),
    /** @param {IdentityInvitationTokenRequest} request @param {IdentityRequestOptions} [options] */
    acceptInvitation: (request, options = {}) => call("accept_invitation", {}, acceptedInvitation, options, invitationToken(request)),
    /** @param {IdentityInvitationTokenRequest} request @param {IdentityRequestOptions} [options] */
    declineInvitation: (request, options = {}) => call("decline_invitation", {}, empty, options, invitationToken(request)),
    /** @param {IdentityRequestOptions} [options] */
    getAccountMfa: (options = {}) => call("get_account_mfa", {}, accountMfa, options),
    /** @param {IdentityRequestOptions} [options] */
    accountLinkedProviders: (options = {}) => call("account_linked_providers", {}, linkedProviders, options),
    /** @param {"github" | "google"} provider @param {{readonly code: string}} request @param {IdentityRequestOptions} [options] */
    accountUnlinkProvider: (provider, request, options = {}) => {
      members(request, ["code"]);
      if (typeof request.code !== "string" || request.code === "") throw new TypeError("code must be a current emailed sign-in code.");
      return call("account_unlink_provider", { provider: providerName(provider) }, empty, options, request);
    },
    /** @param {{readonly receipt?: string}} [request] @param {IdentityRequestOptions} [options] */
    deleteAccount: (request = {}, options = {}) => {
      members(request, ["receipt"]);
      return call("delete_account", {}, empty, options, undefined, undefined, request.receipt === undefined ? undefined : receipt(request.receipt));
    },
    /** @param {IdentityNameRequest} request @param {IdentityRequestOptions} [options] */
    createOrganization: (request, options = {}) => call("create_organization", {}, organization, options, named(request)),
    /** @param {IdentityPageRequest} [request] @param {IdentityRequestOptions} [options] */
    listOrganizations: (request = {}, options = {}) => call("list_organizations", {}, page(organization), options, undefined, paging(request)),
    /** @param {string} organizationId @param {IdentityRequestOptions} [options] */
    getOrganization: (organizationId, options = {}) => call("get_organization", { organization: organizationId }, organization, options),
    /** @param {string} organizationId @param {IdentityNameRequest} request @param {IdentityRequestOptions} [options] */
    renameOrganization: (organizationId, request, options = {}) => call("rename_organization", { organization: organizationId }, organization, options, named(request)),
    /** @param {string} organization @param {IdentityRequestOptions} [options] */
    deleteOrganization: (organization, options = {}) => call("delete_organization", { organization }, empty, options),
    /** @param {string} organization @param {IdentityPageRequest} [request] @param {IdentityRequestOptions} [options] */
    listMembers: (organization, request = {}, options = {}) => call("list_members", { organization }, page(member), options, undefined, paging(request)),
    /** @param {string} organization @param {string} user @param {{readonly role: IdentityOrganizationRole}} request @param {IdentityRequestOptions} [options] */
    changeMemberRole: (organization, user, request, options = {}) => {
      members(request, ["role"]);
      return call("change_member_role", { organization, user }, member, options, { role: organizationRole(request.role) });
    },
    /** @param {string} organization @param {string} user @param {IdentityRequestOptions} [options] */
    removeMember: (organization, user, options = {}) => call("remove_member", { organization, user }, empty, options),
    /** @param {string} organization @param {IdentityRequestOptions} [options] */
    leaveOrganization: (organization, options = {}) => call("leave_organization", { organization }, empty, options),
    /** @param {string} organization @param {IdentityInvitationRequest} request @param {IdentityRequestOptions} [options] */
    createInvitation: (organization, request, options = {}) => call("create_invitation", { organization }, invitationCreated, options, invite(request)),
    /** @param {string} organization @param {IdentityInvitationPageRequest} [request] @param {IdentityRequestOptions} [options] */
    listInvitations: (organization, request = {}, options = {}) => call("list_invitations", { organization }, page(invitation), options, undefined, invitationPaging(request)),
    /** @param {string} organization @param {string} invitation @param {IdentityRequestOptions} [options] */
    revokeInvitation: (organization, invitation, options = {}) => call("revoke_invitation", { organization, invitation }, empty, options),
    /** @param {string} organization @param {{readonly user_id: string}} request @param {IdentityRequestOptions} [options] */
    startOwnershipTransfer: (organization, request, options = {}) => {
      members(request, ["user_id"]);
      return call("start_ownership_transfer", { organization }, ownershipTransfer, options, { user_id: requireId(request.user_id, "user_id") });
    },
    /** @param {string} organization @param {IdentityRequestOptions} [options] */
    getOwnershipTransfer: (organization, options = {}) => call("get_ownership_transfer", { organization }, ownershipTransfer, options),
    /** @param {string} organization @param {IdentityRequestOptions} [options] */
    cancelOwnershipTransfer: (organization, options = {}) => call("cancel_ownership_transfer", { organization }, empty, options),
    /** @param {string} organization @param {IdentityRequestOptions} [options] */
    confirmOwnershipTransfer: (organization, options = {}) => call("confirm_ownership_transfer", { organization }, member, options),
    /** @param {string} organization @param {IdentityRequestOptions} [options] */
    getSsoConnection: (organization, options = {}) => call("get_sso_connection", { organization }, ssoConnection, options),
    /** @param {string} organization @param {IdentitySsoConnectionRequest} request @param {IdentityRequestOptions} [options] */
    putSsoConnection: (organization, request, options = {}) => call("put_sso_connection", { organization }, ssoConnection, options, sso(request)),
    /** @param {string} organization @param {IdentityRequestOptions} [options] */
    deleteSsoConnection: (organization, options = {}) => call("delete_sso_connection", { organization }, empty, options),
    /** @param {string} organization @param {IdentityRequestOptions} [options] */
    verifySsoDomain: (organization, options = {}) => call("verify_sso_domain", { organization }, ssoConnection, options),
    /** @param {string} organizationId @param {{readonly require_mfa: boolean}} request @param {IdentityRequestOptions} [options] */
    setOrganizationMfaPolicy: (organizationId, request, options = {}) => {
      members(request, ["require_mfa"]);
      if (typeof request.require_mfa !== "boolean") throw new TypeError("require_mfa must be boolean.");
      return call("set_organization_mfa_policy", { organization: organizationId }, organization, options, request);
    },
    /** @param {string} organization @param {IdentityPageRequest} [request] @param {IdentityRequestOptions} [options] */
    listAuditEvents: (organization, request = {}, options = {}) => call("list_audit_events", { organization }, page(auditEvent), options, undefined, paging(request)),
    /** @param {string} organization @param {IdentityNameRequest} request @param {IdentityRequestOptions} [options] */
    createProject: (organization, request, options = {}) => call("create_project", { organization }, project, options, named(request)),
    /** @param {string} organization @param {IdentityPageRequest} [request] @param {IdentityRequestOptions} [options] */
    listProjects: (organization, request = {}, options = {}) => call("list_projects", { organization }, page(project), options, undefined, paging(request)),
    /** @param {string} organization @param {string} projectId @param {IdentityRequestOptions} [options] */
    getProject: (organization, projectId, options = {}) => call("get_project", { organization, project: projectId }, project, options),
    /** @param {string} organization @param {string} projectId @param {IdentityNameRequest} request @param {IdentityRequestOptions} [options] */
    renameProject: (organization, projectId, request, options = {}) => call("rename_project", { organization, project: projectId }, project, options, named(request)),
    /** @param {string} organization @param {string} project @param {IdentityRequestOptions} [options] */
    deleteProject: (organization, project, options = {}) => call("delete_project", { organization, project }, empty, options),
    /** @param {string} organization @param {string} projectId @param {IdentityAllowedCidrsRequest} request @param {IdentityRequestOptions} [options] */
    setProjectAllowedCidrs: (organization, projectId, request, options = {}) => call("set_project_allowed_cidrs", { organization, project: projectId }, project, options, allowedCidrs(request)),
    /** @param {string} organization @param {string} project @param {IdentityPageRequest} [request] @param {IdentityRequestOptions} [options] */
    listProjectMembers: (organization, project, request = {}, options = {}) => call("list_project_members", { organization, project }, page(projectMember), options, undefined, paging(request)),
    /** @param {string} organization @param {string} project @param {string} user @param {{readonly role: IdentityProjectRole}} request @param {IdentityRequestOptions} [options] */
    setProjectMemberRole: (organization, project, user, request, options = {}) => {
      members(request, ["role"]);
      return call("set_project_member_role", { organization, project, user }, projectMember, options, { role: projectRole(request.role) });
    },
    /** @param {string} organization @param {string} project @param {string} user @param {IdentityRequestOptions} [options] */
    removeProjectMember: (organization, project, user, options = {}) => call("remove_project_member", { organization, project, user }, empty, options),
    /** @param {string} organization @param {IdentityOrganizationKeyRequest} request @param {IdentityRequestOptions} [options] */
    createOrganizationApiKey: (organization, request, options = {}) => call("create_organization_api_key", { organization }, keySecret, options, newKey(request, false)),
    /** @param {string} organization @param {IdentityPageRequest} [request] @param {IdentityRequestOptions} [options] */
    listOrganizationApiKeys: (organization, request = {}, options = {}) => call("list_organization_api_keys", { organization }, page(apiKey), options, undefined, paging(request)),
    /** @param {string} organization @param {string} key @param {IdentityRequestOptions} [options] */
    getOrganizationApiKey: (organization, key, options = {}) => call("get_organization_api_key", { organization, key }, apiKey, options),
    /** @param {string} organization @param {string} key @param {IdentityRequestOptions} [options] */
    revokeOrganizationApiKey: (organization, key, options = {}) => call("revoke_organization_api_key", { organization, key }, empty, options),
    /** @param {string} organization @param {string} key @param {IdentityRotateKeyRequest} [request] @param {IdentityRequestOptions} [options] */
    rotateOrganizationApiKey: (organization, key, request = {}, options = {}) => call("rotate_organization_api_key", { organization, key }, keySecret, options, rotate(request)),
    /** @param {string} organization @param {string} key @param {IdentityAllowedCidrsRequest} request @param {IdentityRequestOptions} [options] */
    setOrganizationApiKeyAllowedCidrs: (organization, key, request, options = {}) => call("set_organization_api_key_allowed_cidrs", { organization, key }, apiKey, options, allowedCidrs(request)),
    /** @param {string} organization @param {string} project @param {IdentityProjectKeyRequest} request @param {IdentityRequestOptions} [options] */
    createProjectApiKey: (organization, project, request, options = {}) => call("create_project_api_key", { organization, project }, keySecret, options, newKey(request, true)),
    /** @param {string} organization @param {string} project @param {IdentityPageRequest} [request] @param {IdentityRequestOptions} [options] */
    listProjectApiKeys: (organization, project, request = {}, options = {}) => call("list_project_api_keys", { organization, project }, page(apiKey), options, undefined, paging(request)),
    /** @param {string} organization @param {string} project @param {string} key @param {IdentityRequestOptions} [options] */
    getProjectApiKey: (organization, project, key, options = {}) => call("get_project_api_key", { organization, project, key }, apiKey, options),
    /** @param {string} organization @param {string} project @param {string} key @param {IdentityRequestOptions} [options] */
    revokeProjectApiKey: (organization, project, key, options = {}) => call("revoke_project_api_key", { organization, project, key }, empty, options),
    /** @param {string} organization @param {string} project @param {string} key @param {IdentityRotateKeyRequest} [request] @param {IdentityRequestOptions} [options] */
    rotateProjectApiKey: (organization, project, key, request = {}, options = {}) => call("rotate_project_api_key", { organization, project, key }, keySecret, options, rotate(request)),
    /** @param {string} organization @param {string} project @param {string} key @param {IdentityAllowedCidrsRequest} request @param {IdentityRequestOptions} [options] */
    setProjectApiKeyAllowedCidrs: (organization, project, key, request, options = {}) => call("set_project_api_key_allowed_cidrs", { organization, project, key }, apiKey, options, allowedCidrs(request)),
  });
}

/**
 * Receipt-only status/history after sign-in credentials stop working. The receipt
 * is an explicit named field sent only in the published capability header. No
 * bearer token is accepted. Status 204 proves logical deletion, not erasure.
 * @param {TerraScaleAccountDeletionClientConfig} config
 */
export function createTerraScaleAccountDeletionClient(config) {
  members(config, ["apiBaseUrl", "fetch"]);
  const call = platformCaller(config, undefined);
  /** @param {IdentityDeletionStatusRequest} request */
  function validate(request) {
    members(request, ["receipt", "organization"]);
    receipt(request.receipt);
    if (request.organization !== undefined) requireId(request.organization, "organization");
    return request;
  }
  return Object.freeze({
    /** @param {IdentityDeletionStatusRequest} request @param {IdentityRequestOptions} [options] */
    accountDeletionStatus: (request, options = {}) => {
      const { receipt, organization } = validate(request);
      return call("account_deletion_status", {}, empty, options, undefined, organization === undefined ? {} : { organization }, receipt);
    },
    /** @param {IdentityDeletionStatusRequest} request @param {IdentityRequestOptions} [options] */
    accountDeletionHistory: (request, options = {}) => {
      const { receipt, organization } = validate(request);
      return call("account_deletion_history", {}, deletionHistory, options, undefined, organization === undefined ? {} : { organization }, receipt);
    },
  });
}

/**
 * Backend-only assertion using a live unrestricted project API key with both
 * document scopes. This credential is never a browser or sign-in credential.
 * Issued tokens expire within 900 seconds; revocation stops future minting.
 * @param {TerraScaleProjectIdentityClientConfig} config
 */
export function createTerraScaleProjectIdentityClient(config) {
  if (typeof window !== "undefined") throw new Error("Project identity assertion is server-only.");
  members(config, ["apiBaseUrl", "fetch", "project", "projectApiKey"]);
  const project = requireId(config.project, "project");
  if (typeof config.projectApiKey !== "function") projectApiKey(config.projectApiKey);
  const call = platformCaller(config, config.projectApiKey, projectApiKey);
  return Object.freeze({
    /** @param {IdentityEndUserTokenRequest} request @param {IdentityRequestOptions} [options] */
    mintEndUserToken: (request, options = {}) => {
      members(request, ["user_id", "claims"]);
      requireId(request.user_id, "user_id");
      if (request.claims !== undefined && request.claims !== null) {
        members(request.claims, ["roles"]);
        const roles = request.claims.roles;
        if (roles !== undefined && roles !== null && (!Array.isArray(roles) || roles.length > 64 ||
            roles.some(role => typeof role !== "string" || role.trim().length < 1 || role.length > 128 || /\p{Cc}/u.test(role))))
          throw new TypeError("claims.roles must contain at most 64 nonempty strings of 128 characters without controls.");
      }
      if (new TextEncoder().encode(JSON.stringify(request)).byteLength > 16384) throw new TypeError("Token request exceeds 16384 UTF-8 bytes.");
      return call("mintEndUserToken", { project }, endUserToken, options, request);
    },
  });
}
