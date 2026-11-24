import { TsAuthAgentsClient } from "./ts-auth-agents.js";
import { TsAuthHttp } from "./http.js";
import { TsAuthOidcClient } from "./ts-auth-oidc.js";
import { TsAuthProjectsClient } from "./ts-auth-projects.js";
export { verifyAgentToken } from "./ts-auth-agents.js";
export * from "./ts-auth-oidc.js";

/** @typedef {import("./config.js").TsAuthRuntimeConfig} TsAuthRuntimeConfig */

/**
 * ts-auth is a separate hosted IdP service on its own origin. This client
 * speaks that service's published OIDC and project contract, which mirrors
 * the ts-auth service routes — not the TerraScale product /api/v1 producer
 * contract. The deployment's public origin is `apiBaseUrl`.
 * @typedef {Omit<TsAuthRuntimeConfig, "apiBaseUrl" | "accessToken"> & {
 *   readonly apiBaseUrl: string;
 *   readonly instanceSlug: string;
 * }} TsAuthClientConfig
 */

/** The ts-auth client of one instance: its OIDC routes and the project routes. */
export class TsAuthClient {
  /** @type {TsAuthAgentsClient} */
  agents;
  /**
   * @type {TsAuthHttp}
   * @readonly
   */
  #http;
  /**
   * @type {TsAuthOidcClient}
   * @readonly
   */
  oidc;
  /**
   * @type {TsAuthProjectsClient}
   * @readonly
   */
  projects;

  /** @param {TsAuthClientConfig} config */
  constructor(config) {
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
    this.oidc = new TsAuthOidcClient(this.#http, slug);
    this.projects = new TsAuthProjectsClient(this.#http);
    this.agents = new TsAuthAgentsClient(this.#http);
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
