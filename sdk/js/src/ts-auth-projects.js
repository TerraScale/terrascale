import { requireId } from "./ids.js";
import { TsAuthOrganizationsClient } from "./ts-auth-organizations.js";
/** @import { TsAuthHttp } from "./http.js" */
/** @import { TerraScaleResult } from "./result.js" */

/**
 * @typedef {{
 *   readonly signal?: AbortSignal | undefined;
 * }} TsAuthProjectsRequestOptions
 */

/**
 * @typedef {{
 *   readonly passcode: boolean;
 *   readonly password: boolean;
 *   readonly sign_up: boolean;
 * }} TsAuthProjectMethods
 */

/**
 * @typedef {{
 *   readonly label: string;
 *   readonly url: string;
 * }} TsAuthProjectFooterLink
 */

/**
 * @typedef {{
 *   readonly name: string | null;
 *   readonly logo_url: string | null;
 *   readonly theme_color: string | null;
 *   readonly title_size?: number | undefined;
 *   readonly default_theme?: string | null | undefined;
 *   readonly footer_links?: readonly TsAuthProjectFooterLink[] | null | undefined;
 *   readonly background_color?: string | null | undefined;
 *   readonly text_color?: string | null | undefined;
 *   readonly email_message?: string | null | undefined;
 * }} TsAuthProjectBranding
 */

/**
 * @typedef {{
 *   readonly redirect_urls: readonly string[];
 *   readonly session_lifetime_seconds?: number | undefined;
 * }} TsAuthProjectConfiguration
 */

/**
 * @typedef {{
 *   readonly users: number;
 *   readonly clients: number;
 * }} TsAuthProjectCounts
 */

/**
 * @typedef {{
 *   readonly provider: string;
 *   readonly configured: boolean;
 *   readonly client_id: string | null;
 *   readonly issuer: string | null;
 *   readonly team_id: string | null;
 *   readonly key_id: string | null;
 *   readonly callback_url: string;
 * }} TsAuthProjectProvider
 */

/**
 * @typedef {{
 *   readonly project_id: string;
 *   readonly issuer: string;
 *   readonly discovery_url: string;
 *   readonly sign_in_url: string;
 *   readonly methods: TsAuthProjectMethods;
 *   readonly branding: TsAuthProjectBranding;
 *   readonly counts: TsAuthProjectCounts;
 *   readonly providers: readonly TsAuthProjectProvider[];
 *   readonly configuration?: TsAuthProjectConfiguration | null | undefined;
 * }} TsAuthProjectSettings
 */

/**
 * @typedef {{
 *   readonly methods?: TsAuthProjectMethods | undefined;
 *   readonly branding?: TsAuthProjectBranding | undefined;
 *   readonly configuration?: TsAuthProjectConfiguration | undefined;
 * }} TsAuthProjectSettingsUpdate
 */

/**
 * @typedef {{
 *   readonly id: string;
 *   readonly email: string;
 *   readonly created_at: string;
 *   readonly disabled: boolean;
 * }} TsAuthProjectUser
 */

/**
 * @typedef {{
 *   readonly items: readonly TsAuthProjectUser[];
 *   readonly next: string | null;
 * }} TsAuthProjectUserPage
 */

/**
 * @typedef {{
 *   readonly provider: string;
 *   readonly client_id: string;
 *   readonly subject: string;
 * }} TsAuthProjectLinkedProvider
 */

/**
 * @typedef {{
 *   readonly id: string;
 *   readonly email: string;
 *   readonly email_verified: boolean;
 *   readonly created_at: string;
 *   readonly disabled: boolean;
 *   readonly linked_providers: readonly TsAuthProjectLinkedProvider[];
 * }} TsAuthProjectDirectoryUser
 */

/**
 * @typedef {{
 *   readonly items: readonly TsAuthProjectDirectoryUser[];
 *   readonly next: string | null;
 *   readonly created_before: string;
 * }} TsAuthProjectUserExportPage
 */

/**
 * @typedef {{
 *   readonly search?: string | undefined;
 *   readonly limit?: number | undefined;
 *   readonly after?: string | undefined;
 * }} TsAuthProjectUserListOptions
 */

/**
 * @typedef {{
 *   readonly limit?: number | undefined;
 *   readonly after?: string | undefined;
 * }} TsAuthProjectUserExportOptions
 */

/**
 * @typedef {{
 *   readonly signal?: AbortSignal | undefined;
 *   readonly stepUpProof: string;
 * }} TsAuthProjectsProofOptions
 */

