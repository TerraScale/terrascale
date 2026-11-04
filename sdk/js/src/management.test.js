import { readFile } from "node:fs/promises";
import { expect, test } from "vitest";
import { createTerraScaleIdentityManagementClient, createTerraScaleAccountDeletionClient, createTerraScaleProjectIdentityClient, createTerraScaleManagementClient, requireTerraScaleManagement } from "./management.js";
import { createTerraScaleAdministrationClient, requireTerraScaleAdministration } from "./admin.js";
import { V1Databases } from "./v1/catalog.js";
import { V1Database } from "./v1/terrabase-v1.js";
import { V1Platform } from "./v1/system.js";
import { v1Routes } from "./v1/routes.js";
import { managementV1Routes } from "./v1/management-routes.js";
import { platformIdentityContract, platformIdentityRoutes } from "./platform-management-routes.js";
import { platformIdentityOpenApi } from "../contracts/platform-identity/openapi.js";
import { organization, project, apiKey, keySecret, invitation, invitationCreated, invitationLookup, acceptedInvitation, ownershipTransfer, ssoConnection, auditEvent, accountMfa, linkedProviders, deletionHistory, endUserToken } from "./platform-management-decode.js";
import { TsAuthHttp } from "./http.js";

// Isolated construction/contract tests never call a product boundary.
const config = Object.freeze({
  baseUrl: "https://management.example.invalid",
  project: "01ARZ3NDEKTSV4RRFFQ69G5FAV",
  token: "test.payload.signature",
});

const identityConfig = Object.freeze({ apiBaseUrl: "https://identity.example.invalid", signInToken: config.token });
const id = config.project, date = "2026-10-26T00:00:00Z", orgPath = `/v1/terrascale/organizations/${id}`;
const deletionReceipt = "A".repeat(43), projectKey = `${id}_${"a".repeat(52)}`;
const invitationBody = { id, email: "invitee@example.invalid", role: "member", project_id: null,
  invited_by: id, created_at: date, expires_at: date, status: "pending" };
const transferBody = { from_user_id: id, to_user_id: id, created_at: date, expires_at: date };
const ssoBody = { organization_id: id, domain: "example.invalid", domain_verified: false, verified_at: null,
  verification: { type: "TXT", name: "_terrascale-sso.example.invalid", value: "fixture-verification" },
  protocol: "oidc", default_role: "member", oidc: { issuer: null, client_id: null, client_secret_set: true,
    redirect_uri: "https://identity.example.invalid/ui/terrascale/sso/callback" }, created_at: date, updated_at: date };
const auditBody = { id, actor: id, action: "member.changed", target: null, detail: null, occurred_at: date };
const mfaBody = { enabled: true, recovery_codes_remaining: 7, manage_url: "https://identity.example.invalid/ui/terrascale/security" };
const organizationBody = { id, name: "Example", created_by: id, created_at: date, role: "owner", require_mfa: true };
const memberBody = { user_id: id, email: "owner@example.invalid", role: "owner", joined_at: date };
const lookupBody = { organization: { id, name: "Example" }, project: null, email: invitationBody.email, role: "member", expires_at: date };
const acceptedBody = { organization_id: id, project_id: null, role: "member" };
const historyBody = { erasureId: "opaque-erasure", requestedAt: date, authLogicalVerifiedAt: null, historyStatus: null, historyCompletedAt: null };
const tokenBody = { access_token: "fixture.enduser.signature", token_type: "Bearer", expires_in: 900 };
const ssoRequest = /** @type {const} */ ({ domain: "example.invalid", protocol: "oidc", default_role: "member",
  oidc: { issuer: "https://issuer.example.invalid", client_id: "fixture-client", client_secret: "fixture-oidc-secret" } });
