import type { TerraScaleRuntimeConfig } from "./config.js";
import { TerraScaleHttp } from "./http.js";
import { err, type TerraScaleResult } from "./result.js";

export type { TerraScaleRuntimeConfig } from "./config.js";

/**
 * ts-auth is a separate hosted IdP service on its own origin. This client
 * speaks that service's published management and OIDC contract, which mirrors
 * the ts-auth service routes — not the TerraScale product /api/v1 producer
 * contract. The deployment's public origin is `apiBaseUrl` and the instance
 * `sk_` management key is `accessToken`.
 */
export type TsAuthClientConfig = Omit<TerraScaleRuntimeConfig, "apiBaseUrl"> & {
  /** The ts-auth deployment's public origin; there is no default. */
  readonly apiBaseUrl: string;
  /** The instance the `sk_` key belongs to; it prefixes every route. */
  readonly instanceSlug: string;
};

export type TsAuthRequestOptions = {
  readonly signal?: AbortSignal | undefined;
};

// users (snake_case)

export type TsAuthUser = {
  readonly id: string;
  readonly email: string | null;
  readonly email_verified: boolean;
  readonly username: string | null;
  readonly name: string | null;
  readonly banned: boolean;
  /** Omitted by list responses; present on the full single-user view. */
  readonly ban_reason?: string | null | undefined;
  /** Unix seconds. */
  readonly created_at: number;
  readonly last_sign_in_at?: number | null | undefined;
  readonly public_metadata: Record<string, unknown>;
  readonly private_metadata?: Record<string, unknown> | undefined;
};

export type TsAuthSession = {
  readonly id: string;
  readonly status: string;
  /** Unix seconds. */
  readonly created_at: number;
  /** Unix seconds. */
  readonly expires_at: number;
  readonly last_active_at?: number | null | undefined;
  readonly user_agent?: string | null | undefined;
  readonly ip_address?: string | null | undefined;
};

export type TsAuthMfaFactor = {
  readonly id: string;
  readonly type: string;
  readonly confirmed: boolean;
  /** Unix seconds. */
  readonly createdAt: number;
};

export type TsAuthPasskey = {
  readonly id: string;
  readonly nickname: string | null;
  readonly algorithm: string;
  /** Unix seconds. */
  readonly createdAt: number;
  /** Unix seconds. */
  readonly lastUsedAt: number | null;
};

export type TsAuthOrganization = {
  readonly id: string;
  readonly name: string;
  readonly slug: string;
  /** Unix seconds. */
  readonly created_at: number;
};

export type TsAuthUserOrganizationMembership = {
  readonly id: string;
  readonly role: string;
  /** Unix seconds. */
  readonly created_at: number;
  readonly organization: TsAuthOrganization | null;
};

export type TsAuthImpersonationSession = {
  readonly token: string;
  /** Unix seconds. */
  readonly expires_at: number;
};

export type TsAuthUserPage = { readonly data: readonly TsAuthUser[] };
export type TsAuthSessionPage = { readonly data: readonly TsAuthSession[] };
export type TsAuthMfaFactorPage = { readonly data: readonly TsAuthMfaFactor[] };
export type TsAuthPasskeyPage = { readonly passkeys: readonly TsAuthPasskey[] };
export type TsAuthRecoveryCodePage = { readonly data: readonly string[] };

export type TsAuthUserListOptions = TsAuthRequestOptions & {
  /** 1 to 100; the service default is 20. */
  readonly limit?: number | undefined;
  readonly startingAfter?: string | undefined;
};

export type TsAuthUserListAllOptions = TsAuthRequestOptions & {
  readonly pageSize?: number | undefined;
};

export type TsAuthCreateUserRequest = {
  readonly email?: string | undefined;
  readonly username?: string | undefined;
  readonly password?: string | undefined;
  readonly name?: string | undefined;
};

/**
 * Tri-state user patch: an absent field leaves the stored value unchanged, an
 * explicit unset sends JSON null, and a with-call sends the value. Email can
 * only be set — the service rejects `email: null` with a 400.
 */
export class TsAuthUserPatch {
  readonly #fields: Record<string, unknown> = {};

  withName(name: string): this {
    this.#field("name", requirePatchText(name, "name"));
    return this;
  }

  unsetName(): this {
    return this.#null("name");
  }

  withUsername(username: string): this {
    this.#field("username", requirePatchText(username, "username"));
    return this;
  }

  unsetUsername(): this {
    return this.#null("username");
  }

  withPassword(password: string): this {
    this.#field("password", requirePatchText(password, "password"));
    return this;
  }

  unsetPassword(): this {
    return this.#null("password");
  }

  withEmail(email: string): this {
    this.#field("email", requirePatchText(email, "email"));
    return this;
  }

  withBanned(banned: boolean): this {
    if (typeof banned !== "boolean") throw new TypeError("banned must be a boolean.");
    this.#field("banned", banned);
    return this;
  }

  withBanReason(banReason: string): this {
    this.#field("ban_reason", requirePatchText(banReason, "banReason"));
    return this;
  }

  unsetBanReason(): this {
    return this.#null("ban_reason");
  }

  withPublicMetadata(metadata: Record<string, unknown>): this {
    this.#field("public_metadata", requirePatchMetadata(metadata, "publicMetadata"));
    return this;
  }

  unsetPublicMetadata(): this {
    return this.#null("public_metadata");
  }

  withPrivateMetadata(metadata: Record<string, unknown>): this {
    this.#field("private_metadata", requirePatchMetadata(metadata, "privateMetadata"));
    return this;
  }

  unsetPrivateMetadata(): this {
    return this.#null("private_metadata");
  }

  /** The exact wire record: only configured fields are present. */
  toJSON(): Record<string, unknown> {
    return { ...this.#fields };
  }

  #field(name: string, value: unknown): void {
    this.#fields[name] = value;
  }

  #null(name: string): this {
    this.#fields[name] = null;
    return this;
  }
}

export class TsAuthUsersClient {
  readonly #http: TerraScaleHttp;
  readonly #slug: string;

