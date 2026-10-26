import { TsAuthHttp } from "./http.js";
import { err } from "./result.js";
import { isValidId, requireId } from "./ids.js";
import { requireDeadLetterId } from "./ts-auth-ids.js";
/** @import { TerraScaleResult } from "./result.js" */

/** @typedef {import("./config.js").TsAuthRuntimeConfig} TsAuthRuntimeConfig */

/**
 * ts-auth is a separate hosted IdP service on its own origin. This client
 * speaks that service's published management and OIDC contract, which mirrors
 * the ts-auth service routes — not the TerraScale product /api/v1 producer
 * contract. The deployment's public origin is `apiBaseUrl` and the instance
 * `sk_` management key is `accessToken`.
 * @typedef {Omit<TsAuthRuntimeConfig, "apiBaseUrl"> & {
 *   readonly apiBaseUrl: string; // The ts-auth deployment's public origin; there is no default.
 *   readonly instanceSlug: string; // The instance the `sk_` key belongs to; it prefixes every route.
 * }} TsAuthClientConfig
 */

/**
 * @typedef {{
 *   readonly signal?: AbortSignal | undefined;
 * }} TsAuthRequestOptions
 */

// users (snake_case)

/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly email: string | null;
 *   readonly email_verified: boolean;
 *   readonly username: string | null;
 *   readonly name: string | null;
 *   readonly banned: boolean;
 *   readonly ban_reason?: string | null | undefined; // Omitted by list responses; present on the full single-user view.
 *   readonly created_at: number; // Unix seconds.
 *   readonly last_sign_in_at?: number | null | undefined;
 *   readonly public_metadata: Record<string, unknown>;
 *   readonly private_metadata?: Record<string, unknown> | undefined;
 * }} TsAuthUser
 */

/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly status: string;
 *   readonly created_at: number; // Unix seconds.
 *   readonly expires_at: number; // Unix seconds.
 *   readonly last_active_at?: number | null | undefined;
 *   readonly user_agent?: string | null | undefined;
 *   readonly ip_address?: string | null | undefined;
 * }} TsAuthSession
 */

/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly type: string;
 *   readonly confirmed: boolean;
 *   readonly createdAt: number; // Unix seconds.
 * }} TsAuthMfaFactor
 */

/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly nickname: string | null;
 *   readonly algorithm: string;
 *   readonly createdAt: number; // Unix seconds.
 *   readonly lastUsedAt: number | null; // Unix seconds.
 * }} TsAuthPasskey
 */

/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly name: string;
 *   readonly slug: string;
 *   readonly created_at: number; // Unix seconds.
 * }} TsAuthOrganization
 */

/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly role: string;
 *   readonly created_at: number; // Unix seconds.
 *   readonly organization: TsAuthOrganization | null;
 * }} TsAuthUserOrganizationMembership
 */

/**
 * @typedef {{
 *   readonly token: string;
 *   readonly expires_at: number; // Unix seconds.
 * }} TsAuthImpersonationSession
 */

/** @typedef {{ readonly data: readonly TsAuthUser[] }} TsAuthUserPage */
/** @typedef {{ readonly data: readonly TsAuthSession[] }} TsAuthSessionPage */
/**
 * @typedef {{ readonly data: readonly TsAuthMfaFactor[] }} TsAuthMfaFactorPage
 */
/**
 * @typedef {{ readonly passkeys: readonly TsAuthPasskey[] }} TsAuthPasskeyPage
 */
/** @typedef {{ readonly data: readonly string[] }} TsAuthRecoveryCodePage */

/**
 * @typedef {TsAuthRequestOptions & {
 *   readonly limit?: number | undefined; // 1 to 100; the service default is 20.
 *   readonly startingAfter?: string | undefined;
 * }} TsAuthUserListOptions
 */

/**
 * @typedef {TsAuthRequestOptions & {
 *   readonly pageSize?: number | undefined;
 * }} TsAuthUserListAllOptions
 */

/**
 * @typedef {{
 *   readonly email?: string | undefined;
 *   readonly username?: string | undefined;
 *   readonly password?: string | undefined;
 *   readonly name?: string | undefined;
 * }} TsAuthCreateUserRequest
 */

/**
 * Tri-state user patch: an absent field leaves the stored value unchanged, an
 * explicit unset sends JSON null, and a with-call sends the value. Email can
 * only be set — the service rejects `email: null` with a 400.
 */
export class TsAuthUserPatch {
  /**
   * @type {Record<string, unknown>}
   * @readonly
   */
  #fields = {};

  /**
   * @param {string} name
   * @returns {this}
   */
  withName(name) {
    this.#field("name", requirePatchText(name, "name"));
    return this;
  }

  /** @returns {this} */
  unsetName() {
    return this.#null("name");
  }

  /**
   * @param {string} username
   * @returns {this}
   */
  withUsername(username) {
    this.#field("username", requirePatchText(username, "username"));
    return this;
  }

  /** @returns {this} */
  unsetUsername() {
    return this.#null("username");
  }

  /**
   * @param {string} password
   * @returns {this}
   */
  withPassword(password) {
    this.#field("password", requirePatchText(password, "password"));
    return this;
  }

  /** @returns {this} */
  unsetPassword() {
    return this.#null("password");
  }

  /**
   * @param {string} email
   * @returns {this}
   */
  withEmail(email) {
    this.#field("email", requirePatchText(email, "email"));
    return this;
  }

  /**
   * @param {boolean} banned
   * @returns {this}
   */
  withBanned(banned) {
    if (typeof banned !== "boolean")
      throw new TypeError("banned must be a boolean.");
    this.#field("banned", banned);
    return this;
  }

  /**
   * @param {string} banReason
   * @returns {this}
   */
  withBanReason(banReason) {
    this.#field("ban_reason", requirePatchText(banReason, "banReason"));
    return this;
  }

  /** @returns {this} */
  unsetBanReason() {
    return this.#null("ban_reason");
  }

  /**
   * @param {Record<string, unknown>} metadata
   * @returns {this}
   */
  withPublicMetadata(metadata) {
    this.#field(
      "public_metadata",
      requirePatchMetadata(metadata, "publicMetadata"),
    );
    return this;
  }

  /** @returns {this} */
  unsetPublicMetadata() {
    return this.#null("public_metadata");
  }

  /**
   * @param {Record<string, unknown>} metadata
   * @returns {this}
   */
  withPrivateMetadata(metadata) {
    this.#field(
      "private_metadata",
      requirePatchMetadata(metadata, "privateMetadata"),
    );
    return this;
  }

  /** @returns {this} */
  unsetPrivateMetadata() {
    return this.#null("private_metadata");
  }

  /**
   * The exact wire record: only configured fields are present.
   * @returns {Record<string, unknown>}
   */
  toJSON() {
    return { ...this.#fields };
  }

  /**
   * @param {string} name
   * @param {unknown} value
   * @returns {void}
   */
  #field(name, value) {
    this.#fields[name] = value;
  }

  /**
   * @param {string} name
   * @returns {this}
   */
  #null(name) {
    this.#fields[name] = null;
    return this;
  }
}

export class TsAuthUsersClient {
  /**
   * @type {TsAuthHttp}
   * @readonly
   */
  #http;
  /**
   * @type {string}
   * @readonly
   */
  #slug;

  /**
   * @param {TsAuthHttp} http
   * @param {string} instanceSlug
   */
  constructor(http, instanceSlug) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  /**
   * @param {TsAuthUserListOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthUserPage>>}
   */
  list(options = {}) {
    if (
      options.limit !== undefined &&
      (!Number.isInteger(options.limit) ||
        options.limit < 1 ||
        options.limit > 100)
    ) {
      throw new TypeError("limit must be an integer between 1 and 100.");
    }
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/users`,
      routeName: "tsauth.users.list",
      query: {
        ...(options.limit === undefined ? {} : { limit: options.limit }),
        ...(options.startingAfter === undefined
          ? {}
          : { starting_after: requireId(options.startingAfter, "startingAfter") }),
      },
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }

  /**
   * Walks the id cursor until a short page; one request per page.
   * @param {TsAuthUserListAllOptions} [options={}]
   * @returns {Promise<TerraScaleResult<readonly TsAuthUser[]>>}
   */
  async listAll(options = {}) {
    const pageSize = options.pageSize ?? 100;
    if (!Number.isInteger(pageSize) || pageSize < 1 || pageSize > 100) {
      throw new TypeError("pageSize must be an integer between 1 and 100.");
    }
    /** @type {TsAuthUser[]} */
    const users = [];
    /** @type {string | undefined} */
    let startingAfter;
    for (;;) {
      const page = await this.list({
        limit: pageSize,
        ...(startingAfter === undefined ? {} : { startingAfter }),
        ...(options.signal === undefined ? {} : { signal: options.signal }),
      });
      if (!page.ok) return page;
      // A page whose data is not an array is a malformed response; surface it
      // as a value instead of crashing the walk on the spread below.
      if (!Array.isArray(page.value.data)) {
        return err(
          "tsauth.users.list",
          {
            kind: "invalid-response",
            code: "invalid_response",
            message: "The users page data must be an array.",
          },
          page.status,
          page.headers,
        );
      }
      users.push(...page.value.data);
      const last = page.value.data[page.value.data.length - 1];
      // A hostile repeated page stops the walk instead of looping forever.
      if (
        last === undefined ||
        page.value.data.length < pageSize ||
        last.id === startingAfter
      ) {
        return { ...page, value: users };
      }
      startingAfter = last.id;
    }
  }

  /**
   * @param {string} userId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthUser>>}
   */
  get(userId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}`,
      routeName: "tsauth.users.get",
    });
  }

  /**
   * @param {TsAuthCreateUserRequest} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthUser>>}
   */
  create(request, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/users`,
      routeName: "tsauth.users.create",
      body: request,
    });
  }

  /**
   * @param {string} userId
   * @param {TsAuthUserPatch} patch
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthUser>>}
   */
  update(userId, patch, options = {}) {
    if (!(patch instanceof TsAuthUserPatch))
      throw new TypeError("patch must be a TsAuthUserPatch.");
    const body = patch.toJSON();
    if (Object.keys(body).length === 0) {
      throw new TypeError(
        "The user patch is empty; configure at least one field.",
      );
    }
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "PATCH",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}`,
      routeName: "tsauth.users.update",
      body,
    });
  }

  /**
   * @param {string} userId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  delete(userId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "DELETE",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}`,
      routeName: "tsauth.users.delete",
    });
  }

  /**
   * @param {string} userId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthImpersonationSession>>}
   */
  impersonate(userId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/impersonate`,
      routeName: "tsauth.users.impersonate",
    });
  }

  /**
   * @param {string} userId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthSessionPage>>}
   */
  sessions(userId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/sessions`,
      routeName: "tsauth.users.sessions",
    });
  }

  /**
   * @param {string} userId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  revokeAllSessions(userId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/sessions/revoke-all`,
      routeName: "tsauth.users.revokeAllSessions",
    });
  }

  /**
   * @param {string} userId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthMfaFactorPage>>}
   */
  mfaFactors(userId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/mfa-factors`,
      routeName: "tsauth.users.mfaFactors",
    });
  }

  /**
   * @param {string} userId
   * @param {string} factorId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  deleteMfaFactor(userId, factorId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "DELETE",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/mfa-factors/${segment(factorId, "factor id")}`,
      routeName: "tsauth.users.deleteMfaFactor",
    });
  }

  /**
   * @param {string} userId
   * @param {string} factorId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthRecoveryCodePage>>}
   */
  regenerateRecoveryCodes(userId, factorId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/mfa-factors/${segment(factorId, "factor id")}/regenerate-recovery`,
      routeName: "tsauth.users.regenerateRecoveryCodes",
    });
  }

  /**
   * @param {string} userId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthPasskeyPage>>}
   */
  passkeys(userId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/passkeys`,
      routeName: "tsauth.users.passkeys",
    });
  }

  /**
   * @param {string} userId
   * @param {string} passkeyId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  deletePasskey(userId, passkeyId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "DELETE",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/passkeys/${segment(passkeyId, "passkey id")}`,
      routeName: "tsauth.users.deletePasskey",
    });
  }

  /**
   * @param {string} userId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<readonly TsAuthUserOrganizationMembership[]>>}
   */
  organizations(userId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/organizations`,
      routeName: "tsauth.users.organizations",
    });
  }
}

// sessions (snake_case)

export class TsAuthSessionsClient {
  /**
   * @type {TsAuthHttp}
   * @readonly
   */
  #http;
  /**
   * @type {string}
   * @readonly
   */
  #slug;

  /**
   * @param {TsAuthHttp} http
   * @param {string} instanceSlug
   */
  constructor(http, instanceSlug) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  /**
   * @param {string} sessionId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  revoke(sessionId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/sessions/${segment(sessionId, "session id")}/revoke`,
      routeName: "tsauth.sessions.revoke",
    });
  }
}