/** @typedef {{identity: import("./management.js").TerraScaleIdentityManagementClient; deletion: import("./management.js").TerraScaleAccountDeletionClient; backend: import("./management.js").TerraScaleProjectIdentityClient}} IdentityClients */
/** @type {{operation: keyof typeof platformIdentityRoutes; method: string; path: string; body?: object; query?: Record<string, string>; status: number; response?: import("./v1/json.js").JsonValue; run: (clients: IdentityClients) => Promise<import("./result.js").TerraScaleResult<unknown>>}[]} */
const addedCases = [
  { operation: "create_invitation", method: "POST", path: `${orgPath}/invitations`, body: { email: invitationBody.email, role: "member" }, status: 201, response: { invitation: invitationBody, token: "fixture-invitation-token" }, run: c => c.identity.createInvitation(id, { email: invitationBody.email, role: "member" }) },
  { operation: "list_invitations", method: "GET", path: `${orgPath}/invitations`, query: { limit: "3", after: "opaque cursor", project_id: id }, status: 200, response: { items: [invitationBody], next: "next-cursor" }, run: c => c.identity.listInvitations(id, { limit: 3, after: "opaque cursor", project_id: id }) },
  { operation: "revoke_invitation", method: "DELETE", path: `${orgPath}/invitations/${id}`, status: 204, run: c => c.identity.revokeInvitation(id, id) },
  { operation: "start_ownership_transfer", method: "POST", path: `${orgPath}/ownership-transfer`, body: { user_id: id }, status: 201, response: transferBody, run: c => c.identity.startOwnershipTransfer(id, { user_id: id }) },
  { operation: "get_ownership_transfer", method: "GET", path: `${orgPath}/ownership-transfer`, status: 200, response: transferBody, run: c => c.identity.getOwnershipTransfer(id) },
  { operation: "cancel_ownership_transfer", method: "DELETE", path: `${orgPath}/ownership-transfer`, status: 204, run: c => c.identity.cancelOwnershipTransfer(id) },
  { operation: "confirm_ownership_transfer", method: "POST", path: `${orgPath}/ownership-transfer/confirm`, status: 200, response: memberBody, run: c => c.identity.confirmOwnershipTransfer(id) },
  { operation: "get_sso_connection", method: "GET", path: `${orgPath}/sso`, status: 200, response: ssoBody, run: c => c.identity.getSsoConnection(id) },
  { operation: "put_sso_connection", method: "PUT", path: `${orgPath}/sso`, body: ssoRequest, status: 200, response: ssoBody, run: c => c.identity.putSsoConnection(id, ssoRequest) },
  { operation: "delete_sso_connection", method: "DELETE", path: `${orgPath}/sso`, status: 204, run: c => c.identity.deleteSsoConnection(id) },
  { operation: "verify_sso_domain", method: "POST", path: `${orgPath}/sso/verify`, status: 200, response: ssoBody, run: c => c.identity.verifySsoDomain(id) },
  { operation: "set_organization_mfa_policy", method: "PUT", path: `${orgPath}/mfa-policy`, body: { require_mfa: true }, status: 200, response: organizationBody, run: c => c.identity.setOrganizationMfaPolicy(id, { require_mfa: true }) },
  { operation: "list_audit_events", method: "GET", path: `${orgPath}/audit-events`, query: { limit: "2", after: "audit-cursor" }, status: 200, response: { items: [auditBody], next: null }, run: c => c.identity.listAuditEvents(id, { limit: 2, after: "audit-cursor" }) },
  { operation: "lookup_invitation", method: "POST", path: "/v1/terrascale/invitations/lookup", body: { token: "fixture-invitation-token" }, status: 200, response: lookupBody, run: c => c.identity.lookupInvitation({ token: "fixture-invitation-token" }) },
  { operation: "accept_invitation", method: "POST", path: "/v1/terrascale/invitations/accept", body: { token: "fixture-invitation-token" }, status: 200, response: acceptedBody, run: c => c.identity.acceptInvitation({ token: "fixture-invitation-token" }) },
  { operation: "decline_invitation", method: "POST", path: "/v1/terrascale/invitations/decline", body: { token: "fixture-invitation-token" }, status: 204, run: c => c.identity.declineInvitation({ token: "fixture-invitation-token" }) },
  { operation: "get_account_mfa", method: "GET", path: "/v1/terrascale/account/mfa", status: 200, response: mfaBody, run: c => c.identity.getAccountMfa() },
  { operation: "account_linked_providers", method: "GET", path: "/v1/terrascale/account/providers", status: 200, response: { providers: ["github", "google"] }, run: c => c.identity.accountLinkedProviders() },
  { operation: "account_unlink_provider", method: "DELETE", path: "/v1/terrascale/account/providers/github", body: { code: "fixture-code" }, status: 204, run: c => c.identity.accountUnlinkProvider("github", { code: "fixture-code" }) },
  { operation: "delete_account", method: "DELETE", path: "/v1/terrascale/account", status: 202, run: c => c.identity.deleteAccount({ receipt: deletionReceipt }) },
  { operation: "account_deletion_status", method: "GET", path: "/v1/terrascale/account/deletion", query: { organization: id }, status: 202, run: c => c.deletion.accountDeletionStatus({ receipt: deletionReceipt, organization: id }) },
  { operation: "account_deletion_history", method: "GET", path: "/v1/terrascale/account/deletion/history", query: { organization: id }, status: 200, response: historyBody, run: c => c.deletion.accountDeletionHistory({ receipt: deletionReceipt, organization: id }) },
  { operation: "mintEndUserToken", method: "POST", path: `/v1/terrascale/projects/${id}/end-user-tokens`, body: { user_id: id, claims: { roles: ["reader"] } }, status: 200, response: tokenBody, run: c => c.backend.mintEndUserToken({ user_id: id, claims: { roles: ["reader"] } }) },
];