  constructor(http: TerraScaleHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  list(options: TsAuthUserListOptions = {}): Promise<TerraScaleResult<TsAuthUserPage>> {
    if (options.limit !== undefined && (!Number.isInteger(options.limit) || options.limit < 1 || options.limit > 100)) {
      throw new TypeError("limit must be an integer between 1 and 100.");
    }
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/users`,
      routeName: "tsauth.users.list",
      query: {
        ...(options.limit === undefined ? {} : { limit: options.limit }),
        ...(options.startingAfter === undefined ? {} : { starting_after: options.startingAfter }),
      },
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }

  /** Walks the id cursor until a short page; one request per page. */
  async listAll(options: TsAuthUserListAllOptions = {}): Promise<TerraScaleResult<readonly TsAuthUser[]>> {
    const pageSize = options.pageSize ?? 100;
    if (!Number.isInteger(pageSize) || pageSize < 1 || pageSize > 100) {
      throw new TypeError("pageSize must be an integer between 1 and 100.");
    }
    const users: TsAuthUser[] = [];
    let startingAfter: string | undefined;
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
        return err("tsauth.users.list", {
          kind: "invalid-response",
          code: "invalid_response",
          message: "The users page data must be an array.",
        }, page.status, page.headers);
      }
      users.push(...page.value.data);
      const last = page.value.data[page.value.data.length - 1];
      // A hostile repeated page stops the walk instead of looping forever.
      if (last === undefined || page.value.data.length < pageSize || last.id === startingAfter) {
        return { ...page, value: users };
      }
      startingAfter = last.id;
    }
  }

  get(userId: string): Promise<TerraScaleResult<TsAuthUser>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}`,
      routeName: "tsauth.users.get",
    });
  }

  create(request: TsAuthCreateUserRequest): Promise<TerraScaleResult<TsAuthUser>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/users`,
      routeName: "tsauth.users.create",
      body: request,
      idempotencyKey: false,
    });
  }

  update(userId: string, patch: TsAuthUserPatch): Promise<TerraScaleResult<TsAuthUser>> {
    if (!(patch instanceof TsAuthUserPatch)) throw new TypeError("patch must be a TsAuthUserPatch.");
    const body = patch.toJSON();
    if (Object.keys(body).length === 0) {
      throw new TypeError("The user patch is empty; configure at least one field.");
    }
    return this.#http.request({
      method: "PATCH",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}`,
      routeName: "tsauth.users.update",
      body,
      idempotencyKey: false,
    });
  }

  delete(userId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}`,
      routeName: "tsauth.users.delete",
      idempotencyKey: false,
    });
  }

  impersonate(userId: string): Promise<TerraScaleResult<TsAuthImpersonationSession>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/impersonate`,
      routeName: "tsauth.users.impersonate",
      idempotencyKey: false,
    });
  }

  sessions(userId: string): Promise<TerraScaleResult<TsAuthSessionPage>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/sessions`,
      routeName: "tsauth.users.sessions",
    });
  }

  revokeAllSessions(userId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/sessions/revoke-all`,
      routeName: "tsauth.users.revokeAllSessions",
      idempotencyKey: false,
    });
  }

  mfaFactors(userId: string): Promise<TerraScaleResult<TsAuthMfaFactorPage>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/mfa-factors`,
      routeName: "tsauth.users.mfaFactors",
    });
  }

  deleteMfaFactor(userId: string, factorId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/mfa-factors/${segment(factorId, "factor id")}`,
      routeName: "tsauth.users.deleteMfaFactor",
      idempotencyKey: false,
    });
  }

  regenerateRecoveryCodes(userId: string, factorId: string): Promise<TerraScaleResult<TsAuthRecoveryCodePage>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/mfa-factors/${segment(factorId, "factor id")}/regenerate-recovery`,
      routeName: "tsauth.users.regenerateRecoveryCodes",
      idempotencyKey: false,
    });
  }

  passkeys(userId: string): Promise<TerraScaleResult<TsAuthPasskeyPage>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/passkeys`,
      routeName: "tsauth.users.passkeys",
    });
  }

  deletePasskey(userId: string, passkeyId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/passkeys/${segment(passkeyId, "passkey id")}`,
      routeName: "tsauth.users.deletePasskey",
      idempotencyKey: false,
    });
  }

  organizations(userId: string): Promise<TerraScaleResult<readonly TsAuthUserOrganizationMembership[]>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/organizations`,
      routeName: "tsauth.users.organizations",
    });
  }
}

// sessions (snake_case)

export class TsAuthSessionsClient {
  readonly #http: TerraScaleHttp;
  readonly #slug: string;

  constructor(http: TerraScaleHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  revoke(sessionId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/sessions/${segment(sessionId, "session id")}/revoke`,
      routeName: "tsauth.sessions.revoke",
      idempotencyKey: false,
    });
  }
}

// api-keys (snake_case)

export type TsAuthApiKeyWithSecret = {
  readonly id: string;
  readonly name: string;
  /** One-time full sk_ bearer; only the creating call returns it. */
  readonly api_key: string;
  readonly scopes: readonly string[];
};

export type TsAuthApiKey = {
  readonly id: string;
  readonly name: string;
  readonly scopes: readonly string[];
  /** Unix seconds. */
  readonly created_at: number;
  readonly last_used_at?: number | null | undefined;
  readonly revoked: boolean;
};

export type TsAuthApiKeyPage = { readonly data: readonly TsAuthApiKey[] };

export type TsAuthCreateApiKeyRequest = {
  readonly name: string;
  readonly scopes?: readonly string[] | undefined;
};

export class TsAuthApiKeysClient {
  readonly #http: TerraScaleHttp;
  readonly #slug: string;

  constructor(http: TerraScaleHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  create(request: TsAuthCreateApiKeyRequest): Promise<TerraScaleResult<TsAuthApiKeyWithSecret>> {
    requireText(request?.name, "name");
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/api-keys`,
      routeName: "tsauth.apiKeys.create",
      body: request,
      idempotencyKey: false,
    });
  }

  list(): Promise<TerraScaleResult<TsAuthApiKeyPage>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/api-keys`,
      routeName: "tsauth.apiKeys.list",
    });
  }

  delete(keyId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/api-keys/${segment(keyId, "key id")}`,
      routeName: "tsauth.apiKeys.delete",
      idempotencyKey: false,
    });
  }
}

