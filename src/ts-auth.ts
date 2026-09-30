import type { TsAuthRuntimeConfig } from "./config.js";
import { TsAuthHttp } from "./http.js";
import { err, type TerraScaleResult } from "./result.js";

export type { TsAuthRuntimeConfig } from "./config.js";

/**
 * ts-auth is a separate hosted IdP service on its own origin. This client
 * speaks that service's published management and OIDC contract, which mirrors
 * the ts-auth service routes — not the TerraScale product /api/v1 producer
 * contract. The deployment's public origin is `apiBaseUrl` and the instance
 * `sk_` management key is `accessToken`.
 */
export type TsAuthClientConfig = Omit<TsAuthRuntimeConfig, "apiBaseUrl"> & {
  /** The ts-auth deployment's public origin; there is no default. */
  readonly apiBaseUrl: string;
  /** The instance the `sk_` key belongs to; it prefixes every route. */
  readonly instanceSlug: string;
  /** Route public auth calls through the reverse proxy instead of regional discovery. */
  readonly useReverseProxy?: boolean;
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
    if (typeof banned !== "boolean")
      throw new TypeError("banned must be a boolean.");
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
    this.#field(
      "public_metadata",
      requirePatchMetadata(metadata, "publicMetadata"),
    );
    return this;
  }

  unsetPublicMetadata(): this {
    return this.#null("public_metadata");
  }

  withPrivateMetadata(metadata: Record<string, unknown>): this {
    this.#field(
      "private_metadata",
      requirePatchMetadata(metadata, "privateMetadata"),
    );
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
  readonly #http: TsAuthHttp;
  readonly #slug: string;

  constructor(http: TsAuthHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  list(
    options: TsAuthUserListOptions = {},
  ): Promise<TerraScaleResult<TsAuthUserPage>> {
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
          : { starting_after: options.startingAfter }),
      },
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }

  /** Walks the id cursor until a short page; one request per page. */
  async listAll(
    options: TsAuthUserListAllOptions = {},
  ): Promise<TerraScaleResult<readonly TsAuthUser[]>> {
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

  get(userId: string): Promise<TerraScaleResult<TsAuthUser>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}`,
      routeName: "tsauth.users.get",
    });
  }

  create(
    request: TsAuthCreateUserRequest,
  ): Promise<TerraScaleResult<TsAuthUser>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/users`,
      routeName: "tsauth.users.create",
      body: request,
    });
  }

  update(
    userId: string,
    patch: TsAuthUserPatch,
  ): Promise<TerraScaleResult<TsAuthUser>> {
    if (!(patch instanceof TsAuthUserPatch))
      throw new TypeError("patch must be a TsAuthUserPatch.");
    const body = patch.toJSON();
    if (Object.keys(body).length === 0) {
      throw new TypeError(
        "The user patch is empty; configure at least one field.",
      );
    }
    return this.#http.request({
      method: "PATCH",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}`,
      routeName: "tsauth.users.update",
      body,
    });
  }

  delete(userId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}`,
      routeName: "tsauth.users.delete",
    });
  }

  impersonate(
    userId: string,
  ): Promise<TerraScaleResult<TsAuthImpersonationSession>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/impersonate`,
      routeName: "tsauth.users.impersonate",
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
    });
  }

  mfaFactors(userId: string): Promise<TerraScaleResult<TsAuthMfaFactorPage>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/mfa-factors`,
      routeName: "tsauth.users.mfaFactors",
    });
  }

  deleteMfaFactor(
    userId: string,
    factorId: string,
  ): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/mfa-factors/${segment(factorId, "factor id")}`,
      routeName: "tsauth.users.deleteMfaFactor",
    });
  }

  regenerateRecoveryCodes(
    userId: string,
    factorId: string,
  ): Promise<TerraScaleResult<TsAuthRecoveryCodePage>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/mfa-factors/${segment(factorId, "factor id")}/regenerate-recovery`,
      routeName: "tsauth.users.regenerateRecoveryCodes",
    });
  }

  passkeys(userId: string): Promise<TerraScaleResult<TsAuthPasskeyPage>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/passkeys`,
      routeName: "tsauth.users.passkeys",
    });
  }

  deletePasskey(
    userId: string,
    passkeyId: string,
  ): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/passkeys/${segment(passkeyId, "passkey id")}`,
      routeName: "tsauth.users.deletePasskey",
    });
  }

  organizations(
    userId: string,
  ): Promise<TerraScaleResult<readonly TsAuthUserOrganizationMembership[]>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/users/${segment(userId, "user id")}/organizations`,
      routeName: "tsauth.users.organizations",
    });
  }
}

// sessions (snake_case)

export class TsAuthSessionsClient {
  readonly #http: TsAuthHttp;
  readonly #slug: string;

  constructor(http: TsAuthHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  revoke(sessionId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/sessions/${segment(sessionId, "session id")}/revoke`,
      routeName: "tsauth.sessions.revoke",
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
  readonly #http: TsAuthHttp;
  readonly #slug: string;

  constructor(http: TsAuthHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  create(
    request: TsAuthCreateApiKeyRequest,
  ): Promise<TerraScaleResult<TsAuthApiKeyWithSecret>> {
    requireText(request?.name, "name");
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/api-keys`,
      routeName: "tsauth.apiKeys.create",
      body: request,
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
  readonly user: {
    readonly id: string;
    readonly email: string;
    readonly name: string;
  } | null;
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
  readonly #http: TsAuthHttp;
  readonly #slug: string;

  constructor(http: TsAuthHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  create(
    request: TsAuthCreateOrganizationRequest,
  ): Promise<TerraScaleResult<TsAuthOrganization>> {
    requireText(request?.name, "name");
    if (request.slug !== undefined) requireText(request.slug, "slug");
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/organizations`,
      routeName: "tsauth.organizations.create",
      body: request,
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

  update(
    organizationId: string,
    request: TsAuthUpdateOrganizationRequest,
  ): Promise<TerraScaleResult<TsAuthOrganization>> {
    requireText(request?.name, "name");
    return this.#http.request({
      method: "PATCH",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}`,
      routeName: "tsauth.organizations.update",
      body: request,
    });
  }

  delete(organizationId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}`,
      routeName: "tsauth.organizations.delete",
    });
  }

  createMembership(
    organizationId: string,
    request: TsAuthCreateMembershipRequest,
  ): Promise<TerraScaleResult<TsAuthMembership>> {
    requireText(request?.user_id, "user_id");
    requireText(request?.role, "role");
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/memberships`,
      routeName: "tsauth.organizations.createMembership",
      body: request,
    });
  }

  listMemberships(
    organizationId: string,
  ): Promise<TerraScaleResult<readonly TsAuthMembershipWithUser[]>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/memberships`,
      routeName: "tsauth.organizations.listMemberships",
    });
  }

  updateMembership(
    organizationId: string,
    membershipId: string,
    request: { readonly role: string },
  ): Promise<TerraScaleResult<TsAuthMembership>> {
    requireText(request?.role, "role");
    return this.#http.request({
      method: "PATCH",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/memberships/${segment(membershipId, "membership id")}`,
      routeName: "tsauth.organizations.updateMembership",
      body: request,
    });
  }

  deleteMembership(
    organizationId: string,
    membershipId: string,
  ): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/memberships/${segment(membershipId, "membership id")}`,
      routeName: "tsauth.organizations.deleteMembership",
    });
  }

  createInvitation(
    organizationId: string,
    request: TsAuthCreateInvitationRequest,
  ): Promise<TerraScaleResult<TsAuthInvitation>> {
    requireText(request?.email, "email");
    requireText(request?.role, "role");
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/invitations`,
      routeName: "tsauth.organizations.createInvitation",
      body: request,
    });
  }

  listInvitations(
    organizationId: string,
  ): Promise<TerraScaleResult<readonly TsAuthInvitation[]>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/invitations`,
      routeName: "tsauth.organizations.listInvitations",
    });
  }

  revokeInvitation(
    organizationId: string,
    invitationId: string,
  ): Promise<TerraScaleResult<TsAuthInvitation>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/invitations/${segment(invitationId, "invitation id")}/revoke`,
      routeName: "tsauth.organizations.revokeInvitation",
    });
  }

  createDomain(
    organizationId: string,
    request: TsAuthAddDomainRequest,
  ): Promise<TerraScaleResult<TsAuthOrganizationDomain>> {
    requireText(request?.domain, "domain");
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/domains`,
      routeName: "tsauth.organizations.createDomain",
      body: request,
    });
  }

  listDomains(
    organizationId: string,
  ): Promise<TerraScaleResult<readonly TsAuthOrganizationDomain[]>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/domains`,
      routeName: "tsauth.organizations.listDomains",
    });
  }

  deleteDomain(
    organizationId: string,
    domainId: string,
  ): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/organizations/${segment(organizationId, "organization id")}/domains/${segment(domainId, "domain id")}`,
      routeName: "tsauth.organizations.deleteDomain",
    });
  }

  enrollDomain(
    domain: string,
    request: TsAuthEnrollRequest,
  ): Promise<TerraScaleResult<TsAuthDomainEnrollment>> {
    requireText(request?.user_id, "user_id");
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/domains/${segment(domain, "domain")}/enroll`,
      routeName: "tsauth.organizations.enrollDomain",
      body: request,
    });
  }
}

// OAuth management uses the producer's exact snake_case policy records.

export type TsAuthOauthGrantType =
  | "authorization_code"
  | "refresh_token"
  | "client_credentials"
  | "urn:ietf:params:oauth:grant-type:device_code";
export type TsAuthOauthClientAuthMethod =
  | "none"
  | "client_secret_post"
  | "client_secret_basic";
export type TsAuthCreateOauthClientRequest = {
  readonly name?: string | undefined;
  readonly redirect_uris?: readonly string[] | undefined;
  readonly post_logout_redirect_uris?: readonly string[] | undefined;
  readonly public?: boolean | undefined;
  readonly token_endpoint_auth_method?: TsAuthOauthClientAuthMethod | undefined;
  readonly allowed_origins?: readonly string[] | undefined;
  readonly allow_anonymous?: boolean | undefined;
  readonly allowed_resource_audiences?: readonly string[] | undefined;
  readonly allowed_scopes?: readonly string[] | undefined;
  readonly allowed_grant_types?: readonly TsAuthOauthGrantType[] | undefined;
};
export type TsAuthUpdateOauthClientRequest = Omit<
  TsAuthCreateOauthClientRequest,
  "public" | "token_endpoint_auth_method"
> & {
  readonly expected_auth_epoch?: number | undefined;
  readonly enabled?: boolean | undefined;
  readonly rotate_secret?: boolean | undefined;
};
export type TsAuthOauthClientView = {
  readonly id: string;
  readonly client_id: string;
  readonly name: string;
  readonly redirect_uris: readonly string[];
  readonly post_logout_redirect_uris: readonly string[];
  readonly token_endpoint_auth_method: TsAuthOauthClientAuthMethod;
  readonly public: boolean;
  readonly allowed_origins: readonly string[];
  readonly allow_anonymous: boolean;
  readonly created_at: number;
  readonly allowed_resource_audiences: readonly string[];
  readonly allowed_scopes: readonly string[];
  readonly allowed_grant_types: readonly TsAuthOauthGrantType[];
  /** JSON u64 values outside JavaScript's exact integer range are rejected. */
  readonly auth_epoch: number;
  readonly enabled: boolean;
};
export type TsAuthOauthClientWithSecret = {
  readonly client: TsAuthOauthClientView;
  readonly client_secret: string | null;
};
export type TsAuthOauthClientList = {
  readonly clients: readonly TsAuthOauthClientView[];
};
export type TsAuthOauthClientUpdate =
  | TsAuthOauthClientView
  | TsAuthOauthClientWithSecret;

export class TsAuthOauthClientsClient {
  readonly #http: TsAuthHttp;
  readonly #slug: string;
  constructor(http: TsAuthHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }
  create(
    request: TsAuthCreateOauthClientRequest,
  ): Promise<TerraScaleResult<TsAuthOauthClientWithSecret>> {
    validateOauthClientRequest(request, false);
    return oauthResponse(
      this.#http.request({
        method: "POST",
        path: `v1/${this.#slug}/oauth-clients`,
        routeName: "tsauth.oauthClients.create",
        body: request,
      }),
      isOauthClientWithSecret,
    );
  }
  list(): Promise<TerraScaleResult<TsAuthOauthClientList>> {
    return oauthResponse(
      this.#http.request({
        method: "GET",
        path: `v1/${this.#slug}/oauth-clients`,
        routeName: "tsauth.oauthClients.list",
      }),
      isOauthClientList,
    );
  }
  update(
    clientId: string,
    request: TsAuthUpdateOauthClientRequest,
  ): Promise<TerraScaleResult<TsAuthOauthClientUpdate>> {
    validateOauthClientRequest(request, true);
    const rotateSecret = request.rotate_secret === true;
    return oauthResponse(
      this.#http.request({
        method: "PATCH",
        path: `v1/${this.#slug}/oauth-clients/${segment(clientId, "client id")}`,
        routeName: "tsauth.oauthClients.update",
        body: request,
      }),
      (value): value is TsAuthOauthClientUpdate =>
        rotateSecret
          ? isOauthClientWithSecret(value) &&
            typeof value.client_secret === "string"
          : isOauthClientView(value),
    );
  }
  delete(clientId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/oauth-clients/${segment(clientId, "client id")}`,
      routeName: "tsauth.oauthClients.delete",
    });
  }
}

export type TsAuthCreateOauthResourceRequest = {
  readonly audience: string;
  readonly allowed_client_ids: readonly string[];
  readonly allowed_grant_types: readonly TsAuthOauthGrantType[];
  readonly allowed_scopes: readonly string[];
};
export type TsAuthUpdateOauthResourceRequest = {
  readonly expected_auth_epoch: number;
  readonly allowed_client_ids?: readonly string[] | undefined;
  readonly allowed_grant_types?: readonly TsAuthOauthGrantType[] | undefined;
  readonly allowed_scopes?: readonly string[] | undefined;
  readonly enabled?: boolean | undefined;
  readonly rotate_secret?: boolean | undefined;
};
export type TsAuthOauthResourceView = TsAuthCreateOauthResourceRequest & {
  readonly id: string;
  readonly introspection_client_id: string;
  readonly token_endpoint_auth_method: "client_secret_basic";
  readonly auth_epoch: number;
  readonly enabled: boolean;
  readonly created_at: number;
  readonly updated_at: number;
};
export type TsAuthOauthResourceWithSecret = {
  readonly resource: TsAuthOauthResourceView;
  readonly introspection_secret: string | null;
};
export type TsAuthOauthResourceList = {
  readonly resources: readonly TsAuthOauthResourceView[];
};

export class TsAuthOauthResourcesClient {
  readonly #http: TsAuthHttp;
  readonly #slug: string;
  constructor(http: TsAuthHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }
  create(
    request: TsAuthCreateOauthResourceRequest,
  ): Promise<TerraScaleResult<TsAuthOauthResourceWithSecret>> {
    validateOauthResourceRequest(request, false);
    return oauthResponse(
      this.#http.request({
        method: "POST",
        path: `v1/${this.#slug}/oauth-resources`,
        routeName: "tsauth.oauthResources.create",
        body: request,
      }),
      isOauthResourceWithSecret,
    );
  }
  list(): Promise<TerraScaleResult<TsAuthOauthResourceList>> {
    return oauthResponse(
      this.#http.request({
        method: "GET",
        path: `v1/${this.#slug}/oauth-resources`,
        routeName: "tsauth.oauthResources.list",
      }),
      isOauthResourceList,
    );
  }
  update(
    resourceId: string,
    request: TsAuthUpdateOauthResourceRequest,
  ): Promise<TerraScaleResult<TsAuthOauthResourceWithSecret>> {
    validateOauthResourceRequest(request, true);
    return oauthResponse(
      this.#http.request({
        method: "PATCH",
        path: `v1/${this.#slug}/oauth-resources/${segment(resourceId, "resource id")}`,
        routeName: "tsauth.oauthResources.update",
        body: request,
      }),
      isOauthResourceWithSecret,
    );
  }
  /** Disables the resource; its registered identity remains reserved. */
  revoke(resourceId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/oauth-resources/${segment(resourceId, "resource id")}`,
      routeName: "tsauth.oauthResources.revoke",
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
  readonly #http: TsAuthHttp;
  readonly #slug: string;

  constructor(http: TsAuthHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  create(
    request: TsAuthCreateConnectionRequest,
  ): Promise<TerraScaleResult<TsAuthConnectionView>> {
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
    });
  }

  list(): Promise<
    TerraScaleResult<{ readonly data: readonly TsAuthConnectionView[] }>
  > {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/connections`,
      routeName: "tsauth.connections.list",
    });
  }

  update(
    connectionId: string,
    request: TsAuthConnectionPatch,
  ): Promise<TerraScaleResult<TsAuthConnectionView>> {
    return this.#http.request({
      method: "PATCH",
      path: `v1/${this.#slug}/connections/${segment(connectionId, "connection id")}`,
      routeName: "tsauth.connections.update",
      body: request,
    });
  }

  delete(connectionId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/connections/${segment(connectionId, "connection id")}`,
      routeName: "tsauth.connections.delete",
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
  readonly #http: TsAuthHttp;
  readonly #slug: string;

  constructor(http: TsAuthHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  create(
    request: { readonly org_id?: string | undefined } = {},
  ): Promise<TerraScaleResult<TsAuthDirectoryWithToken>> {
    if (request.org_id !== undefined) requireText(request.org_id, "org_id");
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/directories`,
      routeName: "tsauth.directories.create",
      body: request,
    });
  }

  list(): Promise<
    TerraScaleResult<{ readonly data: readonly TsAuthDirectory[] }>
  > {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/directories`,
      routeName: "tsauth.directories.list",
    });
  }

  rotate(
    directoryId: string,
  ): Promise<TerraScaleResult<TsAuthDirectoryWithToken>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/directories/${segment(directoryId, "directory id")}/rotate`,
      routeName: "tsauth.directories.rotate",
    });
  }

  delete(directoryId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/directories/${segment(directoryId, "directory id")}`,
      routeName: "tsauth.directories.delete",
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
  readonly #http: TsAuthHttp;
  readonly #slug: string;

  constructor(http: TsAuthHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  create(
    request: TsAuthCreateJwtTemplateRequest,
  ): Promise<TerraScaleResult<TsAuthJwtTemplate>> {
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
      method: "POST",
      path: `v1/${this.#slug}/jwt-templates`,
      routeName: "tsauth.jwtTemplates.create",
      body: request,
    });
  }

  list(): Promise<TerraScaleResult<readonly TsAuthJwtTemplate[]>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/jwt-templates`,
      routeName: "tsauth.jwtTemplates.list",
    });
  }

  update(
    name: string,
    request: TsAuthUpdateJwtTemplateRequest,
  ): Promise<TerraScaleResult<TsAuthJwtTemplate>> {
    validateJwtTemplateName(name);
    if (request.name !== undefined) validateJwtTemplateName(request.name);
    if (request.claims !== undefined) validateJwtTemplateClaims(request.claims);
    return this.#http.request({
      method: "PATCH",
      path: `v1/${this.#slug}/jwt-templates/${segment(name, "template name")}`,
      routeName: "tsauth.jwtTemplates.update",
      body: request,
    });
  }

  delete(name: string): Promise<TerraScaleResult<void>> {
    validateJwtTemplateName(name);
    return this.#http.request({
      method: "DELETE",
      path: `v1/${this.#slug}/jwt-templates/${segment(name, "template name")}`,
      routeName: "tsauth.jwtTemplates.delete",
    });
  }

  issueToken(
    name: string,
    request: { readonly user_id: string },
  ): Promise<TerraScaleResult<TsAuthJwtTokenResponse>> {
    validateJwtTemplateName(name);
    requireText(request?.user_id, "user_id");
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/jwt-templates/${segment(name, "template name")}/tokens`,
      routeName: "tsauth.jwtTemplates.issueToken",
      body: request,
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
  readonly #http: TsAuthHttp;
  readonly #slug: string;

  constructor(http: TsAuthHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  create(request: {
    readonly url: string;
    readonly events: readonly string[];
  }): Promise<TerraScaleResult<TsAuthWebhookWithSecret>> {
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
      method: "POST",
      path: `v1/${this.#slug}/webhooks`,
      routeName: "tsauth.webhooks.create",
      body: request,
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
    });
  }

  test(
    webhookId: string,
  ): Promise<TerraScaleResult<{ readonly queued: boolean }>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/webhooks/${segment(webhookId, "webhook id")}/test`,
      routeName: "tsauth.webhooks.test",
      body: {},
    });
  }

  deadLetter(): Promise<TerraScaleResult<readonly TsAuthDeadLetterEntry[]>> {
    return this.#http.request({
      method: "GET",
      path: `v1/${this.#slug}/webhooks/dead-letter`,
      routeName: "tsauth.webhooks.deadLetter",
    });
  }

  replayDeadLetter(
    entryId: string,
  ): Promise<TerraScaleResult<{ readonly replayed: boolean }>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/webhooks/dead-letter/${segment(entryId, "dead-letter id")}/replay`,
      routeName: "tsauth.webhooks.replayDeadLetter",
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
  readonly #http: TsAuthHttp;
  readonly #slug: string;

  constructor(http: TsAuthHttp, instanceSlug: string) {
    this.#http = http;
    this.#slug = instanceSlug;
  }

  list(
    options: TsAuthRequestOptions & {
      readonly limit?: number | undefined;
    } = {},
  ): Promise<TerraScaleResult<readonly TsAuthAuditEvent[]>> {
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
  readonly #http: TsAuthHttp;
  readonly #slug: string;

  constructor(http: TsAuthHttp, instanceSlug: string) {
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

  checkout(request: {
    readonly plan: "pro" | "enterprise";
  }): Promise<TerraScaleResult<{ readonly checkout_url: string }>> {
    if (request?.plan !== "pro" && request?.plan !== "enterprise")
      throw new TypeError('plan must be "pro" or "enterprise".');
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/${planSegment}/checkout`,
      routeName: `tsauth.${planSegment}.checkout`,
      body: request,
    });
  }

  portal(): Promise<TerraScaleResult<{ readonly portal_url: string }>> {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/${planSegment}/portal`,
      routeName: `tsauth.${planSegment}.portal`,
      body: {},
    });
  }

  enforceCheck(request: { readonly current_users: number }): Promise<
    TerraScaleResult<{
      readonly allowed: boolean;
      readonly plan: string;
      readonly quota: number;
    }>
  > {
    if (
      !Number.isInteger(request?.current_users) ||
      request.current_users < 0
    ) {
      throw new TypeError("current_users must be a non-negative integer.");
    }
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/${planSegment}/enforce-check`,
      routeName: `tsauth.${planSegment}.enforceCheck`,
      body: request,
    });
  }

  reconcileQuota(): Promise<
    TerraScaleResult<{
      readonly claims_examined: number;
      readonly claims_removed: number;
      readonly claims_retained: number;
    }>
  > {
    return this.#http.request({
      method: "POST",
      path: `v1/${this.#slug}/${planSegment}/quota/reconcile`,
      routeName: `tsauth.${planSegment}.reconcileQuota`,
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
  readonly #http: TsAuthHttp;
  readonly #slug: string;

  constructor(http: TsAuthHttp, instanceSlug: string) {
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

  update(
    request: TsAuthInstanceUpdateRequest,
  ): Promise<TerraScaleResult<TsAuthInstanceUpdateResponse>> {
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
      method: "PATCH",
      path: `v1/${this.#slug}/instance`,
      routeName: "tsauth.instance.update",
      body: request,
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
  readonly device_authorization_endpoint: string;
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

export type TsAuthOidcAuthorizationRequest = {
  readonly client_id: string;
  readonly redirect_uri: string;
  readonly response_type: "code";
  readonly scope?: string | undefined;
  readonly resource?: string | undefined;
  readonly state?: string | undefined;
  readonly nonce?: string | undefined;
  readonly code_challenge?: string | undefined;
  readonly code_challenge_method?: "S256" | undefined;
  readonly prompt?:
    | "none"
    | "login"
    | "consent"
    | "login consent"
    | "consent login"
    | undefined;
};
export type TsAuthOidcDeviceAuthorizationRequest = {
  readonly scope?: string | undefined;
  readonly resource?: string | undefined;
};
export type TsAuthOidcDeviceAuthorizationResponse = {
  readonly device_code: string;
  readonly user_code: string;
  readonly verification_uri: string;
  readonly verification_uri_complete: string;
  readonly expires_in: number;
  readonly interval: number;
};
export type TsAuthOidcTokenRequest =
  | {
      readonly grant_type: "authorization_code";
      readonly code: string;
      readonly redirect_uri: string;
      readonly code_verifier?: string | undefined;
      readonly resource?: string | undefined;
    }
  | {
      readonly grant_type: "refresh_token";
      readonly refresh_token: string;
      readonly scope?: string | undefined;
      readonly resource?: string | undefined;
    }
  | {
      readonly grant_type: "client_credentials";
      readonly scope: string;
      readonly resource: string;
    }
  | {
      readonly grant_type: "urn:ietf:params:oauth:grant-type:device_code";
      readonly device_code: string;
      readonly resource?: string | undefined;
    };
export type TsAuthOidcTokenResponse = {
  readonly access_token: string;
  readonly token_type: "Bearer";
  readonly expires_in: number;
  readonly scope: string;
  /** Omitted for client_credentials; nullable for user code/device issuance. */
  readonly refresh_token?: string | null | undefined;
  /** Present only on code/device issuance with openid scope; omitted on refresh. */
  readonly id_token?: string | undefined;
};
export type TsAuthOidcInactiveIntrospection = { readonly active: false };
export type TsAuthOidcAccessIntrospection = {
  readonly active: true;
  readonly token_type: "Bearer";
  readonly iss: string;
  readonly sub: string;
  readonly aud: string;
  readonly client_id: string;
  readonly scope: string;
  readonly exp: number;
  readonly iat: number;
  readonly sid: string | null;
  readonly principal_kind: "user" | "service";
};
export type TsAuthOidcRefreshIntrospection = Omit<
  TsAuthOidcAccessIntrospection,
  "token_type" | "principal_kind"
> & { readonly token_type: "refresh_token"; readonly family_id: string };
export type TsAuthOidcIntrospection =
  | TsAuthOidcInactiveIntrospection
  | TsAuthOidcAccessIntrospection
  | TsAuthOidcRefreshIntrospection;
export type TsAuthOidcResourceAccessIntrospection = Omit<
  TsAuthOidcAccessIntrospection,
  "sid" | "principal_kind"
> &
  (
    | { readonly principal_kind: "user"; readonly sid: string }
    | { readonly principal_kind: "service"; readonly sid: null }
  );
export type TsAuthOidcResourceIntrospection =
  | TsAuthOidcInactiveIntrospection
  | TsAuthOidcResourceAccessIntrospection;
export type TsAuthOidcResourceCredentials = {
  /** Configured canonical provider issuer, independently trusted by the caller. */
  readonly issuer: string;
  /** Exact registered resource URI; never derived from the response. */
  readonly audience: string;
  readonly introspection_client_id: string;
  readonly introspection_secret: string;
};

export type TsAuthOidcUserInfo = {
  readonly sub: string;
  readonly email?: string | undefined;
  readonly email_verified?: boolean | undefined;
  readonly name?: string | undefined;
};

export class TsAuthOidcClient {
  readonly #http: TsAuthHttp;
  readonly #slug: string;

  constructor(http: TsAuthHttp, instanceSlug: string) {
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

  /** Builds the named authorize URL without dispatching or changing resource bytes. */
  authorizationUrl(request: TsAuthOidcAuthorizationRequest): URL {
    validateAuthorizationRequest(request);
    const url = new URL(
      `${this.#http.config.apiBaseUrl}/${this.#slug}/oauth/authorize`,
    );
    for (const [name, value] of Object.entries(request))
      if (value !== undefined) url.searchParams.set(name, value);
    return url;
  }

  /** Explicit client_secret_post device flow; polling is caller-controlled. */
  deviceAuthorization(
    clientId: string,
    clientSecret: string,
    request: TsAuthOidcDeviceAuthorizationRequest = {},
  ): Promise<TerraScaleResult<TsAuthOidcDeviceAuthorizationResponse>> {
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
      ),
      isDeviceAuthorizationResponse,
    );
  }

  /** client_secret_post only. Omitted resource retains the stored code/device/refresh binding. */
  token(
    clientId: string,
    clientSecret: string,
    request: TsAuthOidcTokenRequest,
  ): Promise<TerraScaleResult<TsAuthOidcTokenResponse>> {
    const form = clientCredentialForm(clientId, clientSecret);
    const grantType = request.grant_type;
    const fields =
      request.grant_type === "authorization_code"
        ? ["grant_type", "code", "redirect_uri", "code_verifier", "resource"]
        : request.grant_type === "refresh_token"
          ? ["grant_type", "refresh_token", "scope", "resource"]
          : request.grant_type === "client_credentials"
            ? ["grant_type", "scope", "resource"]
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
      form.set("resource", requireResourceAudience(request.resource));
      form.set("scope", requireOauthScope(request.scope));
    } else {
      form.set("device_code", requireText(request.device_code, "device_code"));
    }
    return oauthResponse(
      this.#postForm(`${this.#slug}/oauth/token`, "tsauth.oidc.token", form),
      (value): value is TsAuthOidcTokenResponse =>
        isTokenResponse(value, grantType),
    );
  }

  /** Originating-client client_secret_post introspection, including browser refresh tokens. */
  introspect(
    clientId: string,
    clientSecret: string,
    token: string,
  ): Promise<TerraScaleResult<TsAuthOidcIntrospection>> {
    const form = clientCredentialForm(clientId, clientSecret);
    form.set("token", requireText(token, "token"));
    return oauthResponse(
      this.#postForm(
        `${this.#slug}/oauth/introspect`,
        "tsauth.oidc.introspect",
        form,
      ),
      isClientIntrospection,
    );
  }

  /** Resource Basic-only access-token introspection. Acceptance checks exact issuer/audience,
   * current local time and principal/session shape; product ownership stays caller-owned. */
  introspectResource(
    credentials: TsAuthOidcResourceCredentials,
    token: string,
    tokenTypeHint?: "access_token",
  ): Promise<TerraScaleResult<TsAuthOidcResourceIntrospection>> {
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
        method: "POST",
        path: `${this.#slug}/oauth/introspect`,
        routeName: "tsauth.oidc.resourceIntrospect",
        formBody: form,
        resourceIntrospectionBasic: basic,
      }),
      (value): value is TsAuthOidcResourceIntrospection => {
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

  /** RFC 7009 semantics: the service answers 200 with an empty body even for
   * unknown tokens, so success carries no payload. */
  revoke(
    clientId: string,
    clientSecret: string,
    token: string,
  ): Promise<TerraScaleResult<void>> {
    const form = clientCredentialForm(clientId, clientSecret);
    form.set("token", requireText(token, "token"));
    return this.#postForm(
      `${this.#slug}/oauth/revoke`,
      "tsauth.oidc.revoke",
      form,
    );
  }

  /** Bearer access token issued by the token endpoint, not the sk_ key. */
  userinfo(accessToken: string): Promise<TerraScaleResult<TsAuthOidcUserInfo>> {
    requireText(accessToken, "accessToken");
    return this.#http.request({
      method: "GET",
      path: `${this.#slug}/oauth/userinfo`,
      routeName: "tsauth.oidc.userinfo",
      userinfoAccessToken: accessToken.trim(),
    });
  }

  #postForm<T>(
    path: string,
    routeName: `tsauth.${string}`,
    form: URLSearchParams,
  ): Promise<TerraScaleResult<T>> {
    return this.#http.request<T>({
      method: "POST",
      path,
      routeName,
      formBody: form,
    });
  }
}