test.each(addedCases)("published identity HTTP contract: $operation", async row => {
  let calls = 0;
  /** @type {import("./config.js").FetchLike} */
  const fetch = async input => {
    calls++;
    const request = /** @type {Request} */ (input), url = new URL(request.url);
    expect(request.method).toBe(row.method);
    expect(url.pathname).toBe(row.path);
    expect(Object.fromEntries(url.searchParams)).toEqual(row.query ?? {});
    expect(await request.text()).toBe(row.body === undefined ? "" : JSON.stringify(row.body));
    expect(request.headers.get("content-type")).toBe(row.body === undefined ? null : "application/json");
    const receiptOnly = row.operation.startsWith("account_deletion_");
    expect(request.headers.get("authorization")).toBe(receiptOnly ? null : `Bearer ${row.operation === "mintEndUserToken" ? projectKey : config.token}`);
    expect(request.headers.get("X-TerraScale-Deletion-Receipt")).toBe(receiptOnly || row.operation === "delete_account" ? deletionReceipt : null);
    expect(url.href).not.toContain(deletionReceipt);
    return new Response(row.response === undefined ? null : JSON.stringify(row.response), {
      status: row.status, headers: { ...(row.response === undefined ? {} : { "content-type": "application/json" }), "x-request-id": id },
    });
  };
  const clients = { identity: createTerraScaleIdentityManagementClient({ ...identityConfig, fetch }),
    deletion: createTerraScaleAccountDeletionClient({ apiBaseUrl: identityConfig.apiBaseUrl, fetch }),
    backend: createTerraScaleProjectIdentityClient({ apiBaseUrl: identityConfig.apiBaseUrl, project: id, projectApiKey: projectKey, fetch }) };
  const result = await row.run(clients);
  expect(result).toMatchObject({ ok: true, status: row.status, routeName: `tsauth.platform.${row.operation}`, value: row.response });
  expect(result.headers?.get("x-request-id")).toBe(id);
  expect(calls).toBe(1);
  expect(JSON.stringify(clients)).toBe('{"identity":{},"deletion":{},"backend":{}}');
});

test("identity decoders enforce every pinned required field and nested SSO types", () => {
  /** @type {[import("./v1/management-decode.js").Decoder<unknown>, import("./v1/json.js").JsonObject, string][]} */
  const cases = [
    [invitation, invitationBody, "InvitationBody"], [invitationCreated, { invitation: invitationBody, token: "fixture-token" }, "InvitationCreatedBody"],
    [invitationLookup, lookupBody, "InvitationLookupBody"], [acceptedInvitation, acceptedBody, "AcceptedInvitationBody"],
    [ownershipTransfer, transferBody, "OwnershipTransferBody"], [ssoConnection, ssoBody, "SsoConnectionBody"],
    [auditEvent, auditBody, "AuditEventBody"], [accountMfa, mfaBody, "AccountMfaBody"],
    [linkedProviders, { providers: ["github"] }, "LinkedProvidersBody"], [deletionHistory, historyBody, "AccountDeletionHistory"],
    [endUserToken, tokenBody, "EndUserTokenBody"],
  ];
  for (const [decode, body, name] of cases) {
    expect(decode(body)).toEqual(body);
    const schema = Reflect.get(platformIdentityOpenApi.components.schemas, name);
    for (const key of schema.required) {
      const missing = { ...body };
      delete missing[key];
      expect(() => decode(missing), `${name}.${key}`).toThrow(TypeError);
    }
  }
  expect(() => invitation({ ...invitationBody, status: "unknown" })).toThrow(TypeError);
  expect(() => ownershipTransfer({ ...transferBody, to_user_id: "invalid" })).toThrow(TypeError);
  expect(() => ssoConnection({ ...ssoBody, verification: { ...ssoBody.verification, type: "A" } })).toThrow(TypeError);
  expect(() => ssoConnection({ ...ssoBody, oidc: { ...ssoBody.oidc, client_secret_set: null } })).toThrow(TypeError);
  expect(ssoConnection({ ...ssoBody, oidc: null }).oidc).toBeNull();
  expect(() => auditEvent({ ...auditBody, detail: 3 })).toThrow(TypeError);
  expect(() => accountMfa({ ...mfaBody, recovery_codes_remaining: 1.1 })).toThrow(TypeError);
  expect(accountMfa({ ...mfaBody, recovery_codes_remaining: "7" }).recovery_codes_remaining).toBe(7);
  expect(() => endUserToken({ ...tokenBody, expires_in: 901 })).toThrow(TypeError);
  expect(() => endUserToken({ ...tokenBody, access_token: "invalid" })).toThrow(TypeError);
});

