import assert from "node:assert/strict";
import { createHmac } from "node:crypto";
import { describe, test } from "vitest";

import { createTsAuthClient, TsAuthUserPatch, verifyTsAuthWebhook, verifyTsAuthWebhookWithTolerance } from "./ts-auth.js";

const user = {
  id: "usr_1",
  email: "ada@acme.test",
  email_verified: true,
  username: "ada",
  name: "Ada",
  banned: false,
  created_at: 1_788_220_800,
  public_metadata: {},
};

const discoveryDoc = {
  issuer: "https://auth.acme.test/acme",
  authorization_endpoint: "https://auth.acme.test/acme/oauth/authorize",
  token_endpoint: "https://auth.acme.test/acme/oauth/token",
  userinfo_endpoint: "https://auth.acme.test/acme/oauth/userinfo",
  jwks_uri: "https://auth.acme.test/acme/.well-known/jwks.json",
  revocation_endpoint: "https://auth.acme.test/acme/oauth/revoke",
  introspection_endpoint: "https://auth.acme.test/acme/oauth/introspect",
  end_session_endpoint: "https://auth.acme.test/acme/oauth/end-session",
  response_types_supported: ["code"],
  grant_types_supported: ["authorization_code", "refresh_token", "client_credentials"],
  code_challenge_methods_supported: ["S256"],
  subject_types_supported: ["public"],
  id_token_signing_alg_values_supported: ["ES256"],
  token_endpoint_auth_methods_supported: ["client_secret_post"],
  scopes_supported: ["openid"],
};

function json(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: { "content-type": "application/json" } });
}

function capture(requests: Request[], respond: (request: Request, index: number) => Response): typeof fetch {
  return async (input) => {
    const request = new Request(input);
    requests.push(request.clone());
    return respond(request, requests.length - 1);
  };
}

function tsAuthClient(respond: (request: Request, index: number) => Response, requests: Request[], overrides: Record<string, unknown> = {}) {
  return createTsAuthClient({
    apiBaseUrl: "https://auth.acme.test",
    accessToken: "sk_test_management_key",
    instanceSlug: "acme",
    fetch: capture(requests, respond),
    ...overrides,
  });
}