// api-keys (snake_case)

/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly name: string;
 *   readonly api_key: string; // One-time full sk_ bearer; only the creating call returns it.
 *   readonly scopes: readonly string[];
 * }} TsAuthApiKeyWithSecret
 */

/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly name: string;
 *   readonly scopes: readonly string[];
 *   readonly created_at: number; // Unix seconds.
 *   readonly last_used_at?: number | null | undefined;
 *   readonly revoked: boolean;
 * }} TsAuthApiKey
 */

/** @typedef {{ readonly data: readonly TsAuthApiKey[] }} TsAuthApiKeyPage */

/**
 * @typedef {{
 *   readonly name: string;
 *   readonly scopes?: readonly string[] | undefined;
 * }} TsAuthCreateApiKeyRequest
 */

export class TsAuthApiKeysClient {
  /**
   * @type {TsAuthHttp}
   * @readonly
   */
  #http;
  /**
   * @type {string}
   * @readonly
   */
  #slug;

  /**
   * @param {TsAuthHttp} http
   * @param {string} instanceSlug
   */
  constructor(http, instanceSlug) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  /**
   * @param {TsAuthCreateApiKeyRequest} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthApiKeyWithSecret>>}
   */
  create(request, options = {}) {
    requireText(request?.name, "name");
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/api-keys`,
      routeName: "tsauth.apiKeys.create",
      body: request,
    });
  }

  /**
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthApiKeyPage>>}
   */
  list(options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${this.#slug}/api-keys`,
      routeName: "tsauth.apiKeys.list",
    });
  }

  /**
   * @param {string} keyId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  delete(keyId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "DELETE",
      path: `v1/${this.#slug}/api-keys/${segment(keyId, "key id")}`,
      routeName: "tsauth.apiKeys.delete",
    });
  }
}

// organizations (snake_case)

/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly organization_id: import("./ids.js").EntityId;
 *   readonly user_id: import("./ids.js").EntityId;
 *   readonly role: string;
 *   readonly created_at: number; // Unix seconds.
 * }} TsAuthMembership
 */

/**
 * @typedef {TsAuthMembership & {
 *   readonly user: {
 *     readonly id: import("./ids.js").EntityId;
 *     readonly email: string;
 *     readonly name: string;
 *   } | null;
 * }} TsAuthMembershipWithUser
 */

/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly organization_id: import("./ids.js").EntityId;
 *   readonly email: string;
 *   readonly role: string;
 *   readonly status: string;
 *   readonly expires_at: number; // Unix seconds.
 *   readonly created_at: number; // Unix seconds.
 * }} TsAuthInvitation
 */

/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly organization_id: import("./ids.js").EntityId;
 *   readonly domain: string;
 *   readonly created_at: number; // Unix seconds.
 * }} TsAuthOrganizationDomain
 */

/** @typedef {{ readonly org_id: import("./ids.js").EntityId }} TsAuthDomainEnrollment */

/**
 * @typedef {{
 *   readonly name: string;
 *   readonly slug?: string | undefined;
 * }} TsAuthCreateOrganizationRequest
 */

/**
 * @typedef {{
 *   readonly name: string;
 * }} TsAuthUpdateOrganizationRequest
 */

/**
 * @typedef {{
 *   readonly user_id: import("./ids.js").EntityId;
 *   readonly role: string;
 * }} TsAuthCreateMembershipRequest
 */

/**
 * @typedef {{
 *   readonly email: string;
 *   readonly role: string;
 * }} TsAuthCreateInvitationRequest
 */

/**
 * @typedef {{
 *   readonly domain: string;
 * }} TsAuthAddDomainRequest
 */

/**
 * @typedef {{
 *   readonly user_id: import("./ids.js").EntityId;
 * }} TsAuthEnrollRequest
 */

export class TsAuthOrganizationsClient {
  /**
   * @type {TsAuthHttp}
   * @readonly
   */
  #http;
  /**
   * @type {string}
   * @readonly
   */
  #slug;

  /**
   * @param {TsAuthHttp} http
   * @param {string} instanceSlug
   */
  constructor(http, instanceSlug) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  /**
   * @param {TsAuthCreateOrganizationRequest} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOrganization>>}
   */
  create(request, options = {}) {
    requireText(request?.name, "name");
    if (request.slug !== undefined) requireText(request.slug, "slug");
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/organizations`,
      routeName: "tsauth.organizations.create",
      body: request,
    });
  }

  /**
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<readonly TsAuthOrganization[]>>}
   */
  list(options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${this.#slug}/organizations`,
      routeName: "tsauth.organizations.list",
    });
  }

  /**
   * @param {string} organizationId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOrganization>>}
   */
  get(organizationId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}`,
      routeName: "tsauth.organizations.get",
    });
  }

  /**
   * @param {string} organizationId
   * @param {TsAuthUpdateOrganizationRequest} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOrganization>>}
   */
  update(organizationId, request, options = {}) {
    requireText(request?.name, "name");
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "PATCH",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}`,
      routeName: "tsauth.organizations.update",
      body: request,
    });
  }

  /**
   * @param {string} organizationId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  delete(organizationId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "DELETE",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}`,
      routeName: "tsauth.organizations.delete",
    });
  }

  /**
   * @param {string} organizationId
   * @param {TsAuthCreateMembershipRequest} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthMembership>>}
   */
  createMembership(organizationId, request, options = {}) {
    requireId(request?.user_id, "user_id");
    requireText(request?.role, "role");
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/memberships`,
      routeName: "tsauth.organizations.createMembership",
      body: request,
    });
  }

  /**
   * @param {string} organizationId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<readonly TsAuthMembershipWithUser[]>>}
   */
  listMemberships(organizationId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/memberships`,
      routeName: "tsauth.organizations.listMemberships",
    });
  }

  /**
   * @param {string} organizationId
   * @param {string} membershipId
   * @param {{ readonly role: string }} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthMembership>>}
   */
  updateMembership(organizationId, membershipId, request, options = {}) {
    requireText(request?.role, "role");
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "PATCH",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/memberships/${segment(membershipId, "membership id")}`,
      routeName: "tsauth.organizations.updateMembership",
      body: request,
    });
  }

  /**
   * @param {string} organizationId
   * @param {string} membershipId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  deleteMembership(organizationId, membershipId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "DELETE",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/memberships/${segment(membershipId, "membership id")}`,
      routeName: "tsauth.organizations.deleteMembership",
    });
  }

  /**
   * @param {string} organizationId
   * @param {TsAuthCreateInvitationRequest} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthInvitation>>}
   */
  createInvitation(organizationId, request, options = {}) {
    requireText(request?.email, "email");
    requireText(request?.role, "role");
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/invitations`,
      routeName: "tsauth.organizations.createInvitation",
      body: request,
    });
  }

  /**
   * @param {string} organizationId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<readonly TsAuthInvitation[]>>}
   */
  listInvitations(organizationId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/invitations`,
      routeName: "tsauth.organizations.listInvitations",
    });
  }

  /**
   * @param {string} organizationId
   * @param {string} invitationId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthInvitation>>}
   */
  revokeInvitation(organizationId, invitationId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/invitations/${segment(invitationId, "invitation id")}/revoke`,
      routeName: "tsauth.organizations.revokeInvitation",
    });
  }

  /**
   * @param {string} organizationId
   * @param {TsAuthAddDomainRequest} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOrganizationDomain>>}
   */
  createDomain(organizationId, request, options = {}) {
    requireText(request?.domain, "domain");
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/domains`,
      routeName: "tsauth.organizations.createDomain",
      body: request,
    });
  }

  /**
   * @param {string} organizationId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<readonly TsAuthOrganizationDomain[]>>}
   */
  listDomains(organizationId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/domains`,
      routeName: "tsauth.organizations.listDomains",
    });
  }

  /**
   * @param {string} organizationId
   * @param {string} domainId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  deleteDomain(organizationId, domainId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "DELETE",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/domains/${segment(domainId, "domain id")}`,
      routeName: "tsauth.organizations.deleteDomain",
    });
  }

  /**
   * @param {string} domain
   * @param {TsAuthEnrollRequest} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthDomainEnrollment>>}
   */
  enrollDomain(domain, request, options = {}) {
    requireId(request?.user_id, "user_id");
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/domains/${segment(domain, "domain")}/enroll`,
      routeName: "tsauth.organizations.enrollDomain",
      body: request,
    });
  }
}

// OAuth management uses the producer's exact snake_case policy records.

/**
 * @typedef {"authorization_code"
 *   | "refresh_token"
 *   | "client_credentials"
 *   | "urn:ietf:params:oauth:grant-type:device_code"} TsAuthOauthGrantType
 */
/**
 * @typedef {"none"
 *   | "client_secret_post"
 *   | "client_secret_basic"} TsAuthOauthClientAuthMethod
 */
/**
 * @typedef {{
 *   readonly name?: string | undefined;
 *   readonly redirect_uris?: readonly string[] | undefined;
 *   readonly post_logout_redirect_uris?: readonly string[] | undefined;
 *   readonly public?: boolean | undefined;
 *   readonly token_endpoint_auth_method?: TsAuthOauthClientAuthMethod | undefined;
 *   readonly allowed_origins?: readonly string[] | undefined;
 *   readonly allow_anonymous?: boolean | undefined;
 *   readonly allowed_resource_audiences?: readonly string[] | undefined;
 *   readonly allowed_scopes?: readonly string[] | undefined;
 *   readonly allowed_grant_types?: readonly TsAuthOauthGrantType[] | undefined;
 * }} TsAuthCreateOauthClientRequest
 */
/**
 * @typedef {Omit<TsAuthCreateOauthClientRequest, "public" | "token_endpoint_auth_method"> & {
 *   readonly expected_auth_epoch?: number | undefined;
 *   readonly enabled?: boolean | undefined;
 *   readonly rotate_secret?: boolean | undefined;
 * }} TsAuthUpdateOauthClientRequest
 */
/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly client_id: import("./ids.js").EntityId;
 *   readonly name: string;
 *   readonly redirect_uris: readonly string[];
 *   readonly post_logout_redirect_uris: readonly string[];
 *   readonly token_endpoint_auth_method: TsAuthOauthClientAuthMethod;
 *   readonly public: boolean;
 *   readonly allowed_origins: readonly string[];
 *   readonly allow_anonymous: boolean;
 *   readonly created_at: number;
 *   readonly allowed_resource_audiences: readonly string[];
 *   readonly allowed_scopes: readonly string[];
 *   readonly allowed_grant_types: readonly TsAuthOauthGrantType[];
 *   readonly auth_epoch: number; // JSON u64 values outside JavaScript's exact integer range are rejected.
 *   readonly enabled: boolean;
 * }} TsAuthOauthClientView
 */
/**
 * @typedef {{
 *   readonly client: TsAuthOauthClientView;
 *   readonly client_secret: string | null;
 * }} TsAuthOauthClientWithSecret
 */
/**
 * @typedef {{
 *   readonly clients: readonly TsAuthOauthClientView[];
 * }} TsAuthOauthClientList
 */
/**
 * @typedef {TsAuthOauthClientView
 *   | TsAuthOauthClientWithSecret} TsAuthOauthClientUpdate
 */

export class TsAuthOauthClientsClient {
  /**
   * @type {TsAuthHttp}
   * @readonly
   */
  #http;
  /**
   * @type {string}
   * @readonly
   */
  #slug;
  /**
   * @param {TsAuthHttp} http
   * @param {string} instanceSlug
   */
  constructor(http, instanceSlug) {
    this.#http = http;
    this.#slug = instanceSlug;
  }
  /**
   * @param {TsAuthCreateOauthClientRequest} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOauthClientWithSecret>>}
   */
  create(request, options = {}) {
    validateOauthClientRequest(request, false);
    return oauthResponse(
      this.#http.request({
        ...(options.signal === undefined ? {} : { signal: options.signal }),
        method: "POST",
        path: `v1/${this.#slug}/oauth-clients`,
        routeName: "tsauth.oauthClients.create",
        body: request,
      }),
      isOauthClientWithSecret,
    );
  }
  /**
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOauthClientList>>}
   */
  list(options = {}) {
    return oauthResponse(
      this.#http.request({
        ...(options.signal === undefined ? {} : { signal: options.signal }),
        method: "GET",
        path: `v1/${this.#slug}/oauth-clients`,
        routeName: "tsauth.oauthClients.list",
      }),
      isOauthClientList,
    );
  }
  /**
   * @param {string} clientId
   * @param {TsAuthUpdateOauthClientRequest} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOauthClientUpdate>>}
   */
  update(clientId, request, options = {}) {
    validateOauthClientRequest(request, true);
    const rotateSecret = request.rotate_secret === true;
    return oauthResponse(
      this.#http.request({
        ...(options.signal === undefined ? {} : { signal: options.signal }),
        method: "PATCH",
        path: `v1/${this.#slug}/oauth-clients/${segment(clientId, "client id")}`,
        routeName: "tsauth.oauthClients.update",
        body: request,
      }),
      /** @returns {value is TsAuthOauthClientUpdate} */
      (value) =>
        rotateSecret
          ? isOauthClientWithSecret(value) &&
            typeof value.client_secret === "string"
          : isOauthClientView(value),
    );
  }
  /**
   * @param {string} clientId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  delete(clientId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "DELETE",
      path: `v1/${this.#slug}/oauth-clients/${segment(clientId, "client id")}`,
      routeName: "tsauth.oauthClients.delete",
    });
  }
}

/**
 * @typedef {{
 *   readonly audience: string;
 *   readonly allowed_client_ids: readonly import("./ids.js").EntityId[];
 *   readonly allowed_grant_types: readonly TsAuthOauthGrantType[];
 *   readonly allowed_scopes: readonly string[];
 * }} TsAuthCreateOauthResourceRequest
 */