test("new identity requests preserve auth boundaries and reject invalid named fields", () => {
  const identity = createTerraScaleIdentityManagementClient(identityConfig);
  const deletion = createTerraScaleAccountDeletionClient({ apiBaseUrl: identityConfig.apiBaseUrl });
  const backend = createTerraScaleProjectIdentityClient({ apiBaseUrl: identityConfig.apiBaseUrl, project: id, projectApiKey: projectKey });
  expect(Object.keys(deletion).sort()).toEqual(["accountDeletionHistory", "accountDeletionStatus"]);
  expect(Object.keys(backend)).toEqual(["mintEndUserToken"]);
  expect(() => createTerraScaleAccountDeletionClient(/** @type {any} */ (identityConfig))).toThrow(TypeError);
  expect(() => createTerraScaleProjectIdentityClient({ apiBaseUrl: identityConfig.apiBaseUrl, project: id, projectApiKey: config.token })).toThrow(TypeError);
  expect(() => identity.createInvitation(id, { email: "x@example.invalid", role: "owner", project_id: id })).toThrow(TypeError);
  expect(() => identity.listInvitations(id, { project_id: "invalid" })).toThrow(TypeError);
  expect(() => identity.startOwnershipTransfer(id, { user_id: "invalid" })).toThrow(TypeError);
  expect(() => identity.lookupInvitation(/** @type {any} */ ({ token: "fixture-token", headers: {} }))).toThrow(TypeError);
  expect(() => identity.putSsoConnection(id, { ...ssoRequest, oidc: { ...ssoRequest.oidc, issuer: "http://issuer.example.invalid" } })).toThrow(TypeError);
  expect(() => identity.putSsoConnection(id, { ...ssoRequest, oidc: { ...ssoRequest.oidc, issuer: "https://user:password@issuer.example.invalid" } })).toThrow(TypeError);
  expect(() => identity.putSsoConnection(id, { ...ssoRequest, oidc: { ...ssoRequest.oidc, client_secret: "" } })).toThrow(TypeError);
  expect(() => identity.setOrganizationMfaPolicy(id, /** @type {any} */ ({ require_mfa: null }))).toThrow(TypeError);
  expect(() => identity.accountUnlinkProvider(/** @type {any} */ ("../github"), { code: "fixture-code" })).toThrow(TypeError);
  expect(() => identity.deleteAccount({ receipt: "invalid" })).toThrow(TypeError);
  expect(() => deletion.accountDeletionStatus({ receipt: "A".repeat(42) + "B" })).toThrow(TypeError);
  expect(() => deletion.accountDeletionHistory({ receipt: deletionReceipt, organization: "invalid" })).toThrow(TypeError);
  expect(() => backend.mintEndUserToken({ user_id: "invalid" })).toThrow(TypeError);
  expect(() => backend.mintEndUserToken({ user_id: id, claims: { roles: [" "] } })).toThrow(TypeError);
  expect(() => backend.mintEndUserToken(/** @type {any} */ ({ user_id: id, claims: { project_id: id } }))).toThrow(TypeError);
});

test.each([
  ["create_invitation", 429, "rate_limited"], ["verify_sso_domain", 409, "domain_unverified"],
  ["set_organization_mfa_policy", 409, "mfa_not_enrolled"], ["accept_invitation", 403, "email_mismatch"],
  ["account_unlink_provider", 401, "invalid_credentials"], ["mintEndUserToken", 503, "unavailable"],
])("identity error semantics without mutation replay: %s", async (operation, status, code) => {
  const row = addedCases.find(item => item.operation === operation);
  if (row === undefined) throw new Error("Missing named case.");
  let calls = 0;
  /** @type {import("./config.js").FetchLike} */
  const fetch = async () => { calls++; return new Response(JSON.stringify({ error: { code, message: "Denied" } }), {
    status, headers: { "content-type": "application/json", "retry-after": "7", "x-request-id": id },
  }); };
  const clients = { identity: createTerraScaleIdentityManagementClient({ ...identityConfig, fetch }),
    deletion: createTerraScaleAccountDeletionClient({ apiBaseUrl: identityConfig.apiBaseUrl, fetch }),
    backend: createTerraScaleProjectIdentityClient({ apiBaseUrl: identityConfig.apiBaseUrl, project: id, projectApiKey: projectKey, fetch }) };
  expect(await row.run(clients)).toMatchObject({ ok: false, status, error: { code, message: "Denied", requestId: id, retryAfter: 7000 } });
  expect(calls).toBe(1);
});