/**
 * @typedef {{
 *   readonly client_id: string;
 *   readonly client_secret?: string | undefined;
 *   readonly issuer?: string | undefined;
 *   readonly team_id?: string | undefined;
 *   readonly key_id?: string | undefined;
 * }} TsAuthProjectProviderUpdate
 */

/**
 * @typedef {{
 *   readonly id: string;
 *   readonly user_id: string;
 *   readonly email: string;
 *   readonly created_at: string;
 *   readonly expires_at: string;
 * }} TsAuthProjectSession
 */

/**
 * @typedef {{
 *   readonly items: readonly TsAuthProjectSession[];
 *   readonly next: string | null;
 * }} TsAuthProjectSessionPage
 */

/**
 * @typedef {{
 *   readonly userId?: string | undefined;
 *   readonly limit?: number | undefined;
 *   readonly after?: string | undefined;
 * }} TsAuthProjectSessionListOptions
 */

/**
 * @typedef {{
 *   readonly client_id: string;
 *   readonly name: string;
 *   readonly type: string;
 *   readonly redirect_uris: readonly string[];
 *   readonly created_at: string;
 *   readonly client_secret?: string | undefined;
 * }} TsAuthProjectOAuthClient
 */

/**
 * @typedef {{
 *   readonly items: readonly TsAuthProjectOAuthClient[];
 *   readonly next: string | null;
 * }} TsAuthProjectClientPage
 */

/**
 * @typedef {{
 *   readonly name: string;
 *   readonly type: string;
 *   readonly redirect_uris: readonly string[];
 * }} TsAuthProjectCreateClientRequest
 */

/**
 * @typedef {{
 *   readonly name: string;
 *   readonly redirect_uris: readonly string[];
 * }} TsAuthProjectUpdateClientRequest
 */

/**
 * @typedef {{
 *   readonly signal?: AbortSignal | undefined;
 *   readonly stepUpProof: string;
 *   readonly grace_seconds?: number | undefined;
 * }} TsAuthProjectsRotateOptions
 */

/**
 * @typedef {{
 *   readonly client_id: string;
 *   readonly client_secret: string;
 *   readonly previous_secret_expires_at: string | null;
 * }} TsAuthProjectClientSecret
 */

/**
 * @param {string} value
 * @returns {string}
 */
function requireProof(value) {
  if (
    typeof value !== "string" ||
    value.length < 1 ||
    value.length > 8192 ||
    /[\s\p{Cc}]/u.test(value)
  )
    throw new TypeError(
      "stepUpProof must be 1-8192 characters without whitespace or control characters.",
    );
  return value;
}

