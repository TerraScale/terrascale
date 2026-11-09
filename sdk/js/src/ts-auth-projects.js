import { requireId } from "./ids.js";
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

const signInTokenPattern = /^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/u;
const maximumExportCursorLength = 8192;

/**
 * @param {string} token
 * @returns {string}
 */
function signInToken(token) {
  if (typeof token !== "string" || !signInTokenPattern.test(token))
    throw new TypeError(
      "signInToken must be a compact TerraScale account sign-in JWT.",
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
      projectAccessToken: signInToken(signIn),
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
      projectAccessToken: signInToken(signIn),
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
      projectAccessToken: signInToken(signIn),
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
      projectAccessToken: signInToken(signIn),
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
      projectAccessToken: signInToken(signIn),
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
      projectAccessToken: signInToken(signIn),
      query: {
        ...(request.limit === undefined ? {} : { limit: request.limit }),
        ...(request.after === undefined ? {} : { after: request.after }),
      },
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
      projectAccessToken: signInToken(signIn),
    });
  }
}