test("SSO source defaults preserve a stored secret and responses omit secret-bearing extra members", async () => {
  const request = { ...ssoRequest, protocol: null, default_role: null,
    oidc: { issuer: ssoRequest.oidc.issuer, client_id: ssoRequest.oidc.client_id } };
  const client = createTerraScaleIdentityManagementClient({ ...identityConfig, fetch: async input => {
    expect(await /** @type {Request} */ (input).json()).toEqual(request);
    return new Response(JSON.stringify({ ...ssoBody, oidc: { ...ssoBody.oidc, client_secret: ssoRequest.oidc.client_secret } }),
      { headers: { "content-type": "application/json" } });
  } });
  const result = await client.putSsoConnection(id, request);
  expect(result).toMatchObject({ ok: true, value: ssoBody });
  expect(JSON.stringify(result)).not.toContain(ssoRequest.oidc.client_secret);
});

test.each([202, 204])("account deletion and receipt status preserve empty %i", async status => {
  const fetch = async () => new Response(null, { status });
  const identity = createTerraScaleIdentityManagementClient({ ...identityConfig, fetch });
  const deletion = createTerraScaleAccountDeletionClient({ apiBaseUrl: identityConfig.apiBaseUrl, fetch });
  expect(await identity.deleteAccount()).toMatchObject({ ok: true, status, value: undefined });
  expect(await deletion.accountDeletionStatus({ receipt: deletionReceipt })).toMatchObject({ ok: true, status, value: undefined });
});

test("deletion receipt header cannot reach other paths or combine with bearer credentials", async () => {
  let calls = 0;
  const fetch = async () => { calls++; return new Response(null, { status: 204 }); };
  for (const [path, routeName, method] of [
    ["/v1/terrascale/account/deletion", "tsauth.platform.account_deletion_status", "POST"],
    ["/v1/terrascale/account/providers", "tsauth.platform.account_deletion_status", "GET"],
    ["/v1/terrascale/account/deletion/history", "tsauth.platform.delete_account", "GET"],
  ]) {
    const http = new TsAuthHttp({ apiBaseUrl: identityConfig.apiBaseUrl, fetch });
    expect(await http.request(/** @type {import("./http.js").TsAuthRequest<unknown>} */ ({ path, routeName, method, deletionReceipt })))
      .toMatchObject({ ok: false, error: { kind: "transport" } });
  }
  const http = new TsAuthHttp({ apiBaseUrl: identityConfig.apiBaseUrl, accessToken: config.token, fetch });
  expect(await http.request({ path: "/v1/terrascale/account/deletion", routeName: "tsauth.platform.account_deletion_status", method: "GET", deletionReceipt }))
    .toMatchObject({ ok: false, error: { kind: "transport" } });
  expect(calls).toBe(0);
});

test("receipt and backend credential exceptions cannot survive in public results", async () => {
  const backend = createTerraScaleProjectIdentityClient({ apiBaseUrl: identityConfig.apiBaseUrl, project: id,
    projectApiKey: () => { throw new Error(projectKey); }, fetch: async () => { throw new Error("Unexpected request."); } });
  const tokenResult = await backend.mintEndUserToken({ user_id: id });
  expect(tokenResult).toMatchObject({ ok: false, error: { message: "Could not resolve the project API key." } });
  expect(JSON.stringify(tokenResult)).not.toContain(projectKey);
  const deletion = createTerraScaleAccountDeletionClient({ apiBaseUrl: identityConfig.apiBaseUrl,
    fetch: async () => { throw new Error(deletionReceipt, { cause: deletionReceipt }); } });
  expect(JSON.stringify(await deletion.accountDeletionHistory({ receipt: deletionReceipt }))).not.toContain(deletionReceipt);
});

test("provider failures discard credential-bearing messages and causes without sending a request", async () => {
  const sensitive = "provider-sensitive-value-for-redaction-test";
  let requests = 0;
  const client = createTerraScaleIdentityManagementClient({ ...identityConfig,
    signInToken: async () => { throw new Error(sensitive, { cause: { credential: sensitive } }); },
    fetch: async () => { requests++; throw new Error("Unexpected request."); },
  });
  const result = await client.listOrganizations();
  expect(result).toMatchObject({ ok: false, error: { kind: "transport", message: "Could not resolve the platform sign-in token." } });
  expect(JSON.stringify(result)).not.toContain(sensitive);
  expect(JSON.stringify(client)).toBe("{}");
  expect(requests).toBe(0);
});