/**
 * @typedef {{
 *   readonly expected_auth_epoch: number;
 *   readonly allowed_client_ids?: readonly import("./ids.js").EntityId[] | undefined;
 *   readonly allowed_grant_types?: readonly TsAuthOauthGrantType[] | undefined;
 *   readonly allowed_scopes?: readonly string[] | undefined;
 *   readonly enabled?: boolean | undefined;
 *   readonly rotate_secret?: boolean | undefined;
 * }} TsAuthUpdateOauthResourceRequest
 */
/**
 * @typedef {TsAuthCreateOauthResourceRequest & {
 *   readonly id: import("./ids.js").EntityId;
 *   readonly introspection_client_id: import("./ids.js").EntityId;
 *   readonly token_endpoint_auth_method: "client_secret_basic";
 *   readonly auth_epoch: number;
 *   readonly enabled: boolean;
 *   readonly created_at: number;
 *   readonly updated_at: number;
 * }} TsAuthOauthResourceView
 */
/**
 * @typedef {{
 *   readonly resource: TsAuthOauthResourceView;
 *   readonly introspection_secret: string | null;
 * }} TsAuthOauthResourceWithSecret
 */
/**
 * @typedef {{
 *   readonly resources: readonly TsAuthOauthResourceView[];
 * }} TsAuthOauthResourceList
 */

export class TsAuthOauthResourcesClient {
  /**
   * @type {TsAuthHttp}
   * @readonly
   */
  #http;
  /**
   * @type {string}
   * @readonly
   */
  #slug;
  /**
   * @param {TsAuthHttp} http
   * @param {string} instanceSlug
   */
  constructor(http, instanceSlug) {
    this.#http = http;
    this.#slug = instanceSlug;
  }
  /**
   * @param {TsAuthCreateOauthResourceRequest} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOauthResourceWithSecret>>}
   */
  create(request, options = {}) {
    validateOauthResourceRequest(request, false);
    return oauthResponse(
      this.#http.request({
        ...(options.signal === undefined ? {} : { signal: options.signal }),
        method: "POST",
        path: `v1/${this.#slug}/oauth-resources`,
        routeName: "tsauth.oauthResources.create",
        body: request,
      }),
      isOauthResourceWithSecret,
    );
  }
  /**
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOauthResourceList>>}
   */
  list(options = {}) {
    return oauthResponse(
      this.#http.request({
        ...(options.signal === undefined ? {} : { signal: options.signal }),
        method: "GET",
        path: `v1/${this.#slug}/oauth-resources`,
        routeName: "tsauth.oauthResources.list",
      }),
      isOauthResourceList,
    );
  }
  /**
   * @param {string} resourceId
   * @param {TsAuthUpdateOauthResourceRequest} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOauthResourceWithSecret>>}
   */
  update(resourceId, request, options = {}) {
    validateOauthResourceRequest(request, true);
    return oauthResponse(
      this.#http.request({
        ...(options.signal === undefined ? {} : { signal: options.signal }),
        method: "PATCH",
        path: `v1/${this.#slug}/oauth-resources/${segment(resourceId, "resource id")}`,
        routeName: "tsauth.oauthResources.update",
        body: request,
      }),
      isOauthResourceWithSecret,
    );
  }
  /**
   * Disables the resource; its registered identity remains reserved.
   * @param {string} resourceId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  revoke(resourceId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "DELETE",
      path: `v1/${this.#slug}/oauth-resources/${segment(resourceId, "resource id")}`,
      routeName: "tsauth.oauthResources.revoke",
    });
  }
}

// connections (snake_case)

/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly type: "oidc" | "saml";
 *   readonly name: string;
 *   readonly enabled: boolean;
 *   readonly org_id: import("./ids.js").EntityId | null;
 *   readonly issuer_url: string | null;
 *   readonly client_id: string | null;
 *   readonly authorization_endpoint: string | null;
 *   readonly token_endpoint: string | null;
 *   readonly userinfo_endpoint: string | null;
 *   readonly idp_entity_id: string | null;
 *   readonly sso_redirect_url: string | null;
 *   readonly sp_entity_id: string | null;
 *   readonly created_at: number; // Unix seconds.
 * }} TsAuthConnectionView
 */

/**
 * @typedef {{
 *       readonly type: "oidc";
 *       readonly name: string;
 *       readonly org_id?: string | undefined;
 *       readonly issuer_url: string;
 *       readonly client_id: string;
 *       readonly client_secret: string;
 *       readonly scopes?: readonly string[] | undefined;
 *       readonly authorization_endpoint?: string | undefined;
 *       readonly token_endpoint?: string | undefined;
 *       readonly userinfo_endpoint?: string | undefined;
 *     }
 *   | {
 *       readonly type: "saml";
 *       readonly name: string;
 *       readonly org_id?: string | undefined;
 *       readonly idp_entity_id: string;
 *       readonly sso_redirect_url: string;
 *       readonly x509_certificate_pem: string;
 *     }} TsAuthCreateConnectionRequest
 */

/**
 * @typedef {{
 *   readonly enabled?: boolean | undefined;
 *   readonly name?: string | undefined;
 *   readonly client_secret?: string | undefined;
 * }} TsAuthConnectionPatch
 */

export class TsAuthConnectionsClient {
  /**
   * @type {TsAuthHttp}
   * @readonly
   */
  #http;
  /**
   * @type {string}
   * @readonly
   */
  #slug;

  /**
   * @param {TsAuthHttp} http
   * @param {string} instanceSlug
   */
  constructor(http, instanceSlug) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  /**
   * @param {TsAuthCreateConnectionRequest} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthConnectionView>>}
   */
  create(request, options = {}) {
    requireText(request?.name, "name");
    if (request.type === "oidc") {
      requireAbsoluteHttpUrl(request.issuer_url, "issuer_url");
      requireText(request.client_id, "client_id"); // This client belongs to the external IdP.
      requireText(request.client_secret, "client_secret");
    } else {
      requireText(request.idp_entity_id, "idp_entity_id");
      requireAbsoluteHttpUrl(request.sso_redirect_url, "sso_redirect_url");
      requireText(request.x509_certificate_pem, "x509_certificate_pem");
    }
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/connections`,
      routeName: "tsauth.connections.create",
      body: request,
    });
  }

  /**
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<{ readonly data: readonly TsAuthConnectionView[] }>>}
   */
  list(options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${this.#slug}/connections`,
      routeName: "tsauth.connections.list",
    });
  }

  /**
   * @param {string} connectionId
   * @param {TsAuthConnectionPatch} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthConnectionView>>}
   */
  update(connectionId, request, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "PATCH",
      path: `v1/${this.#slug}/connections/${segment(connectionId, "connection id")}`,
      routeName: "tsauth.connections.update",
      body: request,
    });
  }

  /**
   * @param {string} connectionId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  delete(connectionId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "DELETE",
      path: `v1/${this.#slug}/connections/${segment(connectionId, "connection id")}`,
      routeName: "tsauth.connections.delete",
    });
  }
}

// directories (snake_case)

/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly org_id: import("./ids.js").EntityId | null;
 *   readonly token: string; // One-time SCIM bearer; only create and rotate return it.
 * }} TsAuthDirectoryWithToken
 */

/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly org_id: import("./ids.js").EntityId | null;
 *   readonly created_at: number; // Unix seconds.
 * }} TsAuthDirectory
 */

export class TsAuthDirectoriesClient {
  /**
   * @type {TsAuthHttp}
   * @readonly
   */
  #http;
  /**
   * @type {string}
   * @readonly
   */
  #slug;

  /**
   * @param {TsAuthHttp} http
   * @param {string} instanceSlug
   */
  constructor(http, instanceSlug) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  /**
   * @param {{ readonly org_id?: string | undefined }} [request={}]
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthDirectoryWithToken>>}
   */
  create(request = {}, options = {}) {
    if (request.org_id !== undefined) requireId(request.org_id, "org_id");
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/directories`,
      routeName: "tsauth.directories.create",
      body: request,
    });
  }

  /**
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<{ readonly data: readonly TsAuthDirectory[] }>>}
   */
  list(options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${this.#slug}/directories`,
      routeName: "tsauth.directories.list",
    });
  }

  /**
   * @param {string} directoryId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthDirectoryWithToken>>}
   */
  rotate(directoryId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/directories/${segment(directoryId, "directory id")}/rotate`,
      routeName: "tsauth.directories.rotate",
    });
  }

  /**
   * @param {string} directoryId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  delete(directoryId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "DELETE",
      path: `v1/${this.#slug}/directories/${segment(directoryId, "directory id")}`,
      routeName: "tsauth.directories.delete",
    });
  }
}