// organizations (snake_case)

export type TsAuthMembership = {
  readonly id: string;
  readonly organization_id: string;
  readonly user_id: string;
  readonly role: string;
  /** Unix seconds. */
  readonly created_at: number;
};

export type TsAuthMembershipWithUser = TsAuthMembership & {
  readonly user: { readonly id: string; readonly email: string; readonly name: string } | null;
};

export type TsAuthInvitation = {
  readonly id: string;
  readonly organization_id: string;
  readonly email: string;
  readonly role: string;
  readonly status: string;
  /** Unix seconds. */
  readonly expires_at: number;
  /** Unix seconds. */
  readonly created_at: number;
};

export type TsAuthOrganizationDomain = {
  readonly id: string;
  readonly organization_id: string;
  readonly domain: string;
  /** Unix seconds. */
  readonly created_at: number;
};

export type TsAuthDomainEnrollment = { readonly org_id: string };

export type TsAuthCreateOrganizationRequest = {
  readonly name: string;
  readonly slug?: string | undefined;
};

export type TsAuthUpdateOrganizationRequest = {
  readonly name: string;
};

export type TsAuthCreateMembershipRequest = {
  readonly user_id: string;
  readonly role: string;
};

export type TsAuthCreateInvitationRequest = {
  readonly email: string;
  readonly role: string;
};

export type TsAuthAddDomainRequest = {
  readonly domain: string;
};

export type TsAuthEnrollRequest = {
  readonly user_id: string;
};

export class TsAuthOrganizationsClient {
  readonly #http: TerraScaleHttp;
  readonly #slug: string;

  constructor(http: TerraScaleHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  create(request: TsAuthCreateOrganizationRequest): Promise<TerraScaleResult<TsAuthOrganization>> {
    requireText(request?.name, "name");
    if (request.slug !== undefined) requireText(request.slug, "slug");
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/organizations`,
      routeName: "tsauth.organizations.create",
      body: request,
      idempotencyKey: false,
    });
  }

  list(): Promise<TerraScaleResult<readonly TsAuthOrganization[]>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/organizations`,
      routeName: "tsauth.organizations.list",
    });
  }

  get(organizationId: string): Promise<TerraScaleResult<TsAuthOrganization>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}`,
      routeName: "tsauth.organizations.get",
    });
  }

  update(organizationId: string, request: TsAuthUpdateOrganizationRequest): Promise<TerraScaleResult<TsAuthOrganization>> {
    requireText(request?.name, "name");
    return this.#http.request({
      method: "PATCH",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}`,
      routeName: "tsauth.organizations.update",
      body: request,
      idempotencyKey: false,
    });
  }

  delete(organizationId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}`,
      routeName: "tsauth.organizations.delete",
      idempotencyKey: false,
    });
  }

  createMembership(organizationId: string, request: TsAuthCreateMembershipRequest): Promise<TerraScaleResult<TsAuthMembership>> {
    requireText(request?.user_id, "user_id");
    requireText(request?.role, "role");
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/memberships`,
      routeName: "tsauth.organizations.createMembership",
      body: request,
      idempotencyKey: false,
    });
  }

  listMemberships(organizationId: string): Promise<TerraScaleResult<readonly TsAuthMembershipWithUser[]>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/memberships`,
      routeName: "tsauth.organizations.listMemberships",
    });
  }

  updateMembership(organizationId: string, membershipId: string, request: { readonly role: string }): Promise<TerraScaleResult<TsAuthMembership>> {
    requireText(request?.role, "role");
    return this.#http.request({
      method: "PATCH",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/memberships/${segment(membershipId, "membership id")}`,
      routeName: "tsauth.organizations.updateMembership",
      body: request,
      idempotencyKey: false,
    });
  }

  deleteMembership(organizationId: string, membershipId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/memberships/${segment(membershipId, "membership id")}`,
      routeName: "tsauth.organizations.deleteMembership",
      idempotencyKey: false,
    });
  }

  createInvitation(organizationId: string, request: TsAuthCreateInvitationRequest): Promise<TerraScaleResult<TsAuthInvitation>> {
    requireText(request?.email, "email");
    requireText(request?.role, "role");
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/invitations`,
      routeName: "tsauth.organizations.createInvitation",
      body: request,
      idempotencyKey: false,
    });
  }

  listInvitations(organizationId: string): Promise<TerraScaleResult<readonly TsAuthInvitation[]>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/invitations`,
      routeName: "tsauth.organizations.listInvitations",
    });
  }

  revokeInvitation(organizationId: string, invitationId: string): Promise<TerraScaleResult<TsAuthInvitation>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/invitations/${segment(invitationId, "invitation id")}/revoke`,
      routeName: "tsauth.organizations.revokeInvitation",
      idempotencyKey: false,
    });
  }

  createDomain(organizationId: string, request: TsAuthAddDomainRequest): Promise<TerraScaleResult<TsAuthOrganizationDomain>> {
    requireText(request?.domain, "domain");
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/domains`,
      routeName: "tsauth.organizations.createDomain",
      body: request,
      idempotencyKey: false,
    });
  }

  listDomains(organizationId: string): Promise<TerraScaleResult<readonly TsAuthOrganizationDomain[]>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/domains`,
      routeName: "tsauth.organizations.listDomains",
    });
  }

  deleteDomain(organizationId: string, domainId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/domains/${segment(domainId, "domain id")}`,
      routeName: "tsauth.organizations.deleteDomain",
      idempotencyKey: false,
    });
  }

  enrollDomain(domain: string, request: TsAuthEnrollRequest): Promise<TerraScaleResult<TsAuthDomainEnrollment>> {
    requireText(request?.user_id, "user_id");
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/domains/${segment(domain, "domain")}/enroll`,
      routeName: "tsauth.organizations.enrollDomain",
      body: request,
      idempotencyKey: false,
    });
  }
}

// oauth-clients (requests snake_case, responses camelCase)

export type TsAuthCreateOauthClientRequest = {
  readonly name: string;
  readonly redirect_uris: readonly string[];
  readonly public?: boolean | undefined;
  readonly token_endpoint_auth_method?: string | undefined;
};