test("pending project closure preserves empty 202 and completed closure preserves 204", async () => {
  for (const status of [202, 204]) {
    const client = createTerraScaleIdentityManagementClient({ ...identityConfig,
      fetch: async input => {
        expect(input).toBeInstanceOf(Request);
        const request = /** @type {Request} */ (input);
        expect(request.method).toBe("DELETE");
        expect(new URL(request.url).pathname).toBe(`/v1/terrascale/organizations/${config.project}/projects/${config.project}`);
        return new Response(null, { status });
      },
    });
    const result = await client.deleteProject(config.project, config.project);
    expect(result).toMatchObject({ ok: true, status, value: undefined });
    expect(JSON.stringify(result)).not.toContain(config.token);
  }
});

test("empty-body acceptance is limited to the exact named DELETE project closure at 202", async () => {
  const path = `v1/terrascale/organizations/${config.project}/projects/${config.project}`;
  for (const change of [
    { status: 200, method: "DELETE", path, routeName: "tsauth.platform.delete_project" },
    { status: 202, method: "GET", path, routeName: "tsauth.platform.delete_project" },
    { status: 202, method: "DELETE", path: `${path}/members`, routeName: "tsauth.platform.delete_project" },
    { status: 202, method: "DELETE", path, routeName: "tsauth.organizations.delete" },
  ]) {
    const http = new TsAuthHttp({ apiBaseUrl: identityConfig.apiBaseUrl, accessToken: config.token,
      fetch: async () => new Response(null, { status: change.status }) });
    const result = await http.request(/** @type {import("./http.js").TsAuthRequest<unknown>} */ (change));
    expect(result).toMatchObject({ ok: false, status: change.status, error: { code: "invalid_response" } });
  }
});

test("identity mutation denial never refreshes the provider or replays the mutation", async () => {
  let credentials = 0;
  let requests = 0;
  const client = createTerraScaleIdentityManagementClient({ ...identityConfig,
    signInToken: () => { credentials++; return `test.payload${credentials}.signature`; },
    fetch: async () => { requests++; return new Response(JSON.stringify({ error: { code: "unauthorized", message: "Denied" } }),
      { status: 401, headers: { "content-type": "application/json" } }); },
  });
  expect(await client.createOrganization({ name: "Example" })).toMatchObject({ ok: false, status: 401 });
  expect(credentials).toBe(1);
  expect(requests).toBe(1);
});

test("identity is a separate sign-in client with exactly the 50 pinned sign-in operations", () => {
  const client = createTerraScaleIdentityManagementClient(identityConfig);
  const names = Object.entries(platformIdentityRoutes).filter(([, route]) => route.security.some(scheme => "platformUser" in scheme)).map(([operation]) => operation.replace(/_([a-z])/gu, (_match, letter) => letter.toUpperCase()));
  expect(names).toHaveLength(50);
  expect(Object.keys(client).sort()).toEqual(names.sort());
  expect(Object.isFrozen(client)).toBe(true);
  expect(JSON.stringify(client)).toBe("{}");
  expect(() => createTerraScaleIdentityManagementClient(/** @type {any} */ (config))).toThrow(TypeError);
  expect(() => createTerraScaleIdentityManagementClient({ ...identityConfig, apiBaseUrl: "https://identity.example.invalid/path" })).toThrow(TypeError);
  expect(() => createTerraScaleIdentityManagementClient({ ...identityConfig, signInToken: "invalid" })).toThrow(TypeError);
});

test("identity methods reject invalid bodies and selectors before reaching transport", async () => {
  const client = createTerraScaleIdentityManagementClient(identityConfig);
  expect(() => client.listOrganizations({ limit: 101 })).toThrow(TypeError);
  expect(() => client.createOrganization({ name: "\n" })).toThrow(TypeError);
  expect(() => client.setProjectMemberRole(config.project, config.project, config.project, /** @type {any} */ ({ role: "owner" }))).toThrow(TypeError);
  expect(() => client.changeMemberRole(config.project, config.project, /** @type {any} */ ({ role: "invalid" }))).toThrow(TypeError);
  expect(() => client.createOrganizationApiKey(config.project, { name: "test", scopes: [] })).toThrow(TypeError);
  expect(() => client.createOrganizationApiKey(config.project, /** @type {any} */ ({ name: "test", scopes: ["documents:read"], database: "app" }))).toThrow(TypeError);
  expect(() => client.createProjectApiKey(config.project, config.project, { name: "test", scopes: ["documents:read"], database: "app" })).toThrow(TypeError);
  expect(() => client.rotateProjectApiKey(config.project, config.project, config.project, { previous_secret_ttl: 86401 })).toThrow(TypeError);
  await expect(client.getProject(config.project, "../other")).rejects.toThrow(TypeError);
  await expect(client.listOrganizations({}, /** @type {any} */ ({ idempotencyKey: "unsupported" }))).rejects.toThrow(TypeError);
});