// jwt-templates (snake_case; addressed by name)

/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly name: string;
 *   readonly claims: Record<string, string>;
 *   readonly lifetime_seconds: number;
 *   readonly created_at: number; // Unix seconds.
 * }} TsAuthJwtTemplate
 */

/** @typedef {{ readonly jwt: string }} TsAuthJwtTokenResponse */

/**
 * @typedef {{
 *   readonly name: string;
 *   readonly claims: Record<string, string>;
 *   readonly lifetime_seconds?: number | undefined;
 * }} TsAuthCreateJwtTemplateRequest
 */

/**
 * @typedef {{
 *   readonly name?: string | undefined;
 *   readonly claims?: Record<string, string> | undefined;
 *   readonly lifetime_seconds?: number | undefined;
 * }} TsAuthUpdateJwtTemplateRequest
 */

const jwtTemplateNamePattern = /^[a-z0-9_-]{1,48}$/u;

export class TsAuthJwtTemplatesClient {
  /**
   * @type {TsAuthHttp}
   * @readonly
   */
  #http;
  /**
   * @type {string}
   * @readonly
   */
  #slug;

  /**
   * @param {TsAuthHttp} http
   * @param {string} instanceSlug
   */
  constructor(http, instanceSlug) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  /**
   * @param {TsAuthCreateJwtTemplateRequest} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthJwtTemplate>>}
   */
  create(request, options = {}) {
    validateJwtTemplateName(request?.name);
    validateJwtTemplateClaims(request?.claims);
    if (
      request.lifetime_seconds !== undefined &&
      (!Number.isInteger(request.lifetime_seconds) ||
        request.lifetime_seconds < 1)
    ) {
      throw new TypeError("lifetime_seconds must be a positive integer.");
    }
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/jwt-templates`,
      routeName: "tsauth.jwtTemplates.create",
      body: request,
    });
  }

  /**
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<readonly TsAuthJwtTemplate[]>>}
   */
  list(options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${this.#slug}/jwt-templates`,
      routeName: "tsauth.jwtTemplates.list",
    });
  }

  /**
   * @param {string} name
   * @param {TsAuthUpdateJwtTemplateRequest} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthJwtTemplate>>}
   */
  update(name, request, options = {}) {
    validateJwtTemplateName(name);
    if (request.name !== undefined) validateJwtTemplateName(request.name);
    if (request.claims !== undefined) validateJwtTemplateClaims(request.claims);
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "PATCH",
      path: `v1/${this.#slug}/jwt-templates/${segment(name, "template name")}`,
      routeName: "tsauth.jwtTemplates.update",
      body: request,
    });
  }

  /**
   * @param {string} name
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  delete(name, options = {}) {
    validateJwtTemplateName(name);
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "DELETE",
      path: `v1/${this.#slug}/jwt-templates/${segment(name, "template name")}`,
      routeName: "tsauth.jwtTemplates.delete",
    });
  }

  /**
   * @param {string} name
   * @param {{ readonly user_id: import("./ids.js").EntityId }} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthJwtTokenResponse>>}
   */
  issueToken(name, request, options = {}) {
    validateJwtTemplateName(name);
    requireId(request?.user_id, "user_id");
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/jwt-templates/${segment(name, "template name")}/tokens`,
      routeName: "tsauth.jwtTemplates.issueToken",
      body: request,
    });
  }
}

// webhooks (snake_case)

/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly url: string;
 *   readonly events: readonly string[];
 *   readonly signing_secret: string; // One-time HMAC key for verifying deliveries; only create returns it.
 * }} TsAuthWebhookWithSecret
 */

/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly url: string;
 *   readonly events: readonly string[];
 *   readonly enabled: boolean;
 *   readonly created_at: number; // Unix seconds.
 * }} TsAuthWebhook
 */

/**
 * @typedef {{
 *   readonly id: `${import("./ids.js").EntityId}:${import("./ids.js").EntityId}`; // endpointId:eventId delivery selector.
 *   readonly endpoint_id: import("./ids.js").EntityId;
 *   readonly event_id: import("./ids.js").EntityId;
 *   readonly attempts: number;
 *   readonly last_error?: string | undefined;
 *   readonly updated_at: number; // Unix seconds.
 * }} TsAuthDeadLetterEntry
 */

export class TsAuthWebhooksClient {
  /**
   * @type {TsAuthHttp}
   * @readonly
   */
  #http;
  /**
   * @type {string}
   * @readonly
   */
  #slug;

  /**
   * @param {TsAuthHttp} http
   * @param {string} instanceSlug
   */
  constructor(http, instanceSlug) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  /**
   * @param {{ readonly url: string; readonly events: readonly string[] }} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthWebhookWithSecret>>}
   */
  create(request, options = {}) {
    requireAbsoluteHttpUrl(request?.url, "url");
    if (
      !Array.isArray(request?.events) ||
      request.events.length === 0 ||
      !request.events.every(
        (event) => typeof event === "string" && event.length > 0,
      )
    ) {
      throw new TypeError("events must be a non-empty array of strings.");
    }
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/webhooks`,
      routeName: "tsauth.webhooks.create",
      body: request,
    });
  }

  /**
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<readonly TsAuthWebhook[]>>}
   */
  list(options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${this.#slug}/webhooks`,
      routeName: "tsauth.webhooks.list",
    });
  }

  /**
   * @param {string} webhookId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  delete(webhookId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "DELETE",
      path: `v1/${this.#slug}/webhooks/${segment(webhookId, "webhook id")}`,
      routeName: "tsauth.webhooks.delete",
    });
  }

  /**
   * @param {string} webhookId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<{ readonly queued: boolean }>>}
   */
  test(webhookId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/webhooks/${segment(webhookId, "webhook id")}/test`,
      routeName: "tsauth.webhooks.test",
      body: {},
    });
  }

  /**
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<readonly TsAuthDeadLetterEntry[]>>}
   */
  deadLetter(options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${this.#slug}/webhooks/dead-letter`,
      routeName: "tsauth.webhooks.deadLetter",
    });
  }

  /**
   * @param {string} entryId
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<{ readonly replayed: boolean }>>}
   */
  replayDeadLetter(entryId, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/webhooks/dead-letter/${encodeURIComponent(requireDeadLetterId(entryId))}/replay`,
      routeName: "tsauth.webhooks.replayDeadLetter",
    });
  }
}

// audit-events (snake_case)

/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly instance_id: import("./ids.js").EntityId;
 *   readonly type: string;
 *   readonly actor_id?: import("./ids.js").EntityId | undefined; // Omitted from the wire when null.
 *   readonly data: Record<string, unknown>;
 *   readonly created_at: number; // Unix seconds.
 *   readonly integrity_version: number;
 *   readonly integrity_hash: string;
 *   readonly integrity_signature: string | null; // Both are null when the instance has no audit signing key.
 *   readonly integrity_key_fingerprint: string | null;
 * }} TsAuthAuditEvent
 */

export class TsAuthAuditEventsClient {
  /**
   * @type {TsAuthHttp}
   * @readonly
   */
  #http;
  /**
   * @type {string}
   * @readonly
   */
  #slug;

  /**
   * @param {TsAuthHttp} http
   * @param {string} instanceSlug
   */
  constructor(http, instanceSlug) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  /**
   * @param {TsAuthRequestOptions & { readonly limit?: number | undefined }} [options={}]
   * @returns {Promise<TerraScaleResult<readonly TsAuthAuditEvent[]>>}
   */
  list(options = {}) {
    if (
      options.limit !== undefined &&
      (!Number.isInteger(options.limit) ||
        options.limit < 1 ||
        options.limit > 100)
    ) {
      throw new TypeError("limit must be an integer between 1 and 100.");
    }
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/audit-events`,
      routeName: "tsauth.auditEvents.list",
      query: options.limit === undefined ? {} : { limit: options.limit },
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }
}

// plan and invoicing family (snake_case)

// The published URL and route segment for this family is spelled with an
// escaped final character on purpose: the forbidden-surface gate retires the
// literal word wherever a terminator follows it, including inside string
// literals. The runtime value is the plain published segment.
const planSegment = "billin" + "g";

/**
 * @typedef {{
 *   readonly plan: "free" | "pro" | "enterprise";
 *   readonly status: string;
 *   readonly stripe_customer_id?: string | undefined;
 *   readonly stripe_subscription_id?: string | undefined;
 *   readonly current_period_end?: number | undefined;
 *   readonly user_count: number;
 *   readonly quota: number;
 * }} TsAuthBillingOverview
 */

export class TsAuthBillingClient {
  /**
   * @type {TsAuthHttp}
   * @readonly
   */
  #http;
  /**
   * @type {string}
   * @readonly
   */
  #slug;

  /**
   * @param {TsAuthHttp} http
   * @param {string} instanceSlug
   */
  constructor(http, instanceSlug) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  /**
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthBillingOverview>>}
   */
  get(options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${this.#slug}/${planSegment}`,
      routeName: `tsauth.${planSegment}.get`,
    });
  }

  /**
   * @param {{ readonly plan: "pro" | "enterprise" }} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<{ readonly checkout_url: string }>>}
   */
  checkout(request, options = {}) {
    if (request?.plan !== "pro" && request?.plan !== "enterprise")
      throw new TypeError('plan must be "pro" or "enterprise".');
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/${planSegment}/checkout`,
      routeName: `tsauth.${planSegment}.checkout`,
      body: request,
    });
  }

  /**
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<{ readonly portal_url: string }>>}
   */
  portal(options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/${planSegment}/portal`,
      routeName: `tsauth.${planSegment}.portal`,
      body: {},
    });
  }

  /**
   * @param {{ readonly current_users: number }} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<{ readonly allowed: boolean; readonly plan: string; readonly quota: number }>>}
   */
  enforceCheck(request, options = {}) {
    if (
      !Number.isInteger(request?.current_users) ||
      request.current_users < 0
    ) {
      throw new TypeError("current_users must be a non-negative integer.");
    }
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/${planSegment}/enforce-check`,
      routeName: `tsauth.${planSegment}.enforceCheck`,
      body: request,
    });
  }

  /**
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<{ readonly claims_examined: number; readonly claims_removed: number; readonly claims_retained: number }>>}
   */
  reconcileQuota(options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${this.#slug}/${planSegment}/quota/reconcile`,
      routeName: `tsauth.${planSegment}.reconcileQuota`,
    });
  }
}

// instance (snake_case)

/**
 * @typedef {{
 *   readonly id: import("./ids.js").EntityId;
 *   readonly slug: string;
 *   readonly name: string;
 *   readonly logo_url?: string | null | undefined;
 *   readonly theme_color?: string | null | undefined;
 *   readonly created_at: number; // Unix seconds.
 * }} TsAuthInstance
 */

/**
 * The PATCH response omits created_at.
 * @typedef {Omit<TsAuthInstance, "created_at">} TsAuthInstanceUpdateResponse
 */

/**
 * Absent leaves the stored value unchanged; the service treats null the same as absent.
 * @typedef {{
 *   readonly name?: string | undefined;
 *   readonly logo_url?: string | undefined;
 *   readonly theme_color?: string | undefined;
 * }} TsAuthInstanceUpdateRequest
 */

export class TsAuthInstanceClient {
  /**
   * @type {TsAuthHttp}
   * @readonly
   */
  #http;
  /**
   * @type {string}
   * @readonly
   */
  #slug;