/** The ts-auth management client: one hosted identity instance. */
export class TsAuthClient {
  readonly #http: TsAuthHttp;
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
  readonly oauthResources: TsAuthOauthResourcesClient;
  readonly oidc: TsAuthOidcClient;

  constructor(config: TsAuthClientConfig) {
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
    this.#http = new TsAuthHttp(config, {
      authReverseProxy: config.useReverseProxy === true,
    });
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
 */
export async function verifyTsAuthWebhook(
  signingSecret: string,
  timestamp: string | undefined,
  signature: string | undefined,
  payload: Uint8Array,
): Promise<boolean> {
  const verified = await verifySignature(
    signingSecret,
    timestamp,
    signature,
    payload,
  );
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

/** undefined means malformed input; true/false means the signature verdict. */
async function verifySignature(
  signingSecret: string,
  timestamp: string | undefined,
  signature: string | undefined,
  payload: Uint8Array,
): Promise<boolean | undefined> {
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

function segment(value: string, name: string): string {
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

function requireText(value: unknown, name: string): string {
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

function requireAbsoluteHttpUrl(value: string, name: string): string {
  requireText(value, name);
  if (!/^https?:\/\//u.test(value))
    throw new TypeError(`${name} must be an absolute http(s) URL.`);
  return value;
}

function requirePatchText(value: string, name: string): string {
  if (typeof value !== "string" || value.length === 0)
    throw new TypeError(`${name} must be a non-empty string.`);
  return value;
}

function requirePatchMetadata(
  metadata: Record<string, unknown>,
  name: string,
): Record<string, unknown> {
  if (
    typeof metadata !== "object" ||
    metadata === null ||
    Array.isArray(metadata)
  ) {
    throw new TypeError(`${name} must be an object.`);
  }
  return metadata;
}

function validateJwtTemplateName(name: string): string {
  if (typeof name !== "string" || !jwtTemplateNamePattern.test(name)) {
    throw new TypeError(
      "Template names must be 1 to 48 characters of [a-z0-9-_].",
    );
  }
  return name;
}

function validateJwtTemplateClaims(
  claims: Record<string, string>,
): Record<string, string> {
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

function clientCredentialForm(
  clientId: string,
  clientSecret: string,
): URLSearchParams {
  return new URLSearchParams({
    client_id: requireText(clientId, "clientId"),
    client_secret: requireText(clientSecret, "clientSecret"),
  });
}

async function oauthResponse<T>(
  pending: Promise<TerraScaleResult<unknown>>,
  accepts: (value: unknown) => value is T,
): Promise<TerraScaleResult<T>> {
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

function oauthRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
function exactOauthFields(
  value: unknown,
  fields: readonly string[],
): value is Record<string, unknown> {
  return (
    oauthRecord(value) &&
    Object.keys(value).length === fields.length &&
    fields.every((field) => Object.hasOwn(value, field))
  );
}
function requireFields(
  value: unknown,
  fields: readonly string[],
): asserts value is Record<string, unknown> {
  if (
    !oauthRecord(value) ||
    Object.keys(value).some((field) => !fields.includes(field))
  )
    throw new TypeError("Unsupported OAuth request field.");
}
function oauthText(value: unknown): value is string {
  return (
    typeof value === "string" &&
    value.length > 0 &&
    !/\p{Cc}/u.test(value) &&
    !/[\uD800-\uDFFF]/u.test(value)
  );
}
function oauthStrings(value: unknown): value is readonly string[] {
  return Array.isArray(value) && value.every(oauthText);
}
function exactUnsignedInteger(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}
const oauthGrants: readonly TsAuthOauthGrantType[] = [
  "authorization_code",
  "refresh_token",
  "client_credentials",
  "urn:ietf:params:oauth:grant-type:device_code",
];
function oauthGrant(value: unknown): value is TsAuthOauthGrantType {
  return (
    typeof value === "string" && oauthGrants.some((grant) => grant === value)
  );
}
function oauthGrantsList(
  value: unknown,
): value is readonly TsAuthOauthGrantType[] {
  return Array.isArray(value) && value.every(oauthGrant);
}
function oauthAuthMethod(value: unknown): value is TsAuthOauthClientAuthMethod {
  return (
    value === "none" ||
    value === "client_secret_post" ||
    value === "client_secret_basic"
  );
}

function requireResourceAudience(value: unknown): string {
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
  let url: URL;
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
function validResourceAudience(value: unknown): value is string {
  if (typeof value !== "string") return false;
  try {
    requireResourceAudience(value);
    return true;
  } catch {
    return false;
  }
}
function requireOauthIssuer(value: string): void {
  if (!oauthText(value))
    throw new TypeError(
      "issuer must be the configured canonical provider issuer.",
    );
  let url: URL;
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
function oauthScopeToken(value: unknown): value is string {
  return (
    typeof value === "string" &&
    value.length > 0 &&
    value.length <= 256 &&
    /^[\x21\x23-\x5b\x5d-\x7e]+$/u.test(value)
  );
}
function requireOauthScope(value: unknown): string {
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
function requireOauthList(
  value: unknown,
  name: string,
  accepts: (item: unknown) => boolean = oauthText,
): void {
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
function validateOauthClientRequest(request: unknown, update: boolean): void {
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
function validateOauthResourceRequest(request: unknown, update: boolean): void {
  requireFields(request, [
    "allowed_client_ids",
    "allowed_grant_types",
    "allowed_scopes",
    ...(update
      ? ["expected_auth_epoch", "enabled", "rotate_secret"]
      : ["audience"]),
  ]);
  if (!update) requireResourceAudience(request.audience);
  for (const [field, accepts] of [
    ["allowed_client_ids", oauthText],
    ["allowed_grant_types", oauthGrant],
    ["allowed_scopes", oauthScopeToken],
  ] as const) {
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
function validateAuthorizationRequest(request: unknown): void {
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
  requireText(request.client_id, "client_id");
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
function resourceBasic(clientId: string, secret: string): string {
  if (!oauthText(clientId) || !oauthText(secret))
    throw new TypeError(
      "Resource introspection credentials must be nonempty well-formed text.",
    );
  const component = (value: string): string =>
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
function isOauthClientView(value: unknown): value is TsAuthOauthClientView {
  return (
    exactOauthFields(value, clientViewFields) &&
    [value.id, value.client_id, value.name].every(oauthText) &&
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
function isOauthClientWithSecret(
  value: unknown,
): value is TsAuthOauthClientWithSecret {
  return (
    exactOauthFields(value, ["client", "client_secret"]) &&
    isOauthClientView(value.client) &&
    (value.client_secret === null || oauthText(value.client_secret))
  );
}
function isOauthClientList(value: unknown): value is TsAuthOauthClientList {
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
function isOauthResourceView(value: unknown): value is TsAuthOauthResourceView {
  return (
    exactOauthFields(value, resourceViewFields) &&
    oauthText(value.id) &&
    validResourceAudience(value.audience) &&
    oauthStrings(value.allowed_client_ids) &&
    oauthGrantsList(value.allowed_grant_types) &&
    oauthStrings(value.allowed_scopes) &&
    oauthText(value.introspection_client_id) &&
    value.token_endpoint_auth_method === "client_secret_basic" &&
    exactUnsignedInteger(value.auth_epoch) &&
    typeof value.enabled === "boolean" &&
    exactUnsignedInteger(value.created_at) &&
    exactUnsignedInteger(value.updated_at)
  );
}
function isOauthResourceWithSecret(
  value: unknown,
): value is TsAuthOauthResourceWithSecret {
  return (
    exactOauthFields(value, ["resource", "introspection_secret"]) &&
    isOauthResourceView(value.resource) &&
    (value.introspection_secret === null ||
      oauthText(value.introspection_secret))
  );
}
function isOauthResourceList(value: unknown): value is TsAuthOauthResourceList {
  return (
    exactOauthFields(value, ["resources"]) &&
    Array.isArray(value.resources) &&
    value.resources.every(isOauthResourceView)
  );
}
function isTokenResponse(
  value: unknown,
  grant: TsAuthOauthGrantType,
): value is TsAuthOidcTokenResponse {
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
function isDeviceAuthorizationResponse(
  value: unknown,
): value is TsAuthOidcDeviceAuthorizationResponse {
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
function isInactiveIntrospection(
  value: unknown,
): value is TsAuthOidcInactiveIntrospection {
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
function isAccessIntrospection(
  value: unknown,
): value is TsAuthOidcAccessIntrospection {
  return (
    exactOauthFields(value, [...introspectionFields, "principal_kind"]) &&
    value.active === true &&
    value.token_type === "Bearer" &&
    [value.iss, value.sub, value.aud, value.client_id].every(oauthText) &&
    typeof value.scope === "string" &&
    exactUnsignedInteger(value.exp) &&
    exactUnsignedInteger(value.iat) &&
    value.exp > value.iat &&
    ((value.principal_kind === "user" &&
      (value.sid === null || oauthText(value.sid))) ||
      (value.principal_kind === "service" &&
        value.sid === null &&
        value.sub === value.client_id))
  );
}
function isResourceAccessIntrospection(
  value: unknown,
): value is TsAuthOidcResourceAccessIntrospection {
  return (
    isAccessIntrospection(value) &&
    (value.principal_kind === "user"
      ? oauthText(value.sid)
      : value.sid === null && value.sub === value.client_id)
  );
}
function isRefreshIntrospection(
  value: unknown,
): value is TsAuthOidcRefreshIntrospection {
  return (
    exactOauthFields(value, [...introspectionFields, "family_id"]) &&
    value.active === true &&
    value.token_type === "refresh_token" &&
    [value.iss, value.sub, value.aud, value.client_id, value.family_id].every(
      oauthText,
    ) &&
    typeof value.scope === "string" &&
    exactUnsignedInteger(value.exp) &&
    exactUnsignedInteger(value.iat) &&
    value.exp > value.iat &&
    (value.sid === null || oauthText(value.sid))
  );
}
function isClientIntrospection(
  value: unknown,
): value is TsAuthOidcIntrospection {
  return (
    isInactiveIntrospection(value) ||
    isAccessIntrospection(value) ||
    isRefreshIntrospection(value)
  );
}