test("identity DTOs validate required fields, nullability, roles, ULIDs and one-time secrets", () => {
  const organizationBody = { id: config.project, name: "Example", created_by: config.project,
    created_at: "2026-10-26T00:00:00Z", role: null, require_mfa: false };
  expect(organization(organizationBody).role).toBeNull();
  expect(() => organization({ ...organizationBody, created_at: 0 })).toThrow(TypeError);
  expect(() => organization({ ...organizationBody, require_mfa: null })).toThrow(TypeError);
  expect(() => organization({ ...organizationBody, id: "invalid" })).toThrow(TypeError);
  expect(() => organization({ ...organizationBody, role: "superuser" })).toThrow(TypeError);
  const projectBody = { id: config.project, organization_id: config.project, name: "Example", issuer: null,
    created_by: config.project, created_at: organizationBody.created_at, role: "admin", allowed_cidrs: [] };
  expect(project(projectBody).issuer).toBeNull();
  expect(() => project({ ...projectBody, allowed_cidrs: null })).toThrow(TypeError);
  const key = { id: config.project, name: "Example", organization_id: config.project, project_id: null,
    database: null, scopes: ["documents:read"], created_by: config.project, created_at: organizationBody.created_at,
    expires_at: null, previous_secret_expires_at: null, last_used_at: null, allowed_cidrs: [] };
  expect(apiKey(key).last_used_at).toBeNull();
  expect(apiKey({ ...key, database: config.project }).database).toBe(config.project);
  expect(() => apiKey({ ...key, database: "app" })).toThrow(TypeError);
  expect(() => apiKey({ ...key, project_id: "invalid" })).toThrow(TypeError);
  expect(() => apiKey({ ...key, scopes: ["unpublished:scope"] })).toThrow(TypeError);
  const secret = `${config.project}_${"a".repeat(52)}`;
  expect(keySecret({ api_key: key, key: secret }).key).toBe(secret);
  expect(() => keySecret({ api_key: key, key: "invalid" })).toThrow(TypeError);
});

test("identity operation method/path/security/media/status parity uses the pinned owning OpenAPI", () => {
  expect(platformIdentityContract.openapiSha256).toBe("8208fee2cb1ec233cdce818c4e262d9b8abf0c256a5563f52fdf0578daaa9c2a");
  const operations = Object.entries(platformIdentityOpenApi.paths).flatMap(([path, methods]) =>
    Object.entries(methods).map(([method, operation]) => ({ path, method, operation })));
  expect(operations).toHaveLength(54);
  expect(Object.keys(platformIdentityRoutes)).toHaveLength(53);
  expect(operations.filter(item => item.operation.operationId === "email_admission")).toHaveLength(1);
  expect(Object.hasOwn(platformIdentityRoutes, "email_admission")).toBe(false);
  for (const [name, route] of Object.entries(platformIdentityRoutes)) {
    const published = operations.find(item => item.operation.operationId === name);
    expect(published, name).toBeDefined();
    if (published === undefined) throw new Error("Missing pinned operation.");
    const op = published.operation;
    expect(route.pathTemplate, name).toBe(published.path);
    expect(route.method, name).toBe(published.method.toUpperCase());
    expect(route.security, name).toEqual(op.security);
    expect(route.parameters, name).toEqual("parameters" in op ? op.parameters : []);
    expect(route.successStatuses, name).toEqual(Object.keys(op.responses).filter(code => code.startsWith("2")).map(Number));
    expect(route.idempotencyKey).toBe("none");
    const request = "requestBody" in op ? op.requestBody : undefined;
    expect(route.requestRequired, name).toBe(request?.required ?? false);
    expect(route.requestMediaTypes, name).toEqual(Object.keys(request?.content ?? {}));
    expect(route.requestSchema, name).toBe(request?.content["application/json"].schema.$ref ?? null);
    for (const [code, response] of Object.entries(op.responses)) {
      const content = "content" in response ? response.content : undefined;
      expect(Reflect.get(route.responses, code), `${name} ${code}`).toEqual({
        mediaTypes: Object.keys(content ?? {}), schema: content?.["application/json"].schema.$ref ?? null,
      });
    }
  }
  expect(platformIdentityOpenApi.components.schemas.ProjectBody.required).toContain("allowed_cidrs");
  expect(platformIdentityOpenApi.components.schemas.OrganizationBody.properties.role.type).toEqual(["null", "string"]);
});