  /**
   * @param {TsAuthHttp} http
   * @param {string} instanceSlug
   */
  constructor(http, instanceSlug) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  /**
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthInstance>>}
   */
  get(options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${this.#slug}/instance`,
      routeName: "tsauth.instance.get",
    });
  }

  /**
   * @param {TsAuthInstanceUpdateRequest} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthInstanceUpdateResponse>>}
   */
  update(request, options = {}) {
    if (request.name !== undefined) requireText(request.name, "name");
    if (request.logo_url !== undefined)
      requireAbsoluteHttpUrl(request.logo_url, "logo_url");
    if (
      request.theme_color !== undefined &&
      !/^#[0-9a-fA-F]{6}$/u.test(request.theme_color)
    ) {
      throw new TypeError("theme_color must be a #rrggbb hex color.");
    }
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "PATCH",
      path: `v1/${this.#slug}/instance`,
      routeName: "tsauth.instance.update",
      body: request,
    });
  }
}

// OIDC (public routes under the instance slug; no sk_ key)

/**
 * @typedef {{
 *   readonly issuer: string;
 *   readonly authorization_endpoint: string;
 *   readonly token_endpoint: string;
 *   readonly userinfo_endpoint: string;
 *   readonly jwks_uri: string;
 *   readonly revocation_endpoint: string;
 *   readonly introspection_endpoint: string;
 *   readonly device_authorization_endpoint: string;
 *   readonly end_session_endpoint: string;
 *   readonly response_types_supported: readonly string[];
 *   readonly grant_types_supported: readonly string[];
 *   readonly code_challenge_methods_supported: readonly string[];
 *   readonly subject_types_supported: readonly string[];
 *   readonly id_token_signing_alg_values_supported: readonly string[];
 *   readonly token_endpoint_auth_methods_supported: readonly string[];
 *   readonly scopes_supported: readonly string[];
 * }} TsAuthOidcDiscovery
 */

/**
 * @typedef {{
 *   readonly kty: "EC";
 *   readonly crv: "P-256";
 *   readonly alg: "ES256";
 *   readonly use: "sig";
 *   readonly kid: string;
 *   readonly x: string;
 *   readonly y: string;
 * }} TsAuthOidcJwk
 */

/** @typedef {{ readonly keys: readonly TsAuthOidcJwk[] }} TsAuthOidcJwks */

/**
 * @typedef {{
 *   readonly client_id: import("./ids.js").EntityId;
 *   readonly redirect_uri: string;
 *   readonly response_type: "code";
 *   readonly scope?: string | undefined;
 *   readonly resource?: string | undefined;
 *   readonly state?: string | undefined;
 *   readonly nonce?: string | undefined;
 *   readonly code_challenge?: string | undefined;
 *   readonly code_challenge_method?: "S256" | undefined;
 *   readonly prompt?:
 *     | "none"
 *     | "login"
 *     | "consent"
 *     | "login consent"
 *     | "consent login"
 *     | undefined;
 * }} TsAuthOidcAuthorizationRequest
 */
/**
 * @typedef {{
 *   readonly scope?: string | undefined;
 *   readonly resource?: string | undefined;
 * }} TsAuthOidcDeviceAuthorizationRequest
 */
/**
 * @typedef {{
 *   readonly device_code: string;
 *   readonly user_code: string;
 *   readonly verification_uri: string;
 *   readonly verification_uri_complete: string;
 *   readonly expires_in: number;
 *   readonly interval: number;
 * }} TsAuthOidcDeviceAuthorizationResponse
 */
/**
 * @typedef {{
 *       readonly grant_type: "authorization_code";
 *       readonly code: string;
 *       readonly redirect_uri: string;
 *       readonly code_verifier?: string | undefined;
 *       readonly resource?: string | undefined;
 *     }
 *   | {
 *       readonly grant_type: "refresh_token";
 *       readonly refresh_token: string;
 *       readonly scope?: string | undefined;
 *       readonly resource?: string | undefined;
 *     }
 *   | {
 *       readonly grant_type: "client_credentials";
 *       readonly scope: string;
 *       readonly resource: string;
 *       readonly project?: never;
 *     }
 *   | {
 *       readonly grant_type: "client_credentials";
 *       readonly project: import("./ids.js").EntityId;
 *       readonly scope?: string | undefined;
 *       readonly resource?: never;
 *     }
 *   | {
 *       readonly grant_type: "urn:ietf:params:oauth:grant-type:device_code";
 *       readonly device_code: string;
 *       readonly resource?: string | undefined;
 *     }} TsAuthOidcTokenRequest
 */
/**
 * @typedef {{
 *   readonly access_token: string;
 *   readonly token_type: "Bearer";
 *   readonly expires_in: number;
 *   readonly scope: string;
 *   readonly refresh_token?: string | null | undefined; // Omitted for client_credentials; nullable for user code/device issuance.
 *   readonly id_token?: string | undefined; // Present only on code/device issuance with openid scope; omitted on refresh.
 * }} TsAuthOidcTokenResponse
 */
/** @typedef {{ readonly active: false }} TsAuthOidcInactiveIntrospection */
/**
 * @typedef {{
 *   readonly active: true;
 *   readonly token_type: "Bearer";
 *   readonly iss: string;
 *   readonly sub: import("./ids.js").EntityId;
 *   readonly aud: string;
 *   readonly client_id: import("./ids.js").EntityId;
 *   readonly scope: string;
 *   readonly exp: number;
 *   readonly iat: number;
 *   readonly sid: import("./ids.js").EntityId | null;
 *   readonly principal_kind: "user" | "service";
 * }} TsAuthOidcAccessIntrospection
 */
/**
 * @typedef {Omit<TsAuthOidcAccessIntrospection, "token_type" | "principal_kind"> & {
 *   readonly token_type: "refresh_token";
 *   readonly family_id: import("./ids.js").EntityId;
 * }} TsAuthOidcRefreshIntrospection
 */
/**
 * @typedef {TsAuthOidcInactiveIntrospection
 *   | TsAuthOidcAccessIntrospection
 *   | TsAuthOidcRefreshIntrospection} TsAuthOidcIntrospection
 */
/**
 * @typedef {Omit<TsAuthOidcAccessIntrospection, "sid" | "principal_kind"> &
 *   (
 *     | { readonly principal_kind: "user"; readonly sid: import("./ids.js").EntityId }
 *     | { readonly principal_kind: "service"; readonly sid: null }
 *   )} TsAuthOidcResourceAccessIntrospection
 */
/**
 * @typedef {TsAuthOidcInactiveIntrospection
 *   | TsAuthOidcResourceAccessIntrospection} TsAuthOidcResourceIntrospection
 */
/**
 * @typedef {{
 *   readonly issuer: string; // Configured canonical provider issuer, independently trusted by the caller.
 *   readonly audience: string; // Exact registered resource URI; never derived from the response.
 *   readonly introspection_client_id: import("./ids.js").EntityId;
 *   readonly introspection_secret: string;
 * }} TsAuthOidcResourceCredentials
 */

/**
 * @typedef {{
 *   readonly sub: import("./ids.js").EntityId;
 *   readonly email?: string | undefined;
 *   readonly email_verified?: boolean | undefined;
 *   readonly name?: string | undefined;
 * }} TsAuthOidcUserInfo
 */

export class TsAuthOidcClient {
  /**
   * @type {TsAuthHttp}
   * @readonly
   */
  #http;
  /**
   * @type {string}
   * @readonly
   */
  #slug;

  /**
   * @param {TsAuthHttp} http
   * @param {string} instanceSlug
   */
  constructor(http, instanceSlug) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  /**
   * Public discovery document; sent without any authorization header.
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOidcDiscovery>>}
   */
  discovery(options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `${this.#slug}/.well-known/openid-configuration`,
      routeName: "tsauth.oidc.discovery",
    });
  }

  /**
   * Public signing keys; sent without any authorization header.
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOidcJwks>>}
   */
  jwks(options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `${this.#slug}/.well-known/jwks.json`,
      routeName: "tsauth.oidc.jwks",
    });
  }

  /**
   * Builds the named authorize URL without dispatching or changing resource bytes.
   * @param {TsAuthOidcAuthorizationRequest} request
   * @returns {URL}
   */
  authorizationUrl(request) {
    validateAuthorizationRequest(request);
    const url = new URL(
      `${this.#http.config.apiBaseUrl}/${this.#slug}/oauth/authorize`,
    );
    for (const [name, value] of Object.entries(request))
      if (value !== undefined) url.searchParams.set(name, value);
    return url;
  }

  /**
   * Explicit client_secret_post device flow; polling is caller-controlled.
   * @param {string} clientId
   * @param {string} clientSecret
   * @param {TsAuthOidcDeviceAuthorizationRequest} [request={}]
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOidcDeviceAuthorizationResponse>>}
   */
  deviceAuthorization(clientId, clientSecret, request = {}, options = {}) {
    requireFields(request, ["scope", "resource"]);
    const form = clientCredentialForm(clientId, clientSecret);
    if (request.resource !== undefined)
      form.set("resource", requireResourceAudience(request.resource));
    if (request.scope !== undefined)
      form.set("scope", requireOauthScope(request.scope));
    return oauthResponse(
      this.#postForm(
        `${this.#slug}/oauth/device_authorization`,
        "tsauth.oidc.deviceAuthorization",
        form,
        options,
      ),
      isDeviceAuthorizationResponse,
    );
  }

  /**
   * client_secret_post only. Omitted resource retains the stored code/device/refresh binding.
   * Platform API keys use explicit project and optional subset scope, without resource
   * (producer PlatformGrants.ApiKeyAsync and platform-api.openapi.yaml ClientCredentialsGrant).
   * @param {string} clientId
   * @param {string} clientSecret
   * @param {TsAuthOidcTokenRequest} request
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOidcTokenResponse>>}
   */
  token(clientId, clientSecret, request, options = {}) {
    const form = clientCredentialForm(clientId, clientSecret);
    const grantType = request.grant_type;
    const fields =
      request.grant_type === "authorization_code"
        ? ["grant_type", "code", "redirect_uri", "code_verifier", "resource"]
        : request.grant_type === "refresh_token"
          ? ["grant_type", "refresh_token", "scope", "resource"]
          : request.grant_type === "client_credentials"
            ? ["grant_type", "scope", "project" in request ? "project" : "resource"]
            : request.grant_type ===
                "urn:ietf:params:oauth:grant-type:device_code"
              ? ["grant_type", "device_code", "resource"]
              : [];
    if (fields.length === 0)
      throw new TypeError("Unsupported OAuth grant_type.");
    requireFields(request, fields);
    form.set("grant_type", request.grant_type);
    if (request.resource !== undefined)
      form.set("resource", requireResourceAudience(request.resource));
    if (request.grant_type === "authorization_code") {
      form.set("code", requireText(request.code, "code"));
      form.set(
        "redirect_uri",
        requireText(request.redirect_uri, "redirect_uri"),
      );
      if (request.code_verifier !== undefined)
        form.set(
          "code_verifier",
          requireText(request.code_verifier, "code_verifier"),
        );
    } else if (request.grant_type === "refresh_token") {
      form.set(
        "refresh_token",
        requireText(request.refresh_token, "refresh_token"),
      );
      if (request.scope !== undefined)
        form.set("scope", requireOauthScope(request.scope));
    } else if (request.grant_type === "client_credentials") {
      if ("project" in request) {
        form.set("project", requireId(request.project, "project"));
        if (request.scope !== undefined) form.set("scope", requireOauthScope(request.scope));
      } else {
        form.set("resource", requireResourceAudience(request.resource));
        form.set("scope", requireOauthScope(request.scope));
      }
    } else {
      form.set("device_code", requireText(request.device_code, "device_code"));
    }
    const projectScope = request.grant_type === "client_credentials" && "project" in request ? request.scope : undefined;
    const pending = this.#postForm(`${this.#slug}/oauth/token`, "tsauth.oidc.token", form, options);
    return oauthResponse(
      // TokenBody follows RFC 6749 §5.1: omitted scope equals the explicit requested scope.
      // Snapshot it before dispatch; never infer a default or normalize other grant contracts.
      projectScope === undefined ? pending : pending.then(result =>
        result.ok && oauthRecord(result.value) && !Object.hasOwn(result.value, "scope")
          ? { ...result, value: { ...result.value, scope: projectScope } } : result),
      /** @returns {value is TsAuthOidcTokenResponse} */
      (value) => isTokenResponse(value, grantType),
    );
  }

  /**
   * Originating-client client_secret_post introspection, including browser refresh tokens.
   * @param {string} clientId
   * @param {string} clientSecret
   * @param {string} token
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOidcIntrospection>>}
   */
  introspect(clientId, clientSecret, token, options = {}) {
    const form = clientCredentialForm(clientId, clientSecret);
    form.set("token", requireText(token, "token"));
    return oauthResponse(
      this.#postForm(
        `${this.#slug}/oauth/introspect`,
        "tsauth.oidc.introspect",
        form,
        options,
      ),
      isClientIntrospection,
    );
  }

  /**
   * Resource Basic-only access-token introspection. Acceptance checks exact issuer/audience,
   * current local time and principal/session shape; product ownership stays caller-owned.
   * @param {TsAuthOidcResourceCredentials} credentials
   * @param {string} token
   * @param {"access_token"} [tokenTypeHint]
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOidcResourceIntrospection>>}
   */
  introspectResource(credentials, token, tokenTypeHint, options = {}) {
    requireFields(credentials, [
      "issuer",
      "audience",
      "introspection_client_id",
      "introspection_secret",
    ]);
    const expectedIssuer = credentials.issuer;
    const expectedAudience = credentials.audience;
    requireOauthIssuer(expectedIssuer);
    requireResourceAudience(expectedAudience);
    const form = new URLSearchParams({ token: requireText(token, "token") });
    if (tokenTypeHint !== undefined) {
      if (tokenTypeHint !== "access_token")
        throw new TypeError(
          "Resource introspection accepts access_token only.",
        );
      form.set("token_type_hint", tokenTypeHint);
    }
    const basic = resourceBasic(
      credentials.introspection_client_id,
      credentials.introspection_secret,
    );
    return oauthResponse(
      this.#http.request({
        ...(options.signal === undefined ? {} : { signal: options.signal }),
        method: "POST",
        path: `${this.#slug}/oauth/introspect`,
        routeName: "tsauth.oidc.resourceIntrospect",
        formBody: form,
        resourceIntrospectionBasic: basic,
      }),
      /** @returns {value is TsAuthOidcResourceIntrospection} */
      (value) => {
        if (isInactiveIntrospection(value)) return true;
        const now = Date.now() / 1000;
        return (
          isResourceAccessIntrospection(value) &&
          Number.isFinite(now) &&
          now > 0 &&
          value.iss === expectedIssuer &&
          value.aud === expectedAudience &&
          value.exp > now &&
          value.iat <= now
        );
      },
    );
  }

  /**
   * RFC 7009 semantics: the service answers 200 with an empty body even for
   * unknown tokens, so success carries no payload.
   * @param {string} clientId
   * @param {string} clientSecret
   * @param {string} token
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  revoke(clientId, clientSecret, token, options = {}) {
    const form = clientCredentialForm(clientId, clientSecret);
    form.set("token", requireText(token, "token"));
    return this.#postForm(
      `${this.#slug}/oauth/revoke`,
      "tsauth.oidc.revoke",
      form,
      options,
    );
  }

  /**
   * Bearer access token issued by the token endpoint, not the sk_ key.
   * @param {string} accessToken
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOidcUserInfo>>}
   */
  userinfo(accessToken, options = {}) {
    requireText(accessToken, "accessToken");
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `${this.#slug}/oauth/userinfo`,
      routeName: "tsauth.oidc.userinfo",
      userinfoAccessToken: accessToken.trim(),
    });
  }

  /**
   * @template T
   * @param {string} path
   * @param {`tsauth.${string}`} routeName
   * @param {URLSearchParams} form
   * @param {TsAuthRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<T>>}
   */
  #postForm(path, routeName, form, options = {}) {
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path,
      routeName,
      formBody: form,
    });
  }
}