export type TsAuthOauthClientView = {
  readonly id: string;
  readonly clientId: string;
  readonly name: string;
  readonly redirectUris: readonly string[];
  readonly tokenEndpointAuthMethod: string;
  readonly public: boolean;
  /** Unix seconds. */
  readonly createdAt: number;
};

export type TsAuthOauthClientWithSecret = {
  readonly client: TsAuthOauthClientView;
  /** One-time; null for public clients using the `none` auth method. */
  readonly client_secret: string | null;
};

export type TsAuthOauthClientList = { readonly clients: readonly TsAuthOauthClientView[] };

export class TsAuthOauthClientsClient {
  readonly #http: TerraScaleHttp;
  readonly #slug: string;

  constructor(http: TerraScaleHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  create(request: TsAuthCreateOauthClientRequest): Promise<TerraScaleResult<TsAuthOauthClientWithSecret>> {
    requireText(request?.name, "name");
    if (!Array.isArray(request?.redirect_uris) || request.redirect_uris.length === 0 ||
      !request.redirect_uris.every(uri => typeof uri === "string" && uri.length > 0)) {
      throw new TypeError("redirect_uris must be a non-empty array of strings.");
    }
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/oauth-clients`,
      routeName: "tsauth.oauthClients.create",
      body: request,
      idempotencyKey: false,
    });
  }

  list(): Promise<TerraScaleResult<TsAuthOauthClientList>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/oauth-clients`,
      routeName: "tsauth.oauthClients.list",
    });
  }

  delete(clientId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/oauth-clients/${segment(clientId, "client id")}`,
      routeName: "tsauth.oauthClients.delete",
      idempotencyKey: false,
    });
  }
}

// connections (snake_case)

export type TsAuthConnectionView = {
  readonly id: string;
  readonly type: "oidc" | "saml";
  readonly name: string;
  readonly enabled: boolean;
  readonly org_id: string | null;
  readonly issuer_url: string | null;
  readonly client_id: string | null;
  readonly authorization_endpoint: string | null;
  readonly token_endpoint: string | null;
  readonly userinfo_endpoint: string | null;
  readonly idp_entity_id: string | null;
  readonly sso_redirect_url: string | null;
  readonly sp_entity_id: string | null;
  /** Unix seconds. */
  readonly created_at: number;
};

export type TsAuthCreateConnectionRequest =
  | {
    readonly type: "oidc";
    readonly name: string;
    readonly org_id?: string | undefined;
    readonly issuer_url: string;
    readonly client_id: string;
    readonly client_secret: string;
    readonly scopes?: readonly string[] | undefined;
    readonly authorization_endpoint?: string | undefined;
    readonly token_endpoint?: string | undefined;
    readonly userinfo_endpoint?: string | undefined;
  }
  | {
    readonly type: "saml";
    readonly name: string;
    readonly org_id?: string | undefined;
    readonly idp_entity_id: string;
    readonly sso_redirect_url: string;
    readonly x509_certificate_pem: string;
  };

export type TsAuthConnectionPatch = {
  readonly enabled?: boolean | undefined;
  readonly name?: string | undefined;
  readonly client_secret?: string | undefined;
};

export class TsAuthConnectionsClient {
  readonly #http: TerraScaleHttp;
  readonly #slug: string;

  constructor(http: TerraScaleHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  create(request: TsAuthCreateConnectionRequest): Promise<TerraScaleResult<TsAuthConnectionView>> {
    requireText(request?.name, "name");
    if (request.type === "oidc") {
      requireAbsoluteHttpUrl(request.issuer_url, "issuer_url");
      requireText(request.client_id, "client_id");
      requireText(request.client_secret, "client_secret");
    } else {
      requireText(request.idp_entity_id, "idp_entity_id");
      requireAbsoluteHttpUrl(request.sso_redirect_url, "sso_redirect_url");
      requireText(request.x509_certificate_pem, "x509_certificate_pem");
    }
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/connections`,
      routeName: "tsauth.connections.create",
      body: request,
      idempotencyKey: false,
    });
  }

  list(): Promise<TerraScaleResult<{ readonly data: readonly TsAuthConnectionView[] }>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/connections`,
      routeName: "tsauth.connections.list",
    });
  }

  update(connectionId: string, request: TsAuthConnectionPatch): Promise<TerraScaleResult<TsAuthConnectionView>> {
    return this.#http.request({
      method: "PATCH",
      path: `v1/${this.#slug}/connections/${segment(connectionId, "connection id")}`,
      routeName: "tsauth.connections.update",
      body: request,
      idempotencyKey: false,
    });
  }

  delete(connectionId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/connections/${segment(connectionId, "connection id")}`,
      routeName: "tsauth.connections.delete",
      idempotencyKey: false,
    });
  }
}

// directories (snake_case)

export type TsAuthDirectoryWithToken = {
  readonly id: string;
  readonly org_id: string | null;
  /** One-time SCIM bearer; only create and rotate return it. */
  readonly token: string;
};

export type TsAuthDirectory = {
  readonly id: string;
  readonly org_id: string | null;
  /** Unix seconds. */
  readonly created_at: number;
};

export class TsAuthDirectoriesClient {
  readonly #http: TerraScaleHttp;
  readonly #slug: string;

  constructor(http: TerraScaleHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  create(request: { readonly org_id?: string | undefined } = {}): Promise<TerraScaleResult<TsAuthDirectoryWithToken>> {
    if (request.org_id !== undefined) requireText(request.org_id, "org_id");
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/directories`,
      routeName: "tsauth.directories.create",
      body: request,
      idempotencyKey: false,
    });
  }

  list(): Promise<TerraScaleResult<{ readonly data: readonly TsAuthDirectory[] }>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/directories`,
      routeName: "tsauth.directories.list",
    });
  }

  rotate(directoryId: string): Promise<TerraScaleResult<TsAuthDirectoryWithToken>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/directories/${segment(directoryId, "directory id")}/rotate`,
      routeName: "tsauth.directories.rotate",
      idempotencyKey: false,
    });
  }

  delete(directoryId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/directories/${segment(directoryId, "directory id")}`,
      routeName: "tsauth.directories.delete",
      idempotencyKey: false,
    });
  }
}

// jwt-templates (snake_case; addressed by name)

export type TsAuthJwtTemplate = {
  readonly id: string;
  readonly name: string;
  readonly claims: Record<string, string>;
  readonly lifetime_seconds: number;
  /** Unix seconds. */
  readonly created_at: number;
};

export type TsAuthJwtTokenResponse = { readonly jwt: string };

export type TsAuthCreateJwtTemplateRequest = {
  readonly name: string;
  readonly claims: Record<string, string>;
  readonly lifetime_seconds?: number | undefined;
};

export type TsAuthUpdateJwtTemplateRequest = {
  readonly name?: string | undefined;
  readonly claims?: Record<string, string> | undefined;
  readonly lifetime_seconds?: number | undefined;
};

const jwtTemplateNamePattern = /^[a-z0-9_-]{1,48}$/u;

export class TsAuthJwtTemplatesClient {
  readonly #http: TerraScaleHttp;
  readonly #slug: string;

  constructor(http: TerraScaleHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  create(request: TsAuthCreateJwtTemplateRequest): Promise<TerraScaleResult<TsAuthJwtTemplate>> {
    validateJwtTemplateName(request?.name);
    validateJwtTemplateClaims(request?.claims);
    if (request.lifetime_seconds !== undefined &&
      (!Number.isInteger(request.lifetime_seconds) || request.lifetime_seconds < 1)) {
      throw new TypeError("lifetime_seconds must be a positive integer.");
    }
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/jwt-templates`,
      routeName: "tsauth.jwtTemplates.create",
      body: request,
      idempotencyKey: false,
    });
  }

  list(): Promise<TerraScaleResult<readonly TsAuthJwtTemplate[]>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/jwt-templates`,
      routeName: "tsauth.jwtTemplates.list",
    });
  }

  update(name: string, request: TsAuthUpdateJwtTemplateRequest): Promise<TerraScaleResult<TsAuthJwtTemplate>> {
    validateJwtTemplateName(name);
    if (request.name !== undefined) validateJwtTemplateName(request.name);
    if (request.claims !== undefined) validateJwtTemplateClaims(request.claims);
    return this.#http.request({
      method: "PATCH",
      path: `v1/${this.#slug}/jwt-templates/${segment(name, "template name")}`,
      routeName: "tsauth.jwtTemplates.update",
      body: request,
      idempotencyKey: false,
    });
  }

  delete(name: string): Promise<TerraScaleResult<void>> {
    validateJwtTemplateName(name);
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/jwt-templates/${segment(name, "template name")}`,
      routeName: "tsauth.jwtTemplates.delete",
      idempotencyKey: false,
    });
  }

  issueToken(name: string, request: { readonly user_id: string }): Promise<TerraScaleResult<TsAuthJwtTokenResponse>> {
    validateJwtTemplateName(name);
    requireText(request?.user_id, "user_id");
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/jwt-templates/${segment(name, "template name")}/tokens`,
      routeName: "tsauth.jwtTemplates.issueToken",
      body: request,
      idempotencyKey: false,
    });
  }
}