const managementCredentialPattern = /^(?:[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+|[0-9A-HJKMNP-TV-Z]{26}_[0-9abcdefghjkmnpqrstvwxyz]{52})$/u;
const maximumExportCursorLength = 8192;

/**
 * @param {string} token
 * @returns {string}
 */
function managementCredential(token) {
  if (typeof token !== "string" || !managementCredentialPattern.test(token))
    throw new TypeError(
      "Present a TerraScale sign-in JWT or a project secret API key.",
    );
  return token;
}

/**
 * @param {unknown} options
 * @param {readonly string[]} allowed
 */
function members(options, allowed) {
  if (options === null || typeof options !== "object")
    throw new TypeError("options must be an object.");
  for (const key of Object.keys(options))
    if (!allowed.includes(key))
      throw new TypeError(`Unknown option ${key}.`);
}

/**
 * @param {number | undefined} limit
 * @returns {void}
 */
function requireLimit(limit) {
  if (
    limit !== undefined &&
    (!Number.isInteger(limit) || limit < 1 || limit > 100)
  )
    throw new TypeError("limit must be an integer between 1 and 100.");
}

/**
 * @param {string | undefined} after
 * @param {number} maximumLength
 * @returns {void}
 */
function requireCursor(after, maximumLength) {
  if (
    after !== undefined &&
    (typeof after !== "string" || after.length === 0 || after.length > maximumLength)
  )
    throw new TypeError(
      `after must be a non-empty opaque cursor of at most ${maximumLength} characters.`,
    );
}

/**
 * Project tenant administration under /v1/{project} with the caller's own
 * TerraScale account sign-in token; the producer checks the project role on
 * every request (viewer reads, admin changes).
 */
export class TsAuthProjectsClient {
  /** @type {TsAuthOrganizationsClient} */
  organizations;
  /**
   * @type {TsAuthHttp}
   * @readonly
   */
  #http;

  /**
   * @param {TsAuthHttp} http
   */
  constructor(http) {
    this.#http = http;
    this.organizations = new TsAuthOrganizationsClient(http);
  }

  /**
   * GET v1/{project}/settings.
   * @param {string} signIn
   * @param {string} projectId
   * @param {TsAuthProjectsRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthProjectSettings>>}
   */
  getSettings(signIn, projectId, options = {}) {
    members(options, ["signal"]);
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${requireId(projectId, "projectId")}/settings`,
      routeName: "tsauth.projects.get_settings",
      projectAccessToken: managementCredential(signIn),
    });
  }

  /**
   * PUT v1/{project}/settings; omitted parts stay unchanged.
   * @param {string} signIn
   * @param {string} projectId
   * @param {TsAuthProjectSettingsUpdate} request
   * @param {TsAuthProjectsRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthProjectSettings>>}
   */
  updateSettings(signIn, projectId, request, options = {}) {
    members(options, ["signal"]);
    members(request, ["methods", "branding", "configuration"]);
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "PUT",
      path: `v1/${requireId(projectId, "projectId")}/settings`,
      routeName: "tsauth.projects.update_settings",
      projectAccessToken: managementCredential(signIn),
      body: request,
    });
  }

  /**
   * GET v1/{project}/users; search matches an email substring.
   * @param {string} signIn
   * @param {string} projectId
   * @param {TsAuthProjectUserListOptions} [request={}]
   * @param {TsAuthProjectsRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthProjectUserPage>>}
   */
  listUsers(signIn, projectId, request = {}, options = {}) {
    members(request, ["search", "limit", "after"]);
    members(options, ["signal"]);
    requireLimit(request.limit);
    requireCursor(request.after, maximumExportCursorLength);
    if (
      request.search !== undefined &&
      (typeof request.search !== "string" || request.search.length === 0)
    )
      throw new TypeError("search must be a non-empty substring.");
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${requireId(projectId, "projectId")}/users`,
      routeName: "tsauth.projects.list_users",
      projectAccessToken: managementCredential(signIn),
      query: {
        ...(request.search === undefined ? {} : { q: request.search }),
        ...(request.limit === undefined ? {} : { limit: request.limit }),
        ...(request.after === undefined ? {} : { after: request.after }),
      },
    });
  }

  /**
   * GET v1/{project}/users/{user}.
   * @param {string} signIn
   * @param {string} projectId
   * @param {string} userId
   * @param {TsAuthProjectsRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthProjectUser>>}
   */
  getUser(signIn, projectId, userId, options = {}) {
    members(options, ["signal"]);
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${requireId(projectId, "projectId")}/users/${requireId(userId, "userId")}`,
      routeName: "tsauth.projects.get_user",
      projectAccessToken: managementCredential(signIn),
    });
  }

  /**
   * POST v1/{project}/users/{user}/disable; ends every session and token.
   * @param {string} signIn
   * @param {string} projectId
   * @param {string} userId
   * @param {TsAuthProjectsRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthProjectUser>>}
   */
  disableUser(signIn, projectId, userId, options = {}) {
    return this.#userState(signIn, projectId, userId, "disable", options);
  }

  /**
   * POST v1/{project}/users/{user}/enable.
   * @param {string} signIn
   * @param {string} projectId
   * @param {string} userId
   * @param {TsAuthProjectsRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthProjectUser>>}
   */
  enableUser(signIn, projectId, userId, options = {}) {
    return this.#userState(signIn, projectId, userId, "enable", options);
  }

  /**
   * DELETE v1/{project}/users/{user}.
   * @param {string} signIn
   * @param {string} projectId
   * @param {string} userId
   * @param {TsAuthProjectsRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  deleteUser(signIn, projectId, userId, options = {}) {
    members(options, ["signal"]);
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "DELETE",
      path: `v1/${requireId(projectId, "projectId")}/users/${requireId(userId, "userId")}`,
      routeName: "tsauth.projects.delete_user",
      projectAccessToken: managementCredential(signIn),
    });
  }

  /**
   * GET v1/{project}/users/export; one bounded audited directory page. Pass
   * the previous page's next as after; null next ends the export.
   * @param {string} signIn
   * @param {string} projectId
   * @param {TsAuthProjectUserExportOptions} [request={}]
   * @param {TsAuthProjectsRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthProjectUserExportPage>>}
   */
  exportUsers(signIn, projectId, request = {}, options = {}) {
    members(request, ["limit", "after"]);
    members(options, ["signal"]);
    requireLimit(request.limit);
    requireCursor(request.after, maximumExportCursorLength);
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${requireId(projectId, "projectId")}/users/export`,
      routeName: "tsauth.projects.export_users",
      projectAccessToken: managementCredential(signIn),
      query: {
        ...(request.limit === undefined ? {} : { limit: request.limit }),
        ...(request.after === undefined ? {} : { after: request.after }),
      },
    });
  }

  /**
   * PUT v1/{project}/providers/{provider}; an omitted client_secret keeps the
   * stored one. The answered settings list every provider.
   * @param {string} signIn
   * @param {string} projectId
   * @param {"google" | "github" | "apple" | "oidc"} provider
   * @param {TsAuthProjectProviderUpdate} request
   * @param {TsAuthProjectsRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthProjectSettings>>}
   */
  setProvider(signIn, projectId, provider, request, options = {}) {
    members(options, ["signal"]);
    if (
      provider !== "google" &&
      provider !== "github" &&
      provider !== "apple" &&
      provider !== "oidc"
    )
      throw new TypeError(
        "provider must be google, github, apple or oidc.",
      );
    members(request, ["client_id", "client_secret", "issuer", "team_id", "key_id"]);
    if (typeof request.client_id !== "string" || request.client_id.length === 0)
      throw new TypeError("client_id must be a non-empty string.");
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "PUT",
      path: `v1/${requireId(projectId, "projectId")}/providers/${provider}`,
      routeName: "tsauth.projects.set_provider",
      projectAccessToken: managementCredential(signIn),
      body: request,
    });
  }

  /**
   * DELETE v1/{project}/providers/{provider}; the answered settings no longer
   * carry the provider.
   * @param {string} signIn
   * @param {string} projectId
   * @param {"google" | "github" | "apple" | "oidc"} provider
   * @param {TsAuthProjectsRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthProjectSettings>>}
   */
  deleteProvider(signIn, projectId, provider, options = {}) {
    members(options, ["signal"]);
    if (
      provider !== "google" &&
      provider !== "github" &&
      provider !== "apple" &&
      provider !== "oidc"
    )
      throw new TypeError(
        "provider must be google, github, apple or oidc.",
      );
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "DELETE",
      path: `v1/${requireId(projectId, "projectId")}/providers/${provider}`,
      routeName: "tsauth.projects.delete_provider",
      projectAccessToken: managementCredential(signIn),
    });
  }

  /**
   * GET v1/{project}/sessions; live sessions, optionally of one user.
   * @param {string} signIn
   * @param {string} projectId
   * @param {TsAuthProjectSessionListOptions} [request={}]
   * @param {TsAuthProjectsRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthProjectSessionPage>>}
   */
  listSessions(signIn, projectId, request = {}, options = {}) {
    members(request, ["userId", "limit", "after"]);
    members(options, ["signal"]);
    requireLimit(request.limit);
    requireCursor(request.after, maximumExportCursorLength);
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${requireId(projectId, "projectId")}/sessions`,
      routeName: "tsauth.projects.list_sessions",
      projectAccessToken: managementCredential(signIn),
      query: {
        ...(request.userId === undefined
          ? {}
          : { user: requireId(request.userId, "userId") }),
        ...(request.limit === undefined ? {} : { limit: request.limit }),
        ...(request.after === undefined ? {} : { after: request.after }),
      },
    });
  }

  /**
   * DELETE v1/{project}/sessions/{session}; signs the session out.
   * @param {string} signIn
   * @param {string} projectId
   * @param {string} sessionId
   * @param {TsAuthProjectsRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  revokeSession(signIn, projectId, sessionId, options = {}) {
    members(options, ["signal"]);
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "DELETE",
      path: `v1/${requireId(projectId, "projectId")}/sessions/${requireId(sessionId, "sessionId")}`,
      routeName: "tsauth.projects.revoke_session",
      projectAccessToken: managementCredential(signIn),
    });
  }

  /**
   * GET v1/{project}/clients; every client of the tenant in one page, without
   * secrets.
   * @param {string} signIn
   * @param {string} projectId
   * @param {TsAuthProjectsRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthProjectClientPage>>}
   */
  listClients(signIn, projectId, options = {}) {
    members(options, ["signal"]);
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "GET",
      path: `v1/${requireId(projectId, "projectId")}/clients`,
      routeName: "tsauth.projects.list_clients",
      projectAccessToken: managementCredential(signIn),
    });
  }

  /**
   * POST v1/{project}/clients with a create_oauth_client step-up proof. A
   * confidential client's client_secret is answered exactly once. The request
   * is sent once; list before deciding to retry an interrupted response.
   * @param {string} signIn
   * @param {string} projectId
   * @param {TsAuthProjectCreateClientRequest} request
   * @param {TsAuthProjectsProofOptions} options
   * @returns {Promise<TerraScaleResult<TsAuthProjectOAuthClient>>}
   */
  createClient(signIn, projectId, request, options) {
    members(options, ["signal", "stepUpProof"]);
    members(request, ["name", "type", "redirect_uris"]);
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${requireId(projectId, "projectId")}/clients`,
      routeName: "tsauth.projects.create_client",
      projectAccessToken: managementCredential(signIn),
      body: request,
      stepUpProof: requireProof(options.stepUpProof),
    });
  }

  /**
   * PUT v1/{project}/clients/{client_id} with an update_oauth_client step-up
   * proof; replaces the name and redirect URIs, and answers no secret.
   * @param {string} signIn
   * @param {string} projectId
   * @param {string} clientId
   * @param {TsAuthProjectUpdateClientRequest} request
   * @param {TsAuthProjectsProofOptions} options
   * @returns {Promise<TerraScaleResult<TsAuthProjectOAuthClient>>}
   */
  updateClient(signIn, projectId, clientId, request, options) {
    members(options, ["signal", "stepUpProof"]);
    members(request, ["name", "redirect_uris"]);
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "PUT",
      path: `v1/${requireId(projectId, "projectId")}/clients/${requireId(clientId, "clientId")}`,
      routeName: "tsauth.projects.update_client",
      projectAccessToken: managementCredential(signIn),
      body: request,
      stepUpProof: requireProof(options.stepUpProof),
    });
  }

  /**
   * POST v1/{project}/clients/{client_id}/secret/rotate with a
   * rotate_oauth_client step-up proof; the new secret is answered exactly once
   * and the previous one keeps working for the grace (default 3600, 0 to 86400
   * seconds).
   * @param {string} signIn
   * @param {string} projectId
   * @param {string} clientId
   * @param {TsAuthProjectsRotateOptions} options
   * @returns {Promise<TerraScaleResult<TsAuthProjectClientSecret>>}
   */
  rotateClientSecret(signIn, projectId, clientId, options) {
    members(options, ["grace_seconds", "signal", "stepUpProof"]);
    if (
        options.grace_seconds !== undefined &&
        (!Number.isInteger(options.grace_seconds) ||
          options.grace_seconds < 0 ||
          options.grace_seconds > 86400)
      )
      throw new TypeError("grace_seconds must be 0 to 86400 seconds.");
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${requireId(projectId, "projectId")}/clients/${requireId(clientId, "clientId")}/secret/rotate`,
      routeName: "tsauth.projects.rotate_client_secret",
      projectAccessToken: managementCredential(signIn),
      body: {
        ...(options.grace_seconds === undefined
          ? {}
          : { grace_seconds: options.grace_seconds }),
      },
      stepUpProof: requireProof(options.stepUpProof),
    });
  }

  /**
   * DELETE v1/{project}/clients/{client_id}.
   * @param {string} signIn
   * @param {string} projectId
   * @param {string} clientId
   * @param {TsAuthProjectsRequestOptions} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  deleteClient(signIn, projectId, clientId, options = {}) {
    members(options, ["signal"]);
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "DELETE",
      path: `v1/${requireId(projectId, "projectId")}/clients/${requireId(clientId, "clientId")}`,
      routeName: "tsauth.projects.delete_client",
      projectAccessToken: managementCredential(signIn),
    });
  }

  /**
   * @param {string} signIn
   * @param {string} projectId
   * @param {string} userId
   * @param {"disable" | "enable"} action
   * @param {TsAuthProjectsRequestOptions} options
   * @returns {Promise<TerraScaleResult<TsAuthProjectUser>>}
   */
  #userState(signIn, projectId, userId, action, options) {
    members(options, ["signal"]);
    return this.#http.request({
      ...(options.signal === undefined ? {} : { signal: options.signal }),
      method: "POST",
      path: `v1/${requireId(projectId, "projectId")}/users/${requireId(userId, "userId")}/${action}`,
      routeName:
        action === "disable"
          ? "tsauth.projects.disable_user"
          : "tsauth.projects.enable_user",
      projectAccessToken: managementCredential(signIn),
    });
  }
}