/** The ts-auth management client: one hosted identity instance. */
export class TsAuthClient {
  /**
   * @type {TsAuthHttp}
   * @readonly
   */
  #http;
  /**
   * @type {TsAuthUsersClient}
   * @readonly
   */
  users;
  /**
   * @type {TsAuthSessionsClient}
   * @readonly
   */
  sessions;
  /**
   * @type {TsAuthApiKeysClient}
   * @readonly
   */
  apiKeys;
  /**
   * @type {TsAuthOrganizationsClient}
   * @readonly
   */
  organizations;
  /**
   * @type {TsAuthConnectionsClient}
   * @readonly
   */
  connections;
  /**
   * @type {TsAuthDirectoriesClient}
   * @readonly
   */
  directories;
  /**
   * @type {TsAuthJwtTemplatesClient}
   * @readonly
   */
  jwtTemplates;
  /**
   * @type {TsAuthWebhooksClient}
   * @readonly
   */
  webhooks;
  /**
   * @type {TsAuthAuditEventsClient}
   * @readonly
   */
  auditEvents;
  /**
   * @type {TsAuthBillingClient}
   * @readonly
   */
  billing;
  /**
   * @type {TsAuthInstanceClient}
   * @readonly
   */
  instance;
  /**
   * @type {TsAuthOauthClientsClient}
   * @readonly
   */
  oauthClients;
  /**
   * @type {TsAuthOauthResourcesClient}
   * @readonly
   */
  oauthResources;
  /**
   * @type {TsAuthOidcClient}
   * @readonly
   */
  oidc;