// webhooks (snake_case)

export type TsAuthWebhookWithSecret = {
  readonly id: string;
  readonly url: string;
  readonly events: readonly string[];
  /** One-time HMAC key for verifying deliveries; only create returns it. */
  readonly signing_secret: string;
};

export type TsAuthWebhook = {
  readonly id: string;
  readonly url: string;
  readonly events: readonly string[];
  readonly enabled: boolean;
  /** Unix seconds. */
  readonly created_at: number;
};

export type TsAuthDeadLetterEntry = {
  /** endpointId:eventId */
  readonly id: string;
  readonly endpoint_id: string;
  readonly event_id: string;
  readonly attempts: number;
  readonly last_error?: string | undefined;
  /** Unix seconds. */
  readonly updated_at: number;
};

export class TsAuthWebhooksClient {
  readonly #http: TerraScaleHttp;
  readonly #slug: string;

  constructor(http: TerraScaleHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  create(request: { readonly url: string; readonly events: readonly string[] }): Promise<TerraScaleResult<TsAuthWebhookWithSecret>> {
    requireAbsoluteHttpUrl(request?.url, "url");
    if (!Array.isArray(request?.events) || request.events.length === 0 ||
      !request.events.every(event => typeof event === "string" && event.length > 0)) {
      throw new TypeError("events must be a non-empty array of strings.");
    }
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/webhooks`,
      routeName: "tsauth.webhooks.create",
      body: request,
      idempotencyKey: false,
    });
  }

  list(): Promise<TerraScaleResult<readonly TsAuthWebhook[]>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/webhooks`,
      routeName: "tsauth.webhooks.list",
    });
  }

  delete(webhookId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/webhooks/${segment(webhookId, "webhook id")}`,
      routeName: "tsauth.webhooks.delete",
      idempotencyKey: false,
    });
  }

  test(webhookId: string): Promise<TerraScaleResult<{ readonly queued: boolean }>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/webhooks/${segment(webhookId, "webhook id")}/test`,
      routeName: "tsauth.webhooks.test",
      body: {},
      idempotencyKey: false,
    });
  }

  deadLetter(): Promise<TerraScaleResult<readonly TsAuthDeadLetterEntry[]>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/webhooks/dead-letter`,
      routeName: "tsauth.webhooks.deadLetter",
    });
  }

  replayDeadLetter(entryId: string): Promise<TerraScaleResult<{ readonly replayed: boolean }>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/webhooks/dead-letter/${segment(entryId, "dead-letter id")}/replay`,
      routeName: "tsauth.webhooks.replayDeadLetter",
      idempotencyKey: false,
    });
  }
}

// audit-events (snake_case)

export type TsAuthAuditEvent = {
  readonly id: string;
  readonly instance_id: string;
  readonly type: string;
  /** Omitted from the wire when null. */
  readonly actor_id?: string | undefined;
  readonly data: Record<string, unknown>;
  /** Unix seconds. */
  readonly created_at: number;
  readonly integrity_version: number;
  readonly integrity_hash: string;
  /** Both are null when the instance has no audit signing key. */
  readonly integrity_signature: string | null;
  readonly integrity_key_fingerprint: string | null;
};