test("management entrypoints construct the existing project-scoped named clients", () => {
  for (const create of [createTerraScaleManagementClient, requireTerraScaleManagement]) {
    const client = create(config);
    expect(client.databases).toBeInstanceOf(V1Databases);
    // A detached selector retains its root client's private project transport.
    const database = client.database;
    expect(database("app")).toBeInstanceOf(V1Database);
    expect(database("app").backups).toBeDefined();
    expect(database("app").imports).toBeDefined();
    expect(database("app").schema).toBeDefined();
    expect(database("app").indexes).toBeDefined();
    expect(Object.isFrozen(client)).toBe(true);
    expect(JSON.stringify(client)).not.toContain(config.token);
    expect(Object.keys(client).sort()).toEqual([
      "backupDestination", "database", "databases", "metrics", "savedQueries",
    ]);
  }
});

test("management preserves root project and resource validation", () => {
  expect(() => createTerraScaleManagementClient({ ...config, project: "invalid" })).toThrow(TypeError);
  const client = createTerraScaleManagementClient(config);
  expect(() => client.database("../other-project")).toThrow(TypeError);
  expect(() => createTerraScaleManagementClient({ ...config, token: "invalid" })).toThrow(TypeError);
});

test("administration exposes only the public role-protected explore client", () => {
  for (const create of [createTerraScaleAdministrationClient, requireTerraScaleAdministration]) {
    const client = create({ baseUrl: config.baseUrl, token: config.token });
    expect(client).toBeInstanceOf(V1Platform);
    expect(Object.getOwnPropertyNames(V1Platform.prototype).sort()).toEqual([
      "collections", "constructor", "databases", "document", "documents",
    ]);
    expect(Object.isFrozen(client)).toBe(true);
    expect(JSON.stringify(client)).not.toContain(config.token);
  }
});

test("management/admin reuse the producer-published route and scope manifest", async () => {
  const manifest = JSON.parse(await readFile(new URL("../sdk-public-v1-route-manifest.json", import.meta.url), "utf8"));
  const exposed = [...v1Routes, ...managementV1Routes].filter(route => route.permission === "platform-admin" ||
    route.permission === "databases:read" || route.permission === "databases:write");
  for (const route of exposed) {
    expect(manifest.routes.find(/** @param {{method: string; path: string}} item */ item =>
      item.method === route.method && item.path === route.pathTemplate), route.operation).toMatchObject({
      method: route.method, path: route.pathTemplate,
      scopes: [route.permission, ...(route.additionalPermissions ?? [])],
      requestMediaTypes: route.requestMediaTypes,
      statuses: route.successStatuses,
      responseMediaType: route.responseMediaType,
    });
    expect(route.idempotencyKey).toBe("none");
  }
});

test("project API key creation sends a stable database ULID and listing preserves it", async () => {
  const database = "01ARZ3NDEKTSV4RRFFQ69G5FA2";
  const key = { id: config.project, name: "test", organization_id: config.project, project_id: config.project,
    database, scopes: ["documents:read"], created_by: config.project, created_at: "2026-10-26T00:00:00Z",
    expires_at: null, previous_secret_expires_at: null, last_used_at: null, allowed_cidrs: [] };
  const client = createTerraScaleIdentityManagementClient({ ...identityConfig, fetch: async input => {
    const request = /** @type {Request} */ (input);
    expect(new URL(request.url).pathname).toBe(`/v1/terrascale/organizations/${config.project}/projects/${config.project}/api-keys`);
    if (request.method === "POST") {
      expect(await request.json()).toEqual({ name: "test", scopes: ["documents:read"], database });
      return Response.json({ api_key: key, key: `${config.project}_${"a".repeat(52)}` }, { status: 201 });
    }
    expect(request.method).toBe("GET");
    return Response.json({ items: [key], next: null });
  } });
  expect(await client.createProjectApiKey(config.project, config.project, { name: "test", scopes: ["documents:read"], database }))
    .toMatchObject({ ok: true, value: { api_key: { database } } });
  expect(await client.listProjectApiKeys(config.project, config.project))
    .toMatchObject({ ok: true, value: { items: [{ database }] } });
});