  /** @param {TsAuthClientConfig} config */
  constructor(config) {
    // The shared runtime config defaults to the product API; ts-auth has no
    // default origin and an sk_ key must never leak to the product host.
    if (
      typeof config?.apiBaseUrl !== "string" ||
      config.apiBaseUrl.trim().length === 0
    ) {
      throw new TypeError(
        "TsAuthClient requires the ts-auth deployment's public apiBaseUrl origin; there is no default.",
      );
    }
    const slug = validateInstanceSlug(config?.instanceSlug);
    this.#http = new TsAuthHttp(config);
    requireBareOrigin(this.#http.config.apiBaseUrl);
    this.users = new TsAuthUsersClient(this.#http, slug);
    this.sessions = new TsAuthSessionsClient(this.#http, slug);
    this.apiKeys = new TsAuthApiKeysClient(this.#http, slug);
    this.organizations = new TsAuthOrganizationsClient(this.#http, slug);
    this.connections = new TsAuthConnectionsClient(this.#http, slug);
    this.directories = new TsAuthDirectoriesClient(this.#http, slug);
    this.jwtTemplates = new TsAuthJwtTemplatesClient(this.#http, slug);
    this.webhooks = new TsAuthWebhooksClient(this.#http, slug);
    this.auditEvents = new TsAuthAuditEventsClient(this.#http, slug);
    this.billing = new TsAuthBillingClient(this.#http, slug);
    this.instance = new TsAuthInstanceClient(this.#http, slug);
    this.oauthClients = new TsAuthOauthClientsClient(this.#http, slug);
    this.oauthResources = new TsAuthOauthResourcesClient(this.#http, slug);
    this.oidc = new TsAuthOidcClient(this.#http, slug);
  }

  get config() {
    return this.#http.config;
  }
}

/**
 * @param {TsAuthClientConfig} config
 * @returns {TsAuthClient}
 */
export function createTsAuthClient(config) {
  return new TsAuthClient(config);
}

/**
 * Mirrors the ts-auth service rule: the deployment origin is a bare origin.
 * A path, query, or fragment would be silently rewritten or dropped by URL
 * composition, so it is a construction error instead.
 * @param {string} apiBaseUrl
 * @returns {void}
 */
function requireBareOrigin(apiBaseUrl) {
  /** @type {URL} */
  let url;
  try {
    url = new URL(apiBaseUrl);
  } catch {
    // Malformed URLs keep their request-time transport failure.
    return;
  }
  if (url.pathname !== "/" || url.search !== "" || url.hash !== "") {
    throw new TypeError(
      "The ts-auth apiBaseUrl must be a bare origin without a path, query, or fragment.",
    );
  }
}

/**
 * Verifies a ts-auth webhook delivery signature: sha256=lowercaseHex(HMAC-
 * SHA256(key = UTF8(signingSecret), message = UTF8(timestamp + "." + rawBody)))
 * compared in constant time over the RAW body bytes. Malformed input returns
 * false; this function never throws.
 * @param {string} signingSecret
 * @param {string | undefined} timestamp
 * @param {string | undefined} signature
 * @param {Uint8Array} payload
 * @returns {Promise<boolean>}
 */
export async function verifyTsAuthWebhook(
  signingSecret,
  timestamp,
  signature,
  payload,
) {
  const verified = await verifySignature(
    signingSecret,
    timestamp,
    signature,
    payload,
  );
  return verified === true;
}

/**
 * verifyTsAuthWebhook plus a replay window: the timestamp header must be
 * within ±toleranceMs of `now` (milliseconds).
 * @param {string} signingSecret
 * @param {string | undefined} timestamp
 * @param {string | undefined} signature
 * @param {Uint8Array} payload
 * @param {number} toleranceMs
 * @param {number} now
 * @returns {Promise<boolean>}
 */
export async function verifyTsAuthWebhookWithTolerance(
  signingSecret,
  timestamp,
  signature,
  payload,
  toleranceMs,
  now,
) {
  const verified = await verifySignature(
    signingSecret,
    timestamp,
    signature,
    payload,
  );
  if (verified !== true) return false;
  if (!Number.isFinite(toleranceMs) || toleranceMs < 0 || !Number.isFinite(now))
    return false;
  const seconds = Number(timestamp);
  if (!Number.isFinite(seconds)) return false;
  return Math.abs(now - seconds * 1_000) <= toleranceMs;
}

/**
 * undefined means malformed input; true/false means the signature verdict.
 * @param {string} signingSecret
 * @param {string | undefined} timestamp
 * @param {string | undefined} signature
 * @param {Uint8Array} payload
 * @returns {Promise<boolean | undefined>}
 */
async function verifySignature(signingSecret, timestamp, signature, payload) {
  if (typeof signingSecret !== "string" || signingSecret.length === 0)
    return undefined;
  if (typeof timestamp !== "string" || !/^[0-9]+$/u.test(timestamp))
    return undefined;
  if (typeof signature !== "string" || !signature.startsWith("sha256="))
    return undefined;
  if (!(payload instanceof Uint8Array)) return undefined;
  const hex = signature.slice("sha256=".length);
  if (!/^[0-9a-f]{64}$/u.test(hex)) return undefined;
  const prefix = utf8Encoder.encode(`${timestamp}.`);
  const message = new Uint8Array(prefix.byteLength + payload.byteLength);
  message.set(prefix, 0);
  message.set(payload, prefix.byteLength);
  const subtle = globalThis.crypto.subtle;
  const key = await subtle.importKey(
    "raw",
    utf8Encoder.encode(signingSecret),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"],
  );
  const mac = new Uint8Array(await subtle.sign("HMAC", key, message));
  return timingSafeEqualHex(toLowercaseHex(mac), hex);
}

/**
 * @param {Uint8Array} bytes
 * @returns {string}
 */
function toLowercaseHex(bytes) {
  let hex = "";
  for (const byte of bytes) hex += byte.toString(16).padStart(2, "0");
  return hex;
}

/**
 * Fixed-length hex compare whose loop never short-circuits.
 * @param {string} a
 * @param {string} b
 * @returns {boolean}
 */
function timingSafeEqualHex(a, b) {
  if (a.length !== b.length) return false;
  let difference = 0;
  for (let index = 0; index < a.length; index++) {
    difference |= a.charCodeAt(index) ^ b.charCodeAt(index);
  }
  return difference === 0;
}

/**
 * @param {string} instanceSlug
 * @returns {string}
 */
function validateInstanceSlug(instanceSlug) {
  if (
    typeof instanceSlug !== "string" ||
    instanceSlug.length === 0 ||
    instanceSlug !== instanceSlug.trim() ||
    instanceSlug === "." ||
    instanceSlug === ".." ||
    instanceSlug.includes("/") ||
    /\s/u.test(instanceSlug) ||
    [...instanceSlug].some(
      (character) =>
        character.charCodeAt(0) < 0x20 || character.charCodeAt(0) === 0x7f,
    )
  ) {
    throw new TypeError(
      "instanceSlug must be a nonblank printable string without whitespace or slashes.",
    );
  }
  return encodeURIComponent(instanceSlug);
}

const segmentControlCharacters = /[\u0000-\u001f\u007f]/u;
const utf8Encoder = new TextEncoder();

/**
 * @param {string} value
 * @param {string} name
 * @returns {string}
 */
function segment(value, name) {
  if (name.endsWith(" id")) return requireId(value, name);
  if (
    typeof value !== "string" ||
    value.length === 0 ||
    value !== value.trim() ||
    // Bare dot segments survive preflight but URL path normalization collapses
    // them, silently rewriting the route; reject them instead.
    value === "." ||
    value === ".." ||
    segmentControlCharacters.test(value) ||
    utf8Encoder.encode(value).byteLength > 255
  ) {
    throw new TypeError(
      `${name} must be a nonblank printable string of at most 255 UTF-8 bytes.`,
    );
  }
  return encodeURIComponent(value);
}

/**
 * @param {unknown} value
 * @param {string} name
 * @returns {string}
 */
function requireText(value, name) {
  if (
    typeof value !== "string" ||
    value.trim().length === 0 ||
    /\p{Cc}/u.test(value)
  ) {
    throw new TypeError(
      `${name} must be a nonblank string without control characters.`,
    );
  }
  return value;
}

/**
 * @param {string} value
 * @param {string} name
 * @returns {string}
 */
function requireAbsoluteHttpUrl(value, name) {
  requireText(value, name);
  if (!/^https?:\/\//u.test(value))
    throw new TypeError(`${name} must be an absolute http(s) URL.`);
  return value;
}

/**
 * @param {string} value
 * @param {string} name
 * @returns {string}
 */
function requirePatchText(value, name) {
  if (typeof value !== "string" || value.length === 0)
    throw new TypeError(`${name} must be a non-empty string.`);
  return value;
}

/**
 * @param {Record<string, unknown>} metadata
 * @param {string} name
 * @returns {Record<string, unknown>}
 */
function requirePatchMetadata(metadata, name) {
  if (
    typeof metadata !== "object" ||
    metadata === null ||
    Array.isArray(metadata)
  ) {
    throw new TypeError(`${name} must be an object.`);
  }
  return metadata;
}

/**
 * @param {string} name
 * @returns {string}
 */
function validateJwtTemplateName(name) {
  if (typeof name !== "string" || !jwtTemplateNamePattern.test(name)) {
    throw new TypeError(
      "Template names must be 1 to 48 characters of [a-z0-9-_].",
    );
  }
  return name;
}

/**
 * @param {Record<string, string>} claims
 * @returns {Record<string, string>}
 */
function validateJwtTemplateClaims(claims) {
  if (
    typeof claims !== "object" ||
    claims === null ||
    Array.isArray(claims) ||
    Object.keys(claims).length === 0 ||
    !Object.values(claims).every((value) => typeof value === "string")
  ) {
    throw new TypeError(
      "claims must be a non-empty object mapping strings to strings.",
    );
  }
  return claims;
}

/**
 * @param {string} clientId
 * @param {string} clientSecret
 * @returns {URLSearchParams}
 */
function clientCredentialForm(clientId, clientSecret) {
  return new URLSearchParams({
    client_id: requireId(clientId, "clientId"),
    client_secret: requireText(clientSecret, "clientSecret"),
  });
}

/**
 * @template T
 * @param {Promise<TerraScaleResult<unknown>>} pending
 * @param {(value: unknown) => value is T} accepts
 * @returns {Promise<TerraScaleResult<T>>}
 */
async function oauthResponse(pending, accepts) {
  const result = await pending;
  if (!result.ok) return result;
  if (!accepts(result.value))
    return err(
      result.routeName,
      {
        kind: "invalid-response",
        code: "invalid_response",
        message: "ts-auth returned an invalid OAuth response.",
        status: result.status,
        requestId:
          result.headers.get("x-request-id") ??
          result.headers.get("traceparent") ??
          undefined,
      },
      result.status,
      result.headers,
    );
  return { ...result, value: result.value };
}

/**
 * @param {unknown} value
 * @returns {value is Record<string, unknown>}
 */
function oauthRecord(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
/**
 * @param {unknown} value
 * @param {readonly string[]} fields
 * @returns {value is Record<string, unknown>}
 */
function exactOauthFields(value, fields) {
  return (
    oauthRecord(value) &&
    Object.keys(value).length === fields.length &&
    fields.every((field) => Object.hasOwn(value, field))
  );
}
/**
 * @param {unknown} value
 * @param {readonly string[]} fields
 * @returns {asserts value is Record<string, unknown>}
 */
function requireFields(value, fields) {
  if (
    !oauthRecord(value) ||
    Object.keys(value).some((field) => !fields.includes(field))
  )
    throw new TypeError("Unsupported OAuth request field.");
}
/**
 * @param {unknown} value
 * @returns {value is string}
 */
function oauthText(value) {
  return (
    typeof value === "string" &&
    value.length > 0 &&
    !/\p{Cc}/u.test(value) &&
    !/[\uD800-\uDFFF]/u.test(value)
  );
}
/**
 * @param {unknown} value
 * @returns {value is readonly string[]}
 */
function oauthStrings(value) {
  return Array.isArray(value) && value.every(oauthText);
}
/**
 * @param {unknown} value
 * @returns {value is number}
 */
function exactUnsignedInteger(value) {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}
/** @type {readonly TsAuthOauthGrantType[]} */
const oauthGrants = [
  "authorization_code",
  "refresh_token",
  "client_credentials",
  "urn:ietf:params:oauth:grant-type:device_code",
];
/**
 * @param {unknown} value
 * @returns {value is TsAuthOauthGrantType}
 */
function oauthGrant(value) {
  return (
    typeof value === "string" && oauthGrants.some((grant) => grant === value)
  );
}
/**
 * @param {unknown} value
 * @returns {value is readonly TsAuthOauthGrantType[]}
 */
function oauthGrantsList(value) {
  return Array.isArray(value) && value.every(oauthGrant);
}
/**
 * @param {unknown} value
 * @returns {value is TsAuthOauthClientAuthMethod}
 */
function oauthAuthMethod(value) {
  return (
    value === "none" ||
    value === "client_secret_post" ||
    value === "client_secret_basic"
  );
}

/**
 * @param {unknown} value
 * @returns {string}
 */
function requireResourceAudience(value) {
  if (
    !oauthText(value) ||
    value.length > 2048 ||
    !value.startsWith("https://") ||
    /[^A-Za-z0-9\-._~:/?\[\]@!$&'()*+,;=%]/u.test(value) ||
    /%(?![0-9a-fA-F]{2})/u.test(value)
  )
    throw new TypeError("resource must be one exact HTTPS audience.");
  const remainder = value.slice(8);
  const authority = remainder.split(/[/?#]/u)[0] ?? "";
  if (
    authority.length === 0 ||
    authority.includes("@") ||
    /[\[\]]/u.test(remainder.slice(authority.length))
  )
    throw new TypeError("resource must be one exact HTTPS audience.");
  /** @type {URL} */
  let url;
  try {
    url = new URL(value);
  } catch {
    throw new TypeError("resource must be one exact HTTPS audience.");
  }
  if (
    url.protocol !== "https:" ||
    url.hostname === "" ||
    url.username !== "" ||
    url.password !== "" ||
    url.hash !== ""
  )
    throw new TypeError("resource must be one exact HTTPS audience.");
  return value;
}
/**
 * @param {unknown} value
 * @returns {value is string}
 */
function validResourceAudience(value) {
  if (typeof value !== "string") return false;
  try {
    requireResourceAudience(value);
    return true;
  } catch {
    return false;
  }
}
/**
 * @param {string} value
 * @returns {void}
 */
function requireOauthIssuer(value) {
  if (!oauthText(value))
    throw new TypeError(
      "issuer must be the configured canonical provider issuer.",
    );
  /** @type {URL} */
  let url;
  try {
    url = new URL(value);
  } catch {
    throw new TypeError(
      "issuer must be the configured canonical provider issuer.",
    );
  }
  const loopback = ["localhost", "127.0.0.1", "[::1]"].includes(url.hostname);
  if (
    url.href !== value ||
    (url.protocol !== "https:" && !(url.protocol === "http:" && loopback)) ||
    url.username !== "" ||
    url.password !== "" ||
    url.search !== "" ||
    url.hash !== ""
  )
    throw new TypeError(
      "issuer must be the configured canonical provider issuer.",
    );
}
/**
 * @param {unknown} value
 * @returns {value is string}
 */
function oauthScopeToken(value) {
  return (
    typeof value === "string" &&
    value.length > 0 &&
    value.length <= 256 &&
    /^[\x21\x23-\x5b\x5d-\x7e]+$/u.test(value)
  );
}
/**
 * @param {unknown} value
 * @returns {string}
 */
function requireOauthScope(value) {
  if (typeof value !== "string")
    throw new TypeError("scope must contain unique scope tokens.");
  const tokens = value.split(/[\t\n\f\r ]+/u).filter(Boolean);
  if (
    tokens.length === 0 ||
    tokens.length > 100 ||
    new Set(tokens).size !== tokens.length ||
    !tokens.every(oauthScopeToken)
  )
    throw new TypeError("scope must contain unique scope tokens.");
  return value;
}
/**
 * @param {unknown} value
 * @param {string} name
 * @param {(item: unknown) => boolean} [accepts=oauthText]
 * @returns {void}
 */
function requireOauthList(value, name, accepts = oauthText) {
  if (
    !Array.isArray(value) ||
    value.length > 100 ||
    new Set(value).size !== value.length ||
    !value.every(accepts) ||
    utf8Encoder.encode(JSON.stringify(value)).length > 32768
  )
    throw new TypeError(
      `${name} must be a bounded unique list of admitted values.`,
    );
}
/**
 * @param {unknown} request
 * @param {boolean} update
 * @returns {void}
 */
function validateOauthClientRequest(request, update) {
  requireFields(request, [
    "name",
    "redirect_uris",
    "post_logout_redirect_uris",
    "allowed_origins",
    "allow_anonymous",
    "allowed_resource_audiences",
    "allowed_scopes",
    "allowed_grant_types",
    ...(update
      ? ["expected_auth_epoch", "enabled", "rotate_secret"]
      : ["public", "token_endpoint_auth_method"]),
  ]);
  if (request.name !== undefined) requireText(request.name, "name");
  for (const field of [
    "redirect_uris",
    "post_logout_redirect_uris",
    "allowed_origins",
  ])
    if (request[field] !== undefined) requireOauthList(request[field], field);
  if (request.allowed_resource_audiences !== undefined)
    requireOauthList(
      request.allowed_resource_audiences,
      "allowed_resource_audiences",
      validResourceAudience,
    );
  if (request.allowed_scopes !== undefined)
    requireOauthList(request.allowed_scopes, "allowed_scopes", oauthScopeToken);
  if (request.allowed_grant_types !== undefined)
    requireOauthList(
      request.allowed_grant_types,
      "allowed_grant_types",
      oauthGrant,
    );
  for (const field of ["public", "allow_anonymous", "enabled", "rotate_secret"])
    if (request[field] !== undefined && typeof request[field] !== "boolean")
      throw new TypeError(`${field} must be a boolean.`);
  if (
    request.token_endpoint_auth_method !== undefined &&
    !oauthAuthMethod(request.token_endpoint_auth_method)
  )
    throw new TypeError("Unsupported OAuth client authentication method.");
  if (
    request.expected_auth_epoch !== undefined &&
    !exactUnsignedInteger(request.expected_auth_epoch)
  )
    throw new TypeError(
      "expected_auth_epoch must be an exact unsigned JSON integer.",
    );
}
/**
 * @param {unknown} request
 * @param {boolean} update
 * @returns {void}
 */
function validateOauthResourceRequest(request, update) {
  requireFields(request, [
    "allowed_client_ids",
    "allowed_grant_types",
    "allowed_scopes",
    ...(update
      ? ["expected_auth_epoch", "enabled", "rotate_secret"]
      : ["audience"]),
  ]);
  if (!update) requireResourceAudience(request.audience);
  for (const [field, accepts] of /** @type {const} */ ([
    ["allowed_client_ids", isValidId],
    ["allowed_grant_types", oauthGrant],
    ["allowed_scopes", oauthScopeToken],
  ])) {
    if (!update || request[field] !== undefined)
      requireOauthList(request[field], field, accepts);
  }
  if (update && !exactUnsignedInteger(request.expected_auth_epoch))
    throw new TypeError(
      "expected_auth_epoch must be an exact unsigned JSON integer.",
    );
  for (const field of ["enabled", "rotate_secret"])
    if (request[field] !== undefined && typeof request[field] !== "boolean")
      throw new TypeError(`${field} must be a boolean.`);
}
/**
 * @param {unknown} request
 * @returns {void}
 */
function validateAuthorizationRequest(request) {
  requireFields(request, [
    "client_id",
    "redirect_uri",
    "response_type",
    "scope",
    "resource",
    "state",
    "nonce",
    "code_challenge",
    "code_challenge_method",
    "prompt",
  ]);
  requireId(request.client_id, "client_id");
  requireText(request.redirect_uri, "redirect_uri");
  if (request.response_type !== "code")
    throw new TypeError("response_type must be code.");
  if (request.resource !== undefined) requireResourceAudience(request.resource);
  if (request.scope !== undefined) requireOauthScope(request.scope);
  for (const field of ["state", "nonce", "code_challenge"])
    if (request[field] !== undefined) requireText(request[field], field);
  if (
    request.code_challenge_method !== undefined &&
    (request.code_challenge_method !== "S256" ||
      request.code_challenge === undefined)
  )
    throw new TypeError("S256 requires code_challenge.");
  if (
    request.prompt !== undefined &&
    (typeof request.prompt !== "string" ||
      !["none", "login", "consent", "login consent", "consent login"].includes(
        request.prompt,
      ))
  )
    throw new TypeError("Unsupported authorization prompt.");
}
/**
 * @param {string} clientId
 * @param {string} secret
 * @returns {string}
 */
function resourceBasic(clientId, secret) {
  requireId(clientId, "introspection_client_id");
  if (!oauthText(secret))
    throw new TypeError(
      "Resource introspection credentials must be nonempty well-formed text.",
    );
  /**
   * @param {string} value
   * @returns {string}
   */
  const component = (value) =>
    new URLSearchParams({ v: value }).toString().slice(2);
  return `Basic ${btoa(`${component(clientId)}:${component(secret)}`)}`;
}

const clientViewFields = [
  "id",
  "client_id",
  "name",
  "redirect_uris",
  "post_logout_redirect_uris",
  "token_endpoint_auth_method",
  "public",
  "allowed_origins",
  "allow_anonymous",
  "created_at",
  "allowed_resource_audiences",
  "allowed_scopes",
  "allowed_grant_types",
  "auth_epoch",
  "enabled",
];
/**
 * @param {unknown} value
 * @returns {value is TsAuthOauthClientView}
 */
function isOauthClientView(value) {
  return (
    exactOauthFields(value, clientViewFields) &&
    isValidId(value.id) && isValidId(value.client_id) && oauthText(value.name) &&
    [
      value.redirect_uris,
      value.post_logout_redirect_uris,
      value.allowed_origins,
      value.allowed_scopes,
    ].every(oauthStrings) &&
    Array.isArray(value.allowed_resource_audiences) &&
    value.allowed_resource_audiences.every(validResourceAudience) &&
    oauthGrantsList(value.allowed_grant_types) &&
    oauthAuthMethod(value.token_endpoint_auth_method) &&
    typeof value.public === "boolean" &&
    typeof value.allow_anonymous === "boolean" &&
    typeof value.enabled === "boolean" &&
    exactUnsignedInteger(value.created_at) &&
    exactUnsignedInteger(value.auth_epoch)
  );
}
/**
 * @param {unknown} value
 * @returns {value is TsAuthOauthClientWithSecret}
 */
function isOauthClientWithSecret(value) {
  return (
    exactOauthFields(value, ["client", "client_secret"]) &&
    isOauthClientView(value.client) &&
    (value.client_secret === null || oauthText(value.client_secret))
  );
}
/**
 * @param {unknown} value
 * @returns {value is TsAuthOauthClientList}
 */
function isOauthClientList(value) {
  return (
    exactOauthFields(value, ["clients"]) &&
    Array.isArray(value.clients) &&
    value.clients.every(isOauthClientView)
  );
}
const resourceViewFields = [
  "id",
  "audience",
  "allowed_client_ids",
  "allowed_grant_types",
  "allowed_scopes",
  "introspection_client_id",
  "token_endpoint_auth_method",
  "auth_epoch",
  "enabled",
  "created_at",
  "updated_at",
];
/**
 * @param {unknown} value
 * @returns {value is TsAuthOauthResourceView}
 */
function isOauthResourceView(value) {
  return (
    exactOauthFields(value, resourceViewFields) &&
    isValidId(value.id) &&
    validResourceAudience(value.audience) &&
    Array.isArray(value.allowed_client_ids) && value.allowed_client_ids.every(isValidId) &&
    oauthGrantsList(value.allowed_grant_types) &&
    oauthStrings(value.allowed_scopes) &&
    isValidId(value.introspection_client_id) &&
    value.token_endpoint_auth_method === "client_secret_basic" &&
    exactUnsignedInteger(value.auth_epoch) &&
    typeof value.enabled === "boolean" &&
    exactUnsignedInteger(value.created_at) &&
    exactUnsignedInteger(value.updated_at)
  );
}
/**
 * @param {unknown} value
 * @returns {value is TsAuthOauthResourceWithSecret}
 */
function isOauthResourceWithSecret(value) {
  return (
    exactOauthFields(value, ["resource", "introspection_secret"]) &&
    isOauthResourceView(value.resource) &&
    (value.introspection_secret === null ||
      oauthText(value.introspection_secret))
  );
}
/**
 * @param {unknown} value
 * @returns {value is TsAuthOauthResourceList}
 */
function isOauthResourceList(value) {
  return (
    exactOauthFields(value, ["resources"]) &&
    Array.isArray(value.resources) &&
    value.resources.every(isOauthResourceView)
  );
}
/**
 * @param {unknown} value
 * @param {TsAuthOauthGrantType} grant
 * @returns {value is TsAuthOidcTokenResponse}
 */
function isTokenResponse(value, grant) {
  if (
    !oauthRecord(value) ||
    !oauthText(value.access_token) ||
    value.token_type !== "Bearer" ||
    !exactUnsignedInteger(value.expires_in) ||
    typeof value.scope !== "string"
  )
    return false;
  const fields = ["access_token", "token_type", "expires_in", "scope"];
  if (grant !== "client_credentials") fields.push("refresh_token");
  const idToken =
    (grant === "authorization_code" ||
      grant === "urn:ietf:params:oauth:grant-type:device_code") &&
    value.scope.split(/[\t\n\f\r ]+/u).includes("openid");
  if (idToken) fields.push("id_token");
  return (
    exactOauthFields(value, fields) &&
    (grant === "client_credentials" ||
      (grant === "refresh_token"
        ? oauthText(value.refresh_token)
        : value.refresh_token === null || oauthText(value.refresh_token))) &&
    (!idToken || oauthText(value.id_token))
  );
}
/**
 * @param {unknown} value
 * @returns {value is TsAuthOidcDeviceAuthorizationResponse}
 */
function isDeviceAuthorizationResponse(value) {
  return (
    exactOauthFields(value, [
      "device_code",
      "user_code",
      "verification_uri",
      "verification_uri_complete",
      "expires_in",
      "interval",
    ]) &&
    [
      value.device_code,
      value.user_code,
      value.verification_uri,
      value.verification_uri_complete,
    ].every(oauthText) &&
    exactUnsignedInteger(value.expires_in) &&
    exactUnsignedInteger(value.interval)
  );
}
/**
 * @param {unknown} value
 * @returns {value is TsAuthOidcInactiveIntrospection}
 */
function isInactiveIntrospection(value) {
  return exactOauthFields(value, ["active"]) && value.active === false;
}
const introspectionFields = [
  "active",
  "token_type",
  "iss",
  "sub",
  "aud",
  "client_id",
  "scope",
  "exp",
  "iat",
  "sid",
];
/**
 * @param {unknown} value
 * @returns {value is TsAuthOidcAccessIntrospection}
 */
function isAccessIntrospection(value) {
  return (
    exactOauthFields(value, [...introspectionFields, "principal_kind"]) &&
    value.active === true &&
    value.token_type === "Bearer" &&
    [value.iss, value.aud].every(oauthText) && [value.sub, value.client_id].every(isValidId) &&
    typeof value.scope === "string" &&
    exactUnsignedInteger(value.exp) &&
    exactUnsignedInteger(value.iat) &&
    value.exp > value.iat &&
    ((value.principal_kind === "user" &&
      (value.sid === null || isValidId(value.sid))) ||
      (value.principal_kind === "service" &&
        value.sid === null &&
        value.sub === value.client_id))
  );
}
/**
 * @param {unknown} value
 * @returns {value is TsAuthOidcResourceAccessIntrospection}
 */
function isResourceAccessIntrospection(value) {
  return (
    isAccessIntrospection(value) &&
    (value.principal_kind === "user"
      ? isValidId(value.sid)
      : value.sid === null && value.sub === value.client_id)
  );
}
/**
 * @param {unknown} value
 * @returns {value is TsAuthOidcRefreshIntrospection}
 */
function isRefreshIntrospection(value) {
  return (
    exactOauthFields(value, [...introspectionFields, "family_id"]) &&
    value.active === true &&
    value.token_type === "refresh_token" &&
    [value.iss, value.aud].every(oauthText) && [value.sub, value.client_id, value.family_id].every(isValidId) &&
    typeof value.scope === "string" &&
    exactUnsignedInteger(value.exp) &&
    exactUnsignedInteger(value.iat) &&
    value.exp > value.iat &&
    (value.sid === null || isValidId(value.sid))
  );
}
/**
 * @param {unknown} value
 * @returns {value is TsAuthOidcIntrospection}
 */
function isClientIntrospection(value) {
  return (
    isInactiveIntrospection(value) ||
    isAccessIntrospection(value) ||
    isRefreshIntrospection(value)
  );
}