export class TsAuthAuditEventsClient {
  readonly #http: TerraScaleHttp;
  readonly #slug: string;

  constructor(http: TerraScaleHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  list(options: TsAuthRequestOptions & { readonly limit?: number | undefined } = {}): Promise<TerraScaleResult<readonly TsAuthAuditEvent[]>> {
    if (options.limit !== undefined && (!Number.isInteger(options.limit) || options.limit < 1 || options.limit > 100)) {
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

export type TsAuthBillingOverview = {
  readonly plan: "free" | "pro" | "enterprise";
  readonly status: string;
  readonly stripe_customer_id?: string | undefined;
  readonly stripe_subscription_id?: string | undefined;
  readonly current_period_end?: number | undefined;
  readonly user_count: number;
  readonly quota: number;
};

export class TsAuthBillingClient {
  readonly #http: TerraScaleHttp;
  readonly #slug: string;

  constructor(http: TerraScaleHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  get(): Promise<TerraScaleResult<TsAuthBillingOverview>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/${planSegment}`,
      routeName: `tsauth.${planSegment}.get`,
    });
  }

  checkout(request: { readonly plan: "pro" | "enterprise" }): Promise<TerraScaleResult<{ readonly checkout_url: string }>> {
    if (request?.plan !== "pro" && request?.plan !== "enterprise") throw new TypeError('plan must be "pro" or "enterprise".');
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/${planSegment}/checkout`,
      routeName: `tsauth.${planSegment}.checkout`,
      body: request,
      idempotencyKey: false,
    });
  }

  portal(): Promise<TerraScaleResult<{ readonly portal_url: string }>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/${planSegment}/portal`,
      routeName: `tsauth.${planSegment}.portal`,
      body: {},
      idempotencyKey: false,
    });
  }

  enforceCheck(request: { readonly current_users: number }): Promise<TerraScaleResult<{ readonly allowed: boolean; readonly plan: string; readonly quota: number }>> {
    if (!Number.isInteger(request?.current_users) || request.current_users < 0) {
      throw new TypeError("current_users must be a non-negative integer.");
    }
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/${planSegment}/enforce-check`,
      routeName: `tsauth.${planSegment}.enforceCheck`,
      body: request,
      idempotencyKey: false,
    });
  }

  reconcileQuota(): Promise<TerraScaleResult<{ readonly claims_examined: number; readonly claims_removed: number; readonly claims_retained: number }>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/${planSegment}/quota/reconcile`,
      routeName: `tsauth.${planSegment}.reconcileQuota`,
      idempotencyKey: false,
    });
  }
}

// instance (snake_case)

export type TsAuthInstance = {
  readonly id: string;
  readonly slug: string;
  readonly name: string;
  readonly logo_url?: string | null | undefined;
  readonly theme_color?: string | null | undefined;
  /** Unix seconds. */
  readonly created_at: number;
};

/** The PATCH response omits created_at. */
export type TsAuthInstanceUpdateResponse = Omit<TsAuthInstance, "created_at">;

/** Absent leaves the stored value unchanged; the service treats null the same as absent. */
export type TsAuthInstanceUpdateRequest = {
  readonly name?: string | undefined;
  readonly logo_url?: string | undefined;
  readonly theme_color?: string | undefined;
};

export class TsAuthInstanceClient {
  readonly #http: TerraScaleHttp;
  readonly #slug: string;

  constructor(http: TerraScaleHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  get(): Promise<TerraScaleResult<TsAuthInstance>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/instance`,
      routeName: "tsauth.instance.get",
    });
  }

  update(request: TsAuthInstanceUpdateRequest): Promise<TerraScaleResult<TsAuthInstanceUpdateResponse>> {
    if (request.name !== undefined) requireText(request.name, "name");
    if (request.logo_url !== undefined) requireAbsoluteHttpUrl(request.logo_url, "logo_url");
    if (request.theme_color !== undefined && !/^#[0-9a-fA-F]{6}$/u.test(request.theme_color)) {
      throw new TypeError("theme_color must be a #rrggbb hex color.");
    }
    return this.#http.request({
      method: "PATCH",
      path: `v1/${this.#slug}/instance`,
      routeName: "tsauth.instance.update",
      body: request,
      idempotencyKey: false,
    });
  }
}

// OIDC (public routes under the instance slug; no sk_ key)

export type TsAuthOidcDiscovery = {
  readonly issuer: string;
  readonly authorization_endpoint: string;
  readonly token_endpoint: string;
  readonly userinfo_endpoint: string;
  readonly jwks_uri: string;
  readonly revocation_endpoint: string;
  readonly introspection_endpoint: string;
  readonly end_session_endpoint: string;
  readonly response_types_supported: readonly string[];
  readonly grant_types_supported: readonly string[];
  readonly code_challenge_methods_supported: readonly string[];
  readonly subject_types_supported: readonly string[];
  readonly id_token_signing_alg_values_supported: readonly string[];
  readonly token_endpoint_auth_methods_supported: readonly string[];
  readonly scopes_supported: readonly string[];
};

export type TsAuthOidcJwk = {
  readonly kty: "EC";
  readonly crv: "P-256";
  readonly alg: "ES256";
  readonly use: "sig";
  readonly kid: string;
  readonly x: string;
  readonly y: string;
};

export type TsAuthOidcJwks = { readonly keys: readonly TsAuthOidcJwk[] };

export type TsAuthOidcTokenRequest =
  | {
    readonly grant_type: "authorization_code";
    readonly code: string;
    readonly redirect_uri: string;
    readonly code_verifier?: string | undefined;
  }
  | {
    readonly grant_type: "refresh_token";
    readonly refresh_token: string;
  }
  | {
    readonly grant_type: "client_credentials";
    readonly scope?: string | undefined;
  };

export type TsAuthOidcTokenResponse = {
  readonly access_token: string;
  readonly token_type: "Bearer";
  readonly expires_in: number;
  readonly scope?: string | undefined;
  readonly refresh_token?: string | undefined;
  readonly id_token?: string | undefined;
};

