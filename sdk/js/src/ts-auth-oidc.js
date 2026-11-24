import { err } from "./result.js";
import { requireId } from "./ids.js";
/** @import { TsAuthHttp } from "./http.js" */
/** @import { TerraScaleResult } from "./result.js" */

/**
 * @typedef {{
 *   readonly signal?: AbortSignal | undefined;
 * }} TsAuthRequestOptions
 */

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
 * @typedef {"authorization_code"
 *   | "refresh_token"
 *   | "client_credentials"
 *   | "urn:ietf:params:oauth:grant-type:device_code"} TsAuthOauthGrantType
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
 *   readonly refresh_token?: string | null | undefined;
 *   readonly id_token?: string | undefined;
 * }} TsAuthOidcTokenResponse
 */

/**
 * @typedef {{
 *   readonly sub: import("./ids.js").EntityId;
 *   readonly is_anonymous?: boolean | undefined;
 *   readonly email?: string | undefined;
 *   readonly email_verified?: boolean | undefined;
 *   readonly name?: string | undefined;
 *   readonly given_name?: string | undefined;
 *   readonly family_name?: string | undefined;
 *   readonly picture?: string | undefined;
 *   readonly public_metadata?: Record<string, unknown> | undefined;
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
      projectScope === undefined ? pending : pending.then(result =>
        result.ok && oauthRecord(result.value) && !Object.hasOwn(result.value, "scope")
          ? { ...result, value: { ...result.value, scope: projectScope } } : result),
      /** @returns {value is TsAuthOidcTokenResponse} */
      (value) => isTokenResponse(value, grantType),
    );
  }

  /**
   * Public-client refresh: grant_type=refresh_token with client_id only, no
   * client secret. ts-auth rotates the refresh token on every use.
   * @param {string} clientId
   * @param {string} refreshToken
   * @param {TsAuthRequestOptions & { readonly scope?: string }} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOidcTokenResponse>>}
   */
  refreshPublic(clientId, refreshToken, options = {}) {
    const form = new URLSearchParams({
      client_id: requireId(clientId, "clientId"),
    });
    form.set("grant_type", "refresh_token");
    form.set(
      "refresh_token",
      requireText(refreshToken, "refreshToken"),
    );
    if (options.scope !== undefined) form.set("scope", requireOauthScope(options.scope));
    const pending = this.#postForm(`${this.#slug}/oauth/token`, "tsauth.oidc.token", form, options);
    return oauthResponse(
      pending.then(result => result.ok && oauthRecord(result.value) && !Object.hasOwn(result.value, "scope") && options.scope !== undefined
        ? { ...result, value: { ...result.value, scope: options.scope } } : result),
      /** @returns {value is TsAuthOidcTokenResponse} */
      (value) => isTokenResponse(value, "refresh_token"),
    );
  }

  /** @param {string} clientId @param {string} code @param {string} redirectUri @param {string} verifier @param {TsAuthRequestOptions} [options] */
  exchangePublic(clientId, code, redirectUri, verifier, options = {}) {
    return oauthResponse(this.#postForm(`${this.#slug}/oauth/token`, "tsauth.oidc.token",
      new URLSearchParams({ client_id: requireId(clientId, "clientId"), grant_type: "authorization_code",
        code: requireText(code, "code"), redirect_uri: requireText(redirectUri, "redirectUri"), code_verifier: requireText(verifier, "verifier") }), options),
      /** @returns {value is TsAuthOidcTokenResponse} */
      value => isTokenResponse(value, "authorization_code"));
  }

  /** @param {string} clientId @param {TsAuthRequestOptions} [options] */
  signInGuest(clientId, options = {}) {
    const pending = this.#postForm(`${this.#slug}/oauth/anonymous`, "tsauth.oidc.anonymous",
      new URLSearchParams({ client_id: requireId(clientId, "clientId") }), options);
    return oauthResponse(pending.then(result => result.ok && oauthRecord(result.value) && !Object.hasOwn(result.value, "scope")
      ? { ...result, value: { ...result.value, scope: "openid offline_access" } } : result),
      /** @returns {value is TsAuthOidcTokenResponse} */
      value => isTokenResponse(value, "authorization_code"));
  }

  /** @param {string} clientId @param {string} token @param {TsAuthRequestOptions} [options] @returns {Promise<TerraScaleResult<void>>} */
  revokePublic(clientId, token, options = {}) {
    return this.#postForm(`${this.#slug}/oauth/revoke`, "tsauth.oidc.revoke",
      new URLSearchParams({ client_id: requireId(clientId, "clientId"), token: requireText(token, "token") }), options);
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
   * Bearer access token issued by the token endpoint.
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
 * @returns {value is number}
 */
function exactUnsignedInteger(value) {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
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
      grant === "urn:ietf:params:oauth:grant-type:device_code" ||
      grant === "refresh_token" && Object.hasOwn(value, "id_token")) &&
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