describe("ts-auth client", () => {
  test("public auth origin selects a regional endpoint and fails over safe reads", async () => {
    const requests: Request[] = [];
    const tsAuth = tsAuthClient((request) => {
      const host = new URL(request.url).hostname;
      if (host === "atlas.terrascale.tech") return json({
        v: 1, refreshSeconds: 60, endpoints: [
          { region: "SFO", url: "https://sfo.auth.terrascale.tech" },
          { region: "GRU", url: "https://gru.auth.terrascale.tech" },
        ],
      });
      return host === "sfo.auth.terrascale.tech" ? json({ error: { code: "unavailable" } }, 503) : json({ data: [user] });
    }, requests, { apiBaseUrl: "https://auth.terrascale.tech" });

    const result = await tsAuth.users.list();
    assert.equal(result.ok, true);
    assert.deepEqual(requests.map(request => new URL(request.url).hostname), [
      "atlas.terrascale.tech", "sfo.auth.terrascale.tech", "gru.auth.terrascale.tech",
    ]);
    assert.equal(requests[0]!.headers.get("authorization"), null);
    assert.equal(requests[1]!.headers.get("authorization"), "Bearer sk_test_management_key");
    await tsAuth.users.list();
    assert.equal(requests.filter(request => new URL(request.url).hostname === "atlas.terrascale.tech").length, 1);
  });

  test("public auth mutations are sent once and proxy mode skips discovery", async () => {
    const directRequests: Request[] = [];
    const direct = tsAuthClient((request) => new URL(request.url).hostname === "atlas.terrascale.tech"
      ? json({ v: 1, refreshSeconds: 60, endpoints: [
        { region: "SFO", url: "https://sfo.auth.terrascale.tech" },
        { region: "GRU", url: "https://gru.auth.terrascale.tech" },
      ] })
      : json({ error: { code: "unavailable" } }, 503), directRequests, { apiBaseUrl: "https://auth.terrascale.tech" });
    const result = await direct.users.create({ email: "a@b.test" });
    assert.equal(result.ok, false);
    assert.deepEqual(directRequests.map(request => new URL(request.url).hostname), ["atlas.terrascale.tech", "sfo.auth.terrascale.tech"]);

    const proxyRequests: Request[] = [];
    const proxy = tsAuthClient(() => json({ data: [user] }), proxyRequests, {
      apiBaseUrl: "https://auth.terrascale.tech", useReverseProxy: true,
    });
    await proxy.users.list();
    assert.deepEqual(proxyRequests.map(request => new URL(request.url).hostname), ["auth.terrascale.tech"]);
  });

  test("auth discovery rejects a mismatched regional host before sending credentials", async () => {
    const requests: Request[] = [];
    const tsAuth = tsAuthClient(() => json({ v: 1, refreshSeconds: 60, endpoints: [
      { region: "SFO", url: "https://gru.auth.terrascale.tech" },
    ] }), requests, { apiBaseUrl: "https://auth.terrascale.tech" });
    const result = await tsAuth.users.list();
    assert.equal(result.ok, false);
    assert.deepEqual(requests.map(request => new URL(request.url).hostname), ["atlas.terrascale.tech"]);
    assert.equal(requests[0]!.headers.get("authorization"), null);
  });

  test("sends exact management routes, bearer, and JSON bodies", async () => {
    const requests: Request[] = [];
    const tsAuth = tsAuthClient(() => json({ data: [user] }), requests);

    const listed = await tsAuth.users.list({ limit: 100, startingAfter: "usr_1" });
    assert.equal(listed.ok, true);
    assert.equal(new URL(requests[0]!.url).pathname, "/v1/acme/users");
    assert.equal(new URL(requests[0]!.url).search, "?limit=100&starting_after=usr_1");
    assert.equal(requests[0]!.method, "GET");
    assert.equal(requests[0]!.headers.get("authorization"), "Bearer sk_test_management_key");
    assert.equal(requests[0]!.headers.get("accept"), "application/json");
    assert.equal(requests[0]!.headers.get("content-type"), null);
    if (listed.ok) assert.equal(listed.value.data[0]?.id, "usr_1");

    const created = await tsAuth.users.create({ email: "grace@acme.test", password: "hunter2000" });
    assert.equal(created.ok, true);
    assert.equal(new URL(requests[1]!.url).pathname, "/v1/acme/users");
    assert.equal(requests[1]!.method, "POST");
    assert.equal(requests[1]!.headers.get("content-type"), "application/json");
    assert.deepEqual(JSON.parse(await requests[1]!.text()), { email: "grace@acme.test", password: "hunter2000" });

    const revoked = await tsAuth.sessions.revoke("ses_9");
    assert.equal(revoked.ok, true);
    assert.equal(new URL(requests[2]!.url).pathname, "/v1/acme/sessions/ses_9/revoke");
    assert.equal(requests[2]!.method, "POST");

    const deleted = await tsAuth.apiKeys.delete("key_7");
    assert.equal(deleted.ok, true);
    assert.equal(new URL(requests[3]!.url).pathname, "/v1/acme/api-keys/key_7");
    assert.equal(requests[3]!.method, "DELETE");
  });

  test("sends the tri-state user patch wire body exactly", async () => {
    const requests: Request[] = [];
    const tsAuth = tsAuthClient(() => json(user), requests);

    const patch = new TsAuthUserPatch()
      .withName("Ada Lovelace")
      .unsetUsername()
      .withPassword("new-password-1")
      .withBanned(true)
      .withPublicMetadata({ tier: "pro" });
    const updated = await tsAuth.users.update("usr_1", patch);
    assert.equal(updated.ok, true);
    assert.equal(new URL(requests[0]!.url).pathname, "/v1/acme/users/usr_1");
    assert.equal(requests[0]!.method, "PATCH");
    assert.deepEqual(JSON.parse(await requests[0]!.text()), {
      name: "Ada Lovelace",
      username: null,
      password: "new-password-1",
      banned: true,
      public_metadata: { tier: "pro" },
    });
  });

  test("decodes per-family casings and envelope shapes", async () => {
    const requests: Request[] = [];
    const wireOauthClient = {
      client: {
        id: "con_1",
        clientId: "clt_1",
        name: "Dashboard",
        redirectUris: ["https://app.acme.test/cb"],
        tokenEndpointAuthMethod: "client_secret_post",
        public: false,
        createdAt: 1_788_220_800,
      },
      client_secret: "cs_one_time",
    };
    const tsAuth = tsAuthClient((request) => {
      const path = new URL(request.url).pathname;
      if (path === "/v1/acme/oauth-clients") return json(wireOauthClient);
      if (path === "/v1/acme/users/usr_1/passkeys") {
        return json({ passkeys: [{ id: "psk_1", nickname: "YubiKey", algorithm: "ES256", createdAt: 1_788_220_800, lastUsedAt: null }] });
      }
      if (path === "/v1/acme/users/usr_1/mfa-factors") {
        return json({ data: [{ id: "fac_1", type: "totp", confirmed: true, createdAt: 1_788_220_800 }] });
      }
      if (path === "/v1/acme/organizations") {
        return json([{ id: "org_1", name: "Acme", slug: "acme", created_at: 1_788_220_800 }]);
      }
      return json({});
    }, requests);

    const oauthClient = await tsAuth.oauthClients.create({ name: "Dashboard", redirect_uris: ["https://app.acme.test/cb"] });
    assert.equal(oauthClient.ok, true);
    if (oauthClient.ok) assert.deepEqual(oauthClient.value, wireOauthClient);

    const listedClients = await tsAuth.oauthClients.list();
    assert.equal(listedClients.ok, true);

    const passkeys = await tsAuth.users.passkeys("usr_1");
    assert.equal(passkeys.ok, true);
    if (passkeys.ok) {
      assert.deepEqual(passkeys.value.passkeys[0], {
        id: "psk_1", nickname: "YubiKey", algorithm: "ES256", createdAt: 1_788_220_800, lastUsedAt: null,
      });
    }

    const factors = await tsAuth.users.mfaFactors("usr_1");
    assert.equal(factors.ok, true);
    if (factors.ok) assert.equal(factors.value.data[0]?.createdAt, 1_788_220_800);

    const organizations = await tsAuth.organizations.list();
    assert.equal(organizations.ok, true);
    if (organizations.ok) assert.deepEqual(organizations.value, [
      { id: "org_1", name: "Acme", slug: "acme", created_at: 1_788_220_800 },
    ]);
  });

  test("maps nested management and flat OAuth error envelopes", async () => {
    const nestedRequests: Request[] = [];
    const nested = tsAuthClient(() =>
      json({ error: { code: "user_not_found", message: "No such user." } }, 404), nestedRequests);
    const missing = await nested.users.get("usr_missing");
    assert.equal(missing.ok, false);
    if (!missing.ok) {
      assert.equal(missing.status, 404);
      assert.equal(missing.error.kind, "not-found");
      assert.equal(missing.error.code, "user_not_found");
      assert.equal(missing.error.message, "No such user.");
    }

    const flatRequests: Request[] = [];
    const flat = tsAuthClient(() =>
      json({ error: "invalid_client", error_description: "Client authentication failed." }, 401), flatRequests);
    const token = await flat.oidc.token("clt_1", "cs_wrong", { grant_type: "client_credentials" });
    assert.equal(token.ok, false);
    if (!token.ok) {
      assert.equal(token.status, 401);
      assert.equal(token.error.kind, "authentication");
      assert.equal(token.error.code, "invalid_client");
      assert.equal(token.error.message, "Client authentication failed.");
    }
  });

  test("retries safe reads once and never retries mutations", async () => {
    const readRequests: Request[] = [];
    const retried = tsAuthClient((_request, index) =>
      index === 0 ? json({ error: { code: "unavailable", message: "Try again." } }, 503) : json({ data: [user] }),
      readRequests, { maxRetryAttempts: 1, retryDelayMs: 0 });
    const listed = await retried.users.list();
    assert.equal(listed.ok, true);
    assert.equal(readRequests.length, 2);

    const mutationRequests: Request[] = [];
    const failing = tsAuthClient(() =>
      json({ error: { code: "unavailable", message: "Try again." } }, 503),
      mutationRequests, { maxRetryAttempts: 1, retryDelayMs: 0 });
    const created = await failing.apiKeys.create({ name: "ci" });
    assert.equal(created.ok, false);
    if (!created.ok) assert.equal(created.error.kind, "server");
    assert.equal(mutationRequests.length, 1);
  });

  test("returns one-time secrets from creating calls", async () => {
    const requests: Request[] = [];
    const tsAuth = tsAuthClient((request) => {
      const path = new URL(request.url).pathname;
      if (path === "/v1/acme/api-keys") return json({ id: "key_1", name: "ci", api_key: "sk_one_time_bearer", scopes: ["users:read"] }, 201);
      if (path === "/v1/acme/webhooks") return json({ id: "wh_1", url: "https://hooks.acme.test/ts", events: ["user.*"], signing_secret: "whsec_one_time_signing_secret_32chars" });
      if (path === "/v1/acme/directories") return json({ id: "dir_1", org_id: null, token: "scim_one_time_token" }, 201);
      return json({});
    }, requests);

    const key = await tsAuth.apiKeys.create({ name: "ci" });
    assert.equal(key.ok, true);
    if (key.ok) assert.equal(key.value.api_key, "sk_one_time_bearer");

    const webhook = await tsAuth.webhooks.create({ url: "https://hooks.acme.test/ts", events: ["user.*"] });
    assert.equal(webhook.ok, true);
    if (webhook.ok) assert.equal(webhook.value.signing_secret, "whsec_one_time_signing_secret_32chars");

    const directory = await tsAuth.directories.create({});
    assert.equal(directory.ok, true);
    if (directory.ok) assert.equal(directory.value.token, "scim_one_time_token");
  });

  test("walks the user pagination cursor across pages", async () => {
    const requests: Request[] = [];
    const pages = [
      { data: [{ ...user, id: "usr_1" }, { ...user, id: "usr_2" }] },
      { data: [{ ...user, id: "usr_3" }] },
    ];
    const tsAuth = tsAuthClient((_request, index) => json(pages[index] ?? { data: [] }), requests);

    const all = await tsAuth.users.listAll({ pageSize: 2 });
    assert.equal(all.ok, true);
    if (all.ok) assert.deepEqual(all.value.map(u => u.id), ["usr_1", "usr_2", "usr_3"]);
    assert.equal(requests.length, 2);
    assert.equal(new URL(requests[0]!.url).search, "?limit=2");
    assert.equal(new URL(requests[1]!.url).search, "?limit=2&starting_after=usr_2");
  });

  test("verifies webhook signature vectors including tampering and tolerance", async () => {
    const secret = `whsec_${"k".repeat(40)}`;
    const timestamp = "1758340000";
    const payload = new TextEncoder().encode(JSON.stringify({ id: "evt_1", type: "user.created", created_at: Number(timestamp), data: {} }));
    const mac = createHmac("sha256", secret).update(`${timestamp}.`).update(payload).digest("hex");
    const signature = `sha256=${mac}`;

    assert.equal(await verifyTsAuthWebhook(secret, timestamp, signature, payload), true);
    assert.equal(await verifyTsAuthWebhook(secret, timestamp, signature, new TextEncoder().encode('{"id":"evt_2"}')), false);
    assert.equal(await verifyTsAuthWebhook("whsec_wrong_key", timestamp, signature, payload), false);
    assert.equal(await verifyTsAuthWebhook(secret, timestamp, "sha256=zz-not-hex", payload), false);
    assert.equal(await verifyTsAuthWebhook(secret, undefined, signature, payload), false);
    assert.equal(await verifyTsAuthWebhook(secret, "1758340000.9", signature, payload), false);

    const now = Number(timestamp) * 1_000;
    assert.equal(await verifyTsAuthWebhookWithTolerance(secret, timestamp, signature, payload, 60_000, now + 30_000), true);
    assert.equal(await verifyTsAuthWebhookWithTolerance(secret, timestamp, signature, payload, 60_000, now + 61_000), false);
  });

  test("rejects invalid slugs and empty patches synchronously", async () => {
    const fetch = async (): Promise<Response> => json({});
    for (const instanceSlug of ["", " ", "ac me", "ac/me", "ac\u0007me"]) {
      assert.throws(
        () => createTsAuthClient({ apiBaseUrl: "https://auth.acme.test", accessToken: "sk_x", instanceSlug, fetch }),
        /instanceSlug/u,
        JSON.stringify(instanceSlug),
      );
    }
    const tsAuth = createTsAuthClient({ apiBaseUrl: "https://auth.acme.test", accessToken: "sk_x", instanceSlug: "acme", fetch });
    assert.throws(() => tsAuth.users.update("usr_1", new TsAuthUserPatch()), /patch is empty/u);
    assert.throws(() => tsAuth.users.list({ limit: 0 }), /between 1 and 100/u);
    assert.throws(() => tsAuth.users.get("usr\u0000"), /user id/u);
  });

  test("posts OIDC form bodies and keeps public discovery routes anonymous", async () => {
    const requests: Request[] = [];
    const tsAuth = tsAuthClient((request) => {
      const path = new URL(request.url).pathname;
      if (path === "/acme/oauth/token") return json({ access_token: "at_1", token_type: "Bearer", expires_in: 3600 });
      if (path === "/acme/oauth/userinfo") return json({ sub: "usr_1", email: "ada@acme.test" });
      if (path === "/acme/oauth/revoke") return new Response(null, { status: 200 });
      if (path === "/acme/.well-known/openid-configuration") return json(discoveryDoc);
      if (path === "/acme/.well-known/jwks.json") {
        return json({ keys: [{ kty: "EC", crv: "P-256", alg: "ES256", use: "sig", kid: "k1", x: "eX", y: "eY" }] });
      }
      return json({});
    }, requests);

    const token = await tsAuth.oidc.token("clt_1", "cs_1", {
      grant_type: "authorization_code",
      code: "c1",
      redirect_uri: "https://app.acme.test/cb",
      code_verifier: "v1",
    });
    assert.equal(token.ok, true);
    assert.equal(new URL(requests[0]!.url).pathname, "/acme/oauth/token");
    assert.equal(requests[0]!.headers.get("content-type"), "application/x-www-form-urlencoded");
    assert.equal(requests[0]!.headers.get("authorization"), null);
    assert.equal(
      await requests[0]!.text(),
      "client_id=clt_1&client_secret=cs_1&grant_type=authorization_code&code=c1&redirect_uri=https%3A%2F%2Fapp.acme.test%2Fcb&code_verifier=v1",
    );

    const discovery = await tsAuth.oidc.discovery();
    assert.equal(discovery.ok, true);
    if (discovery.ok) assert.equal(discovery.value.token_endpoint_auth_methods_supported[0], "client_secret_post");
    assert.equal(new URL(requests[1]!.url).pathname, "/acme/.well-known/openid-configuration");
    assert.equal(requests[1]!.headers.get("authorization"), null);

    const jwks = await tsAuth.oidc.jwks();
    assert.equal(jwks.ok, true);
    assert.equal(new URL(requests[2]!.url).pathname, "/acme/.well-known/jwks.json");
    assert.equal(requests[2]!.headers.get("authorization"), null);

    const revoked = await tsAuth.oidc.revoke("clt_1", "cs_1", "at_1");
    assert.equal(revoked.ok, true);
    assert.equal(new URL(requests[3]!.url).pathname, "/acme/oauth/revoke");
    assert.equal(requests[3]!.headers.get("content-type"), "application/x-www-form-urlencoded");
    assert.equal(await requests[3]!.text(), "client_id=clt_1&client_secret=cs_1&token=at_1");

    const userinfo = await tsAuth.oidc.userinfo("at_1");
    assert.equal(userinfo.ok, true);
    if (userinfo.ok) assert.equal(userinfo.value.sub, "usr_1");
    assert.equal(new URL(requests[4]!.url).pathname, "/acme/oauth/userinfo");
    assert.equal(requests[4]!.headers.get("authorization"), "Bearer at_1");
  });

  test("sends plan family routes and instance patches on their published paths", async () => {
    const requests: Request[] = [];
    const tsAuth = tsAuthClient((request) => {
      const path = new URL(request.url).pathname;
      if (path === "/v1/acme/billing") return json({ plan: "pro", status: "active", user_count: 3, quota: 10 });
      if (path === "/v1/acme/billing/checkout") return json({ checkout_url: "https://checkout.stripe.test/1" });
      return json({ id: "ins_1", slug: "acme", name: "Acme" });
    }, requests);

    const overview = await tsAuth.billing.get();
    assert.equal(overview.ok, true);
    if (overview.ok) assert.equal(overview.value.plan, "pro");
    assert.equal(new URL(requests[0]!.url).pathname, "/v1/acme/billing");

    const checkout = await tsAuth.billing.checkout({ plan: "pro" });
    assert.equal(checkout.ok, true);
    assert.equal(new URL(requests[1]!.url).pathname, "/v1/acme/billing/checkout");
    assert.deepEqual(JSON.parse(await requests[1]!.text()), { plan: "pro" });

    const updated = await tsAuth.instance.update({ name: "Acme Labs" });
    assert.equal(updated.ok, true);
    assert.equal(new URL(requests[2]!.url).pathname, "/v1/acme/instance");
    assert.deepEqual(JSON.parse(await requests[2]!.text()), { name: "Acme Labs" });
  });
});