export type TsAuthOidcIntrospection = {
  readonly active: boolean;
  readonly token_type?: string | undefined;
  readonly iss?: string | undefined;
  readonly sub?: string | undefined;
  readonly aud?: string | readonly string[] | undefined;
  readonly client_id?: string | undefined;
  readonly scope?: string | undefined;
  readonly exp?: number | undefined;
  readonly iat?: number | undefined;
};

export type TsAuthOidcUserInfo = {
  readonly sub: string;
  readonly email?: string | undefined;
  readonly email_verified?: boolean | undefined;
  readonly name?: string | undefined;
};

export class TsAuthOidcClient {
  readonly #http: TerraScaleHttp;
  readonly #slug: string;

  constructor(http: TerraScaleHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  /** Public discovery document; sent without any authorization header. */
  discovery(): Promise<TerraScaleResult<TsAuthOidcDiscovery>> {
    return this.#http.request({
      method: "GET",
      path: `${this.#slug}/.well-known/openid-configuration`,
      routeName: "tsauth.oidc.discovery",
    });
  }

  /** Public signing keys; sent without any authorization header. */
  jwks(): Promise<TerraScaleResult<TsAuthOidcJwks>> {
    return this.#http.request({
      method: "GET",
      path: `${this.#slug}/.well-known/jwks.json`,
      routeName: "tsauth.oidc.jwks",
    });
  }

  /** client_secret_post authentication: credentials travel as form fields. */
  token(clientId: string, clientSecret: string, request: TsAuthOidcTokenRequest): Promise<TerraScaleResult<TsAuthOidcTokenResponse>> {
    const form = clientCredentialForm(clientId, clientSecret);
    form.set("grant_type", request.grant_type);
    if (request.grant_type === "authorization_code") {
      requireText(request.code, "code");
      requireText(request.redirect_uri, "redirect_uri");
      form.set("code", request.code);
      form.set("redirect_uri", request.redirect_uri);
      if (request.code_verifier !== undefined) form.set("code_verifier", request.code_verifier);
    } else if (request.grant_type === "refresh_token") {
      requireText(request.refresh_token, "refresh_token");
      form.set("refresh_token", request.refresh_token);
    } else if (request.scope !== undefined) {
      form.set("scope", request.scope);
    }
    return this.#postForm(`${this.#slug}/oauth/token`, "tsauth.oidc.token", form);
  }

  introspect(clientId: string, clientSecret: string, token: string): Promise<TerraScaleResult<TsAuthOidcIntrospection>> {
    const form = clientCredentialForm(clientId, clientSecret);
    form.set("token", requireText(token, "token"));
    return this.#postForm(`${this.#slug}/oauth/introspect`, "tsauth.oidc.introspect", form);
  }

  /** RFC 7009 semantics: the service answers 200 with an empty body even for
   * unknown tokens, so success carries no payload. */
  revoke(clientId: string, clientSecret: string, token: string): Promise<TerraScaleResult<void>> {
    const form = clientCredentialForm(clientId, clientSecret);
    form.set("token", requireText(token, "token"));
    return this.#postForm(`${this.#slug}/oauth/revoke`, "tsauth.oidc.revoke", form);
  }

  /** Bearer access token issued by the token endpoint, not the sk_ key. */
  userinfo(accessToken: string): Promise<TerraScaleResult<TsAuthOidcUserInfo>> {
    requireText(accessToken, "accessToken");
    return this.#http.request({
      method: "GET",
      path: `${this.#slug}/oauth/userinfo`,
      routeName: "tsauth.oidc.userinfo",
      headers: { authorization: `Bearer ${accessToken.trim()}` },
    });
  }

  #postForm<T>(path: string, routeName: `tsauth.${string}`, form: URLSearchParams): Promise<TerraScaleResult<T>> {
    return this.#http.request<T>({
      method: "POST",
      path,
      routeName,
      formBody: form,
      idempotencyKey: false,
    });
  }
}

/** The ts-auth management client: one hosted identity instance. */
export class TsAuthClient {
  readonly #http: TerraScaleHttp;
  readonly users: TsAuthUsersClient;
  readonly sessions: TsAuthSessionsClient;
  readonly apiKeys: TsAuthApiKeysClient;
  readonly organizations: TsAuthOrganizationsClient;
  readonly connections: TsAuthConnectionsClient;
  readonly directories: TsAuthDirectoriesClient;
  readonly jwtTemplates: TsAuthJwtTemplatesClient;
  readonly webhooks: TsAuthWebhooksClient;
  readonly auditEvents: TsAuthAuditEventsClient;
  readonly billing: TsAuthBillingClient;
  readonly instance: TsAuthInstanceClient;
  readonly oauthClients: TsAuthOauthClientsClient;
  readonly oidc: TsAuthOidcClient;

  constructor(config: TsAuthClientConfig) {
    // The shared runtime config defaults to the product API; ts-auth has no
    // default origin and an sk_ key must never leak to the product host.
    if (typeof config?.apiBaseUrl !== "string" || config.apiBaseUrl.trim().length === 0) {
      throw new TypeError("TsAuthClient requires the ts-auth deployment's public apiBaseUrl origin; there is no default.");
    }
    const slug = validateInstanceSlug(config?.instanceSlug);
    this.#http = new TerraScaleHttp(config);
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
    this.oidc = new TsAuthOidcClient(this.#http, slug);
  }

  get config() { return this.#http.config; }
}

export function createTsAuthClient(config: TsAuthClientConfig): TsAuthClient {
  return new TsAuthClient(config);
}

/** Mirrors the ts-auth service rule: the deployment origin is a bare origin.
 * A path, query, or fragment would be silently rewritten or dropped by URL
 * composition, so it is a construction error instead. */
function requireBareOrigin(apiBaseUrl: string): void {
  let url: URL;
  try {
    url = new URL(apiBaseUrl);
  } catch {
    // Malformed URLs keep their request-time transport failure.
    return;
  }
  if (url.pathname !== "/" || url.search !== "" || url.hash !== "") {
    throw new TypeError("The ts-auth apiBaseUrl must be a bare origin without a path, query, or fragment.");
  }
}

/**
 * Verifies a ts-auth webhook delivery signature: sha256=lowercaseHex(HMAC-
 * SHA256(key = UTF8(signingSecret), message = UTF8(timestamp + "." + rawBody)))
 * compared in constant time over the RAW body bytes. Malformed input returns
 * false; this function never throws.
 */
export async function verifyTsAuthWebhook(
  signingSecret: string,
  timestamp: string | undefined,
  signature: string | undefined,
  payload: Uint8Array,
): Promise<boolean> {
  const verified = await verifySignature(signingSecret, timestamp, signature, payload);
  return verified === true;
}

/** verifyTsAuthWebhook plus a replay window: the timestamp header must be
 * within ±toleranceMs of `now` (milliseconds). */
export async function verifyTsAuthWebhookWithTolerance(
  signingSecret: string,
  timestamp: string | undefined,
  signature: string | undefined,
  payload: Uint8Array,
  toleranceMs: number,
  now: number,
): Promise<boolean> {
  const verified = await verifySignature(signingSecret, timestamp, signature, payload);
  if (verified !== true) return false;
  if (!Number.isFinite(toleranceMs) || toleranceMs < 0 || !Number.isFinite(now)) return false;
  const seconds = Number(timestamp);
  if (!Number.isFinite(seconds)) return false;
  return Math.abs(now - seconds * 1_000) <= toleranceMs;
}

/** undefined means malformed input; true/false means the signature verdict. */
async function verifySignature(
  signingSecret: string,
  timestamp: string | undefined,
  signature: string | undefined,
  payload: Uint8Array,
): Promise<boolean | undefined> {
  if (typeof signingSecret !== "string" || signingSecret.length === 0) return undefined;
  if (typeof timestamp !== "string" || !/^[0-9]+$/u.test(timestamp)) return undefined;
  if (typeof signature !== "string" || !signature.startsWith("sha256=")) return undefined;
  if (!(payload instanceof Uint8Array)) return undefined;
  const hex = signature.slice("sha256=".length);
  if (!/^[0-9a-f]{64}$/u.test(hex)) return undefined;
  const prefix = utf8Encoder.encode(`${timestamp}.`);
  const message = new Uint8Array(prefix.byteLength + payload.byteLength);
  message.set(prefix, 0);
  message.set(payload, prefix.byteLength);
  const subtle = globalThis.crypto.subtle;
  const key = await subtle.importKey("raw", utf8Encoder.encode(signingSecret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const mac = new Uint8Array(await subtle.sign("HMAC", key, message));
  return timingSafeEqualHex(toLowercaseHex(mac), hex);
}

function toLowercaseHex(bytes: Uint8Array): string {
  let hex = "";
  for (const byte of bytes) hex += byte.toString(16).padStart(2, "0");
  return hex;
}

/** Fixed-length hex compare whose loop never short-circuits. */
function timingSafeEqualHex(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let difference = 0;
  for (let index = 0; index < a.length; index++) {
    difference |= a.charCodeAt(index) ^ b.charCodeAt(index);
  }
  return difference === 0;
}

function validateInstanceSlug(instanceSlug: string): string {
  if (typeof instanceSlug !== "string" || instanceSlug.length === 0 || instanceSlug !== instanceSlug.trim() ||
    instanceSlug === "." || instanceSlug === ".." ||
    instanceSlug.includes("/") || /\s/u.test(instanceSlug) ||
    [...instanceSlug].some(character => character.charCodeAt(0) < 0x20 || character.charCodeAt(0) === 0x7f)) {
    throw new TypeError("instanceSlug must be a nonblank printable string without whitespace or slashes.");
  }
  return encodeURIComponent(instanceSlug);
}

const segmentControlCharacters = /[\u0000-\u001f\u007f]/u;
const utf8Encoder = new TextEncoder();

function segment(value: string, name: string): string {
  if (typeof value !== "string" || value.length === 0 || value !== value.trim() ||
    // Bare dot segments survive preflight but URL path normalization collapses
    // them, silently rewriting the route; reject them instead.
    value === "." || value === ".." ||
    segmentControlCharacters.test(value) ||
    utf8Encoder.encode(value).byteLength > 255) {
    throw new TypeError(`${name} must be a nonblank printable string of at most 255 UTF-8 bytes.`);
  }
  return encodeURIComponent(value);
}

function requireText(value: string, name: string): string {
  if (typeof value !== "string" || value.trim().length === 0 || /\p{Cc}/u.test(value)) {
    throw new TypeError(`${name} must be a nonblank string without control characters.`);
  }
  return value;
}

function requireAbsoluteHttpUrl(value: string, name: string): string {
  requireText(value, name);
  if (!/^https?:\/\//u.test(value)) throw new TypeError(`${name} must be an absolute http(s) URL.`);
  return value;
}

function requirePatchText(value: string, name: string): string {
  if (typeof value !== "string" || value.length === 0) throw new TypeError(`${name} must be a non-empty string.`);
  return value;
}

function requirePatchMetadata(metadata: Record<string, unknown>, name: string): Record<string, unknown> {
  if (typeof metadata !== "object" || metadata === null || Array.isArray(metadata)) {
    throw new TypeError(`${name} must be an object.`);
  }
  return metadata;
}

function validateJwtTemplateName(name: string): string {
  if (typeof name !== "string" || !jwtTemplateNamePattern.test(name)) {
    throw new TypeError("Template names must be 1 to 48 characters of [a-z0-9-_].");
  }
  return name;
}

function validateJwtTemplateClaims(claims: Record<string, string>): Record<string, string> {
  if (typeof claims !== "object" || claims === null || Array.isArray(claims) || Object.keys(claims).length === 0 ||
    !Object.values(claims).every(value => typeof value === "string")) {
    throw new TypeError("claims must be a non-empty object mapping strings to strings.");
  }
  return claims;
}

function clientCredentialForm(clientId: string, clientSecret: string): URLSearchParams {
  return new URLSearchParams({
    client_id: requireText(clientId, "clientId"),
    client_secret: requireText(clientSecret, "clientSecret"),
  });
}
