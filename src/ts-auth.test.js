import assert from "node:assert/strict";
import { createHmac } from "node:crypto";
import { describe, test } from "vitest";

import {
  createTsAuthClient,
  TsAuthUserPatch,
  verifyTsAuthWebhook,
  verifyTsAuthWebhookWithTolerance,
} from "./ts-auth.js";
/** @import { TsAuthClient } from "./ts-auth.js" */

const user = {
  id: "01ARZ3NDEKTSV4RRFFQ6000011",
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
  grant_types_supported: [
    "authorization_code",
    "refresh_token",
    "client_credentials",
  ],
  code_challenge_methods_supported: ["S256"],
  subject_types_supported: ["public"],
  id_token_signing_alg_values_supported: ["ES256"],
  token_endpoint_auth_methods_supported: ["client_secret_post"],
  scopes_supported: ["openid"],
};

/**
 * @param {unknown} value
 * @param {number} [status=200]
 * @returns {Response}
 */
function json(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "content-type": "application/json" },
  });
}

/**
 * @param {Request[]} requests
 * @param {(request: Request, index: number) => Response} respond
 * @returns {typeof fetch}
 */
function capture(requests, respond) {
  return async (input) => {
    const request = new Request(input);
    requests.push(request.clone());
    return respond(request, requests.length - 1);
  };
}

/**
 * @param {(request: Request, index: number) => Response} respond
 * @param {Request[]} requests
 * @param {Record<string, unknown>} [overrides={}]
 * @returns {TsAuthClient}
 */
function tsAuthClient(respond, requests, overrides = {}) {
  return createTsAuthClient({
    apiBaseUrl: "https://auth.acme.test",
    accessToken: "sk_test_management_key",
    instanceSlug: "acme",
    fetch: capture(requests, respond),
    ...overrides,
  });
}

test("dead-letter list and replay use the documented two-ULID delivery selector", async () => {
  const endpoint = "01ARZ3NDEKTSV4RRFFQ6000001";
  const event = "01ARZ3NDEKTSV4RRFFQ6000002";
  const entry = { id: `${endpoint}:${event}`, endpoint_id: endpoint, event_id: event, attempts: 3, updated_at: 1 };
  /** @type {Request[]} */
  const requests = [];
  const client = tsAuthClient((_request, index) => index === 0 ? json([entry]) : json({ replayed: true }), requests);
  const page = await client.webhooks.deadLetter();
  assert.equal(page.ok, true);
  if (page.ok) assert.deepEqual(page.value, [entry]);
  const replay = await client.webhooks.replayDeadLetter(entry.id);
  assert.equal(replay.ok, true);
  assert.equal(requests[1]?.method, "POST");
  assert.equal(new URL(/** @type {Request} */ (requests[1]).url).pathname, `/v1/acme/webhooks/dead-letter/${endpoint}%3A${event}/replay`);
  for (const invalid of [endpoint, `${endpoint}:event_1`, entry.id.toLowerCase(), `${entry.id}:${event}`])
    assert.throws(() => client.webhooks.replayDeadLetter(invalid), /ULID/u);
  assert.equal(requests.length, 2);
});

test("malformed entity pages return invalid-response while recovery-code pages succeed", async () => {
  for (const entry of [null, "legacy-id", 1, true, []]) {
    const client = tsAuthClient(() => json({ data: [entry] }), []);
    const result = await client.users.list();
    assert.equal(result.ok, false);
    if (!result.ok) assert.equal(result.error.kind, "invalid-response");
  }
  const client = tsAuthClient(() => json({ data: ["recovery-code"] }), []);
  const result = await client.users.regenerateRecoveryCodes(user.id, "01ARZ3NDEKTSV4RRFFQ6000001");
  assert.equal(result.ok, true);
  if (result.ok) assert.deepEqual(result.value.data, ["recovery-code"]);
});

test("nullable membership relationships succeed while non-null relationships require IDs", async () => {
  const id = "01ARZ3NDEKTSV4RRFFQ6000001";
  for (const related of [null, { id }, {}, { id: id.toLowerCase() }, "legacy-id", []]) {
    const client = tsAuthClient(request => json(new URL(request.url).pathname.endsWith("/memberships")
      ? [{ id, organization_id: id, user_id: user.id, role: "member", created_at: 1, user: related }]
      : [{ id, role: "member", created_at: 1, organization: related }]), []);
    const organizations = await client.users.organizations(user.id);
    const memberships = await client.organizations.listMemberships(id);
    const valid = related === null || (typeof related === "object" && !Array.isArray(related) && Reflect.get(related, "id") === id);
    assert.equal(organizations.ok, valid);
    assert.equal(memberships.ok, valid);
    if (!organizations.ok) assert.equal(organizations.error.kind, "invalid-response");
    if (!memberships.ok) assert.equal(memberships.error.kind, "invalid-response");
    if (related === null && organizations.ok && memberships.ok) {
      assert.equal(organizations.value[0]?.organization, null);
      assert.equal(memberships.value[0]?.user, null);
    }
  }
});

test("single organization and instance responses without IDs return invalid-response", async () => {
  for (const body of [{}, null, [], "legacy-id", { id: "org_legacy" }]) {
    const client = tsAuthClient(() => json(body), []);
    for (const result of [await client.organizations.get(user.id), await client.instance.get()]) {
      assert.equal(result.ok, false);
      if (!result.ok) assert.equal(result.error.kind, "invalid-response");
    }
  }
});

test("enrollment and userinfo reject malformed successful response shapes", async () => {
  for (const body of [null, "legacy-subject", 1, false, [], [{ id: user.id, org_id: user.id, sub: user.id }], {},
    { org_id: null, sub: null }, { org_id: "org_legacy", sub: "legacy-subject" }]) {
    const client = tsAuthClient(() => json(body), []);
    for (const result of [await client.organizations.enrollDomain("acme.test", { user_id: user.id }),
      await client.oidc.userinfo("at_test")]) {
      assert.equal(result.ok, false);
      if (!result.ok) assert.equal(result.error.kind, "invalid-response");
    }
  }
});

test("missing membership, invitation and audit references return invalid-response", async () => {
  const membership = { id: user.id, organization_id: user.id, user_id: user.id, role: "member", created_at: 1 };
  const invitation = { id: user.id, organization_id: user.id, email: "ada@acme.test", role: "member", status: "pending", expires_at: 2, created_at: 1 };
  const audit = { id: user.id, type: "user.created", data: {}, created_at: 1 };
  for (const field of ["organization_id", "user_id"]) {
    const body = Object.fromEntries(Object.entries(membership).filter(([key]) => key !== field));
    const client = tsAuthClient(() => json(body), []);
    const result = await client.organizations.createMembership(user.id, { user_id: user.id, role: "member" });
    assert.equal(result.ok, false);
    if (!result.ok) assert.equal(result.error.kind, "invalid-response");
  }
  const withoutOrganization = Object.fromEntries(Object.entries(invitation).filter(([key]) => key !== "organization_id"));
  const invitationClient = tsAuthClient(() => json(withoutOrganization), []);
  const auditClient = tsAuthClient(() => json({ data: [audit] }), []);
  for (const result of [await invitationClient.organizations.createInvitation(user.id, { email: "ada@acme.test", role: "member" }),
    await auditClient.auditEvents.list()]) {
    assert.equal(result.ok, false);
    if (!result.ok) assert.equal(result.error.kind, "invalid-response");
  }
});

describe("ts-auth client", () => {
  test("sends exact management routes, bearer, and JSON bodies", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient((_request, index) => json(index === 1 ? user : { data: [user] }), requests);

    const listed = await tsAuth.users.list({
      limit: 100,
      startingAfter: "01ARZ3NDEKTSV4RRFFQ6000011",
    });
    assert.equal(listed.ok, true);
    assert.equal(
      new URL(/** @type {Request} */ (requests[0]).url).pathname,
      "/v1/acme/users",
    );
    assert.equal(
      new URL(/** @type {Request} */ (requests[0]).url).search,
      "?limit=100&starting_after=01ARZ3NDEKTSV4RRFFQ6000011",
    );
    assert.equal(/** @type {Request} */ (requests[0]).method, "GET");
    assert.equal(
      /** @type {Request} */ (requests[0]).headers.get("authorization"),
      "Bearer sk_test_management_key",
    );
    assert.equal(
      /** @type {Request} */ (requests[0]).headers.get("accept"),
      "application/json",
    );
    assert.equal(
      /** @type {Request} */ (requests[0]).headers.get("content-type"),
      null,
    );
    if (listed.ok) assert.equal(listed.value.data[0]?.id, "01ARZ3NDEKTSV4RRFFQ6000011");

    const created = await tsAuth.users.create({
      email: "grace@acme.test",
      password: "hunter2000",
    });
    assert.equal(created.ok, true);
    assert.equal(
      new URL(/** @type {Request} */ (requests[1]).url).pathname,
      "/v1/acme/users",
    );
    assert.equal(/** @type {Request} */ (requests[1]).method, "POST");
    assert.equal(
      /** @type {Request} */ (requests[1]).headers.get("content-type"),
      "application/json",
    );
    assert.deepEqual(
      JSON.parse(await /** @type {Request} */ (requests[1]).text()),
      {
        email: "grace@acme.test",
        password: "hunter2000",
      },
    );

    const revoked = await tsAuth.sessions.revoke("01ARZ3NDEKTSV4RRFFQ6000010");
    assert.equal(revoked.ok, true);
    assert.equal(
      new URL(/** @type {Request} */ (requests[2]).url).pathname,
      "/v1/acme/sessions/01ARZ3NDEKTSV4RRFFQ6000010/revoke",
    );
    assert.equal(/** @type {Request} */ (requests[2]).method, "POST");

    const deleted = await tsAuth.apiKeys.delete("01ARZ3NDEKTSV4RRFFQ6000007");
    assert.equal(deleted.ok, true);
    assert.equal(
      new URL(/** @type {Request} */ (requests[3]).url).pathname,
      "/v1/acme/api-keys/01ARZ3NDEKTSV4RRFFQ6000007",
    );
    assert.equal(/** @type {Request} */ (requests[3]).method, "DELETE");
  });

  test("sends the tri-state user patch wire body exactly", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(() => json(user), requests);

    const patch = new TsAuthUserPatch()
      .withName("Ada Lovelace")
      .unsetUsername()
      .withPassword("new-password-1")
      .withBanned(true)
      .withPublicMetadata({ tier: "pro" });
    const updated = await tsAuth.users.update("01ARZ3NDEKTSV4RRFFQ6000011", patch);
    assert.equal(updated.ok, true);
    assert.equal(
      new URL(/** @type {Request} */ (requests[0]).url).pathname,
      "/v1/acme/users/01ARZ3NDEKTSV4RRFFQ6000011",
    );
    assert.equal(/** @type {Request} */ (requests[0]).method, "PATCH");
    assert.deepEqual(
      JSON.parse(await /** @type {Request} */ (requests[0]).text()),
      {
        name: "Ada Lovelace",
        username: null,
        password: "new-password-1",
        banned: true,
        public_metadata: { tier: "pro" },
      },
    );
  });

  test("decodes per-family casings and envelope shapes", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient((request) => {
      const path = new URL(request.url).pathname;
      if (path === "/v1/acme/users/01ARZ3NDEKTSV4RRFFQ6000011/passkeys") {
        return json({
          passkeys: [
            {
              id: "01ARZ3NDEKTSV4RRFFQ6000009",
              nickname: "YubiKey",
              algorithm: "ES256",
              createdAt: 1_788_220_800,
              lastUsedAt: null,
            },
          ],
        });
      }
      if (path === "/v1/acme/users/01ARZ3NDEKTSV4RRFFQ6000011/mfa-factors") {
        return json({
          data: [
            {
              id: "01ARZ3NDEKTSV4RRFFQ6000004",
              type: "totp",
              confirmed: true,
              createdAt: 1_788_220_800,
            },
          ],
        });
      }
      if (path === "/v1/acme/organizations") {
        return json([
          {
            id: "01ARZ3NDEKTSV4RRFFQ6000008",
            name: "Acme",
            slug: "acme",
            created_at: 1_788_220_800,
          },
        ]);
      }
      return json({});
    }, requests);

    const passkeys = await tsAuth.users.passkeys("01ARZ3NDEKTSV4RRFFQ6000011");
    assert.equal(passkeys.ok, true);
    if (passkeys.ok) {
      assert.deepEqual(passkeys.value.passkeys[0], {
        id: "01ARZ3NDEKTSV4RRFFQ6000009",
        nickname: "YubiKey",
        algorithm: "ES256",
        createdAt: 1_788_220_800,
        lastUsedAt: null,
      });
    }

    const factors = await tsAuth.users.mfaFactors("01ARZ3NDEKTSV4RRFFQ6000011");
    assert.equal(factors.ok, true);
    if (factors.ok)
      assert.equal(factors.value.data[0]?.createdAt, 1_788_220_800);

    const organizations = await tsAuth.organizations.list();
    assert.equal(organizations.ok, true);
    if (organizations.ok)
      assert.deepEqual(organizations.value, [
        { id: "01ARZ3NDEKTSV4RRFFQ6000008", name: "Acme", slug: "acme", created_at: 1_788_220_800 },
      ]);
  });

  test("maps nested management and flat OAuth error envelopes", async () => {
    /** @type {Request[]} */
    const nestedRequests = [];
    const nested = tsAuthClient(
      () =>
        json(
          { error: { code: "user_not_found", message: "No such user." } },
          404,
        ),
      nestedRequests,
    );
    const missing = await nested.users.get("01ARZ3NDEKTSV4RRFFQ6000014");
    assert.equal(missing.ok, false);
    if (!missing.ok) {
      assert.equal(missing.status, 404);
      assert.equal(missing.error.kind, "not-found");
      assert.equal(missing.error.code, "user_not_found");
      assert.equal(missing.error.message, "No such user.");
    }

    /** @type {Request[]} */

    const flatRequests = [];
    const flat = tsAuthClient(
      () =>
        json(
          {
            error: "invalid_client",
            error_description: "Client authentication failed.",
          },
          401,
        ),
      flatRequests,
    );
    const token = await flat.oidc.token("01ARZ3NDEKTSV4RRFFQ6000002", "cs_wrong", {
      grant_type: "client_credentials",
      resource: "https://api.example.test/management",
      scope: "management:read",
    });
    assert.equal(token.ok, false);
    if (!token.ok) {
      assert.equal(token.status, 401);
      assert.equal(token.error.kind, "authentication");
      assert.equal(token.error.code, "invalid_client");
      assert.equal(token.error.message, "Client authentication failed.");
    }
  });

  test("retries safe reads once and never retries mutations", async () => {
    /** @type {Request[]} */
    const readRequests = [];
    const retried = tsAuthClient(
      (_request, index) =>
        index === 0
          ? json({ error: { code: "unavailable", message: "Try again." } }, 503)
          : json({ data: [user] }),
      readRequests,
      { maxRetryAttempts: 1, retryDelayMs: 0 },
    );
    const listed = await retried.users.list();
    assert.equal(listed.ok, true);
    assert.equal(readRequests.length, 2);

    /** @type {Request[]} */

    const mutationRequests = [];
    const failing = tsAuthClient(
      () =>
        json({ error: { code: "unavailable", message: "Try again." } }, 503),
      mutationRequests,
      { maxRetryAttempts: 1, retryDelayMs: 0 },
    );
    const created = await failing.apiKeys.create({ name: "ci" });
    assert.equal(created.ok, false);
    if (!created.ok) assert.equal(created.error.kind, "server");
    assert.equal(mutationRequests.length, 1);
  });

  test("returns one-time secrets from creating calls", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient((request) => {
      const path = new URL(request.url).pathname;
      if (path === "/v1/acme/api-keys")
        return json(
          {
            id: "01ARZ3NDEKTSV4RRFFQ6000006",
            name: "ci",
            api_key: "sk_one_time_bearer",
            scopes: ["users:read"],
          },
          201,
        );
      if (path === "/v1/acme/webhooks")
        return json({
          id: "01ARZ3NDEKTSV4RRFFQ6000015",
          url: "https://hooks.acme.test/ts",
          events: ["user.*"],
          signing_secret: "whsec_one_time_signing_secret_32chars",
        });
      if (path === "/v1/acme/directories")
        return json(
          { id: "01ARZ3NDEKTSV4RRFFQ6000001", org_id: null, token: "scim_one_time_token" },
          201,
        );
      return json({});
    }, requests);

    const key = await tsAuth.apiKeys.create({ name: "ci" });
    assert.equal(key.ok, true);
    if (key.ok) assert.equal(key.value.api_key, "sk_one_time_bearer");

    const webhook = await tsAuth.webhooks.create({
      url: "https://hooks.acme.test/ts",
      events: ["user.*"],
    });
    assert.equal(webhook.ok, true);
    if (webhook.ok)
      assert.equal(
        webhook.value.signing_secret,
        "whsec_one_time_signing_secret_32chars",
      );

    const directory = await tsAuth.directories.create({});
    assert.equal(directory.ok, true);
    if (directory.ok)
      assert.equal(directory.value.token, "scim_one_time_token");
  });

  test("walks the user pagination cursor across pages", async () => {
    /** @type {Request[]} */
    const requests = [];
    const pages = [
      {
        data: [
          { ...user, id: "01ARZ3NDEKTSV4RRFFQ6000011" },
          { ...user, id: "01ARZ3NDEKTSV4RRFFQ6000012" },
        ],
      },
      { data: [{ ...user, id: "01ARZ3NDEKTSV4RRFFQ6000013" }] },
    ];
    const tsAuth = tsAuthClient(
      (_request, index) => json(pages[index] ?? { data: [] }),
      requests,
    );

    const all = await tsAuth.users.listAll({ pageSize: 2 });
    assert.equal(all.ok, true);
    if (all.ok)
      assert.deepEqual(
        all.value.map((u) => u.id),
        ["01ARZ3NDEKTSV4RRFFQ6000011", "01ARZ3NDEKTSV4RRFFQ6000012", "01ARZ3NDEKTSV4RRFFQ6000013"],
      );
    assert.equal(requests.length, 2);
    assert.equal(
      new URL(/** @type {Request} */ (requests[0]).url).search,
      "?limit=2",
    );
    assert.equal(
      new URL(/** @type {Request} */ (requests[1]).url).search,
      "?limit=2&starting_after=01ARZ3NDEKTSV4RRFFQ6000012",
    );
  });

  test("verifies webhook signature vectors including tampering and tolerance", async () => {
    const secret = `whsec_${"k".repeat(40)}`;
    const timestamp = "1758340000";
    const payload = new TextEncoder().encode(
      JSON.stringify({
        id: "01ARZ3NDEKTSV4RRFFQ6000002",
        type: "user.created",
        created_at: Number(timestamp),
        data: {},
      }),
    );
    const mac = createHmac("sha256", secret)
      .update(`${timestamp}.`)
      .update(payload)
      .digest("hex");
    const signature = `sha256=${mac}`;

    assert.equal(
      await verifyTsAuthWebhook(secret, timestamp, signature, payload),
      true,
    );
    assert.equal(
      await verifyTsAuthWebhook(
        secret,
        timestamp,
        signature,
        new TextEncoder().encode('{"id":"01ARZ3NDEKTSV4RRFFQ6000003"}'),
      ),
      false,
    );
    assert.equal(
      await verifyTsAuthWebhook(
        "whsec_wrong_key",
        timestamp,
        signature,
        payload,
      ),
      false,
    );
    assert.equal(
      await verifyTsAuthWebhook(
        secret,
        timestamp,
        "sha256=zz-not-hex",
        payload,
      ),
      false,
    );
    assert.equal(
      await verifyTsAuthWebhook(secret, undefined, signature, payload),
      false,
    );
    assert.equal(
      await verifyTsAuthWebhook(secret, "1758340000.9", signature, payload),
      false,
    );

    const now = Number(timestamp) * 1_000;
    assert.equal(
      await verifyTsAuthWebhookWithTolerance(
        secret,
        timestamp,
        signature,
        payload,
        60_000,
        now + 30_000,
      ),
      true,
    );
    assert.equal(
      await verifyTsAuthWebhookWithTolerance(
        secret,
        timestamp,
        signature,
        payload,
        60_000,
        now + 61_000,
      ),
      false,
    );
  });

  test("rejects invalid slugs and empty patches synchronously", async () => {
    /** @returns {Promise<Response>} */
    const fetch = async () => json({});
    for (const instanceSlug of ["", " ", "ac me", "ac/me", "ac\u0007me"]) {
      assert.throws(
        () =>
          createTsAuthClient({
            apiBaseUrl: "https://auth.acme.test",
            accessToken: "sk_x",
            instanceSlug,
            fetch,
          }),
        /instanceSlug/u,
        JSON.stringify(instanceSlug),
      );
    }
    const tsAuth = createTsAuthClient({
      apiBaseUrl: "https://auth.acme.test",
      accessToken: "sk_x",
      instanceSlug: "acme",
      fetch,
    });
    assert.throws(
      () => tsAuth.users.update("01ARZ3NDEKTSV4RRFFQ6000011", new TsAuthUserPatch()),
      /patch is empty/u,
    );
    assert.throws(() => tsAuth.users.list({ limit: 0 }), /between 1 and 100/u);
    assert.throws(() => tsAuth.users.get("usr\u0000"), /user id/u);
  });

  test("posts OIDC form bodies and keeps public discovery routes anonymous", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient((request) => {
      const path = new URL(request.url).pathname;
      if (path === "/acme/oauth/token")
        throw new Error("Token request probe; no provider response.");
      if (path === "/acme/oauth/userinfo")
        return json({ sub: "01ARZ3NDEKTSV4RRFFQ6000011", email: "ada@acme.test" });
      if (path === "/acme/oauth/revoke")
        return new Response(null, { status: 200 });
      if (path === "/acme/.well-known/openid-configuration")
        return json(discoveryDoc);
      if (path === "/acme/.well-known/jwks.json") {
        return json({
          keys: [
            {
              kty: "EC",
              crv: "P-256",
              alg: "ES256",
              use: "sig",
              kid: "k1",
              x: "eX",
              y: "eY",
            },
          ],
        });
      }
      return json({});
    }, requests);

    const token = await tsAuth.oidc.token("01ARZ3NDEKTSV4RRFFQ6000002", "cs_1", {
      grant_type: "authorization_code",
      code: "c1",
      redirect_uri: "https://app.acme.test/cb",
      code_verifier: "v1",
    });
    assert.equal(token.ok, false);
    assert.equal(
      new URL(/** @type {Request} */ (requests[0]).url).pathname,
      "/acme/oauth/token",
    );
    assert.equal(
      /** @type {Request} */ (requests[0]).headers.get("content-type"),
      "application/x-www-form-urlencoded",
    );
    assert.equal(
      /** @type {Request} */ (requests[0]).headers.get("authorization"),
      null,
    );
    assert.equal(
      await /** @type {Request} */ (requests[0]).text(),
      "client_id=01ARZ3NDEKTSV4RRFFQ6000002&client_secret=cs_1&grant_type=authorization_code&code=c1&redirect_uri=https%3A%2F%2Fapp.acme.test%2Fcb&code_verifier=v1",
    );

    const discovery = await tsAuth.oidc.discovery();
    assert.equal(discovery.ok, true);
    if (discovery.ok)
      assert.equal(
        discovery.value.token_endpoint_auth_methods_supported[0],
        "client_secret_post",
      );
    assert.equal(
      new URL(/** @type {Request} */ (requests[1]).url).pathname,
      "/acme/.well-known/openid-configuration",
    );
    assert.equal(
      /** @type {Request} */ (requests[1]).headers.get("authorization"),
      null,
    );

    const jwks = await tsAuth.oidc.jwks();
    assert.equal(jwks.ok, true);
    assert.equal(
      new URL(/** @type {Request} */ (requests[2]).url).pathname,
      "/acme/.well-known/jwks.json",
    );
    assert.equal(
      /** @type {Request} */ (requests[2]).headers.get("authorization"),
      null,
    );

    const revoked = await tsAuth.oidc.revoke("01ARZ3NDEKTSV4RRFFQ6000002", "cs_1", "at_1");
    assert.equal(revoked.ok, true);
    assert.equal(
      new URL(/** @type {Request} */ (requests[3]).url).pathname,
      "/acme/oauth/revoke",
    );
    assert.equal(
      /** @type {Request} */ (requests[3]).headers.get("content-type"),
      "application/x-www-form-urlencoded",
    );
    assert.equal(
      await /** @type {Request} */ (requests[3]).text(),
      "client_id=01ARZ3NDEKTSV4RRFFQ6000002&client_secret=cs_1&token=at_1",
    );

    const userinfo = await tsAuth.oidc.userinfo("at_1");
    assert.equal(userinfo.ok, true);
    if (userinfo.ok) assert.equal(userinfo.value.sub, "01ARZ3NDEKTSV4RRFFQ6000011");
    assert.equal(
      new URL(/** @type {Request} */ (requests[4]).url).pathname,
      "/acme/oauth/userinfo",
    );
    assert.equal(
      /** @type {Request} */ (requests[4]).headers.get("authorization"),
      "Bearer at_1",
    );
  });

  test("sends plan family routes and instance patches on their published paths", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient((request) => {
      const path = new URL(request.url).pathname;
      if (path === "/v1/acme/billing")
        return json({
          plan: "pro",
          status: "active",
          user_count: 3,
          quota: 10,
        });
      if (path === "/v1/acme/billing/checkout")
        return json({ checkout_url: "https://checkout.stripe.test/1" });
      return json({ id: "01ARZ3NDEKTSV4RRFFQ6000005", slug: "acme", name: "Acme" });
    }, requests);

    const overview = await tsAuth.billing.get();
    assert.equal(overview.ok, true);
    if (overview.ok) assert.equal(overview.value.plan, "pro");
    assert.equal(
      new URL(/** @type {Request} */ (requests[0]).url).pathname,
      "/v1/acme/billing",
    );

    const checkout = await tsAuth.billing.checkout({ plan: "pro" });
    assert.equal(checkout.ok, true);
    assert.equal(
      new URL(/** @type {Request} */ (requests[1]).url).pathname,
      "/v1/acme/billing/checkout",
    );
    assert.deepEqual(
      JSON.parse(await /** @type {Request} */ (requests[1]).text()),
      { plan: "pro" },
    );

    const updated = await tsAuth.instance.update({ name: "Acme Labs" });
    assert.equal(updated.ok, true);
    assert.equal(
      new URL(/** @type {Request} */ (requests[2]).url).pathname,
      "/v1/acme/instance",
    );
    assert.deepEqual(
      JSON.parse(await /** @type {Request} */ (requests[2]).text()),
      {
        name: "Acme Labs",
      },
    );
  });
});

// These controls inspect SDK-owned requests with an always-throwing transport;
// no successful hosted identity service response is fabricated.
test("resource requests retain one exact audience and keep no-resource browser inheritance", async () => {
  /** @type {Request[]} */
  const requests = [];
  const client = createTsAuthClient({
    apiBaseUrl: "https://auth.example.test",
    instanceSlug: "example",
    fetch: async (input) => {
      requests.push(new Request(input));
      throw new Error("Request-only protocol control.");
    },
  });
  const resource =
    "https://api.example.test/management/../exact?tenant=a%2Fb&mode=1";
  const grants = [
    {
      grant_type: /** @type {const} */ ("authorization_code"),
      code: "code",
      redirect_uri: "https://app.example.test/callback",
      resource,
    },
    {
      grant_type: /** @type {const} */ ("refresh_token"),
      refresh_token: "refresh",
      scope: "management:read",
      resource,
    },
    {
      grant_type: /** @type {const} */ ("client_credentials"),
      resource,
      scope: "management:read",
    },
    {
      grant_type: /** @type {const} */ (
        "urn:ietf:params:oauth:grant-type:device_code"
      ),
      device_code: "device",
      resource,
    },
    {
      grant_type: /** @type {const} */ ("refresh_token"),
      refresh_token: "browser-refresh",
    },
    {
      grant_type: /** @type {const} */ ("authorization_code"),
      code: "browser-code",
      redirect_uri: "https://app.example.test/callback",
    },
  ];
  for (const grant of grants)
    assert.equal(
      (await client.oidc.token("01ARZ3NDEKTSV4RRFFQ6000002", "secret", grant)).ok,
      false,
    );
  assert.equal(
    (
      await client.oidc.deviceAuthorization("01ARZ3NDEKTSV4RRFFQ6000002", "secret", {
        resource,
        scope: "management:read",
      })
    ).ok,
    false,
  );
  for (const request of requests) {
    assert.equal(request.headers.get("authorization"), null);
    assert.equal(
      request.headers.get("content-type"),
      "application/x-www-form-urlencoded",
    );
    const form = new URLSearchParams(await request.text());
    const browser = ["browser-refresh", "browser-code"].includes(
      form.get("refresh_token") ?? form.get("code") ?? "",
    );
    assert.deepEqual(form.getAll("resource"), browser ? [] : [resource]);
  }
  const authorize = client.oidc.authorizationUrl({
    client_id: "01ARZ3NDEKTSV4RRFFQ6000002",
    response_type: "code",
    redirect_uri: "https://app.example.test/callback",
    resource,
    scope: "management:read",
    state: "state",
  });
  assert.equal(authorize.pathname, "/example/oauth/authorize");
  assert.deepEqual(authorize.searchParams.getAll("resource"), [resource]);
  assert.equal(
    client.oidc
      .authorizationUrl({
        client_id: "01ARZ3NDEKTSV4RRFFQ6000002",
        response_type: "code",
        redirect_uri: "https://app.example.test/callback",
      })
      .searchParams.has("resource"),
    false,
  );
  assert.equal(requests.length, 7);
});

test("resource Basic encodes the secret and never resolves an instance key", async () => {
  let instanceKeys = 0;
  /** @type {Request | undefined} */
  let captured;
  const client = createTsAuthClient({
    apiBaseUrl: "https://auth.example.test",
    instanceSlug: "example",
    accessToken: () => {
      instanceKeys++;
      throw new Error("Instance key must stay separate.");
    },
    fetch: async (input) => {
      captured = new Request(input);
      throw new Error("Basic request-only control.");
    },
  });
  const result = await client.oidc.introspectResource(
    {
      issuer: "https://issuer.example.test/example",
      audience: "https://api.example.test/management",
      introspection_client_id: "01ARZ3NDEKTSV4RRFFQ6000003",
      introspection_secret: "secret: +%β",
    },
    "opaque-token",
    "access_token",
  );
  assert.equal(result.ok, false);
  assert.equal(instanceKeys, 0);
  assert.ok(captured);
  assert.equal(new URL(captured.url).pathname, "/example/oauth/introspect");
  assert.equal(
    atob(
      /** @type {string} */ (captured.headers.get("authorization")).slice(6),
    ),
    "01ARZ3NDEKTSV4RRFFQ6000003:secret%3A+%2B%25%CE%B2",
  );
  assert.deepEqual(
    [...new URLSearchParams(await captured.text())],
    [
      ["token", "opaque-token"],
      ["token_type_hint", "access_token"],
    ],
  );
});

test("OAuth policy management sends exact snake_case requests and epoch checks", async () => {
  /** @type {Request[]} */
  const requests = [];
  const client = createTsAuthClient({
    apiBaseUrl: "https://auth.example.test",
    instanceSlug: "example",
    accessToken: "sk_request_only",
    fetch: async (input) => {
      requests.push(new Request(input));
      throw new Error("Management request-only control.");
    },
  });
  const policy = {
    allowed_resource_audiences: ["https://api.example.test/management"],
    allowed_scopes: ["management:read"],
    allowed_grant_types: [/** @type {const} */ ("client_credentials")],
  };
  await client.oauthClients.create({
    name: "service",
    redirect_uris: ["https://app.example.test/callback"],
    ...policy,
  });
  await client.oauthClients.update("01ARZ3NDEKTSV4RRFFQ69G5FAV", {
    expected_auth_epoch: 1,
    enabled: false,
    rotate_secret: true,
    ...policy,
  });
  const resource = {
    audience: /** @type {string} */ (policy.allowed_resource_audiences[0]),
    allowed_client_ids: ["01ARZ3NDEKTSV4RRFFQ6000002"],
    allowed_scopes: policy.allowed_scopes,
    allowed_grant_types: policy.allowed_grant_types,
  };
  await client.oauthResources.create(resource);
  await client.oauthResources.list();
  await client.oauthResources.update("01ARZ3NDEKTSV4RRFFQ69G5FAV", {
    expected_auth_epoch: 2,
    enabled: false,
    rotate_secret: true,
  });
  await client.oauthResources.revoke("01ARZ3NDEKTSV4RRFFQ69G5FAV");
  assert.deepEqual(
    requests.map((request) => [request.method, new URL(request.url).pathname]),
    [
      ["POST", "/v1/example/oauth-clients"],
      ["PATCH", "/v1/example/oauth-clients/01ARZ3NDEKTSV4RRFFQ69G5FAV"],
      ["POST", "/v1/example/oauth-resources"],
      ["GET", "/v1/example/oauth-resources"],
      ["PATCH", "/v1/example/oauth-resources/01ARZ3NDEKTSV4RRFFQ69G5FAV"],
      ["DELETE", "/v1/example/oauth-resources/01ARZ3NDEKTSV4RRFFQ69G5FAV"],
    ],
  );
  for (const request of requests)
    assert.equal(
      request.headers.get("authorization"),
      "Bearer sk_request_only",
    );
  assert.deepEqual(
    JSON.parse(await /** @type {Request} */ (requests[2]).text()),
    resource,
  );
  assert.deepEqual(
    JSON.parse(await /** @type {Request} */ (requests[4]).text()),
    {
      expected_auth_epoch: 2,
      enabled: false,
      rotate_secret: true,
    },
  );
});

test("malformed resource and policy values reject synchronously without fallback or dispatch", () => {
  let dispatched = 0;
  const client = createTsAuthClient({
    apiBaseUrl: "https://auth.example.test",
    instanceSlug: "example",
    fetch: async () => {
      dispatched++;
      throw new Error("No dispatch permitted.");
    },
  });
  for (const resource of [
    "",
    "https://",
    "https:///repair",
    "http://api.example.test",
    "https://user@api.example.test",
    "https://api.example.test/#fragment",
    "https://api.example.test/%bad%",
    "https://api.example.test/a b",
    "https://api.example.test/[array]",
    ["https://api.example.test"],
  ]) {
    // Runtime fault injection covers untyped callers as well as the string API.
    assert.throws(
      () =>
        client.oidc.token("01ARZ3NDEKTSV4RRFFQ6000002", "secret", {
          grant_type: "client_credentials",
          resource: /** @type {string} */ (resource),
          scope: "management:read",
        }),
      /resource/u,
    );
    assert.throws(
      () =>
        client.oidc.authorizationUrl({
          client_id: "01ARZ3NDEKTSV4RRFFQ6000002",
          response_type: "code",
          redirect_uri: "https://app.example.test/callback",
          resource: /** @type {string} */ (resource),
        }),
      /resource/u,
    );
  }
  assert.throws(
    () =>
      client.oauthResources.update("01ARZ3NDEKTSV4RRFFQ69G5FAV", {
        expected_auth_epoch: Number.MAX_SAFE_INTEGER + 1,
      }),
    /exact unsigned/u,
  );
  assert.throws(
    () => client.oauthClients.update("01ARZ3NDEKTSV4RRFFQ69G5FAV", { expected_auth_epoch: -1 }),
    /exact unsigned/u,
  );
  assert.throws(
    () =>
      client.oidc.token("01ARZ3NDEKTSV4RRFFQ6000002", "secret", {
        grant_type: "client_credentials",
        resource: "https://api.example.test",
        scope: "read read",
      }),
    /scope/u,
  );
  assert.equal(dispatched, 0);
});

const componentManifestPath = process.env.TERRASCALE_AUTH_COMPONENT_RESPONSES;
// This is producer-byte playback for schema interoperability, not a live or
// authenticated hosted boundary. Without retained producer artifacts it skips.
test.skipIf(componentManifestPath === undefined)(
  "decodes the accepted producer's captured component OAuth response bytes",
  async () => {
    const { readFile } = await import("node:fs/promises");
    const { createHash } = await import("node:crypto");
    assert.ok(componentManifestPath);
    const manifestBytes = await readFile(componentManifestPath);
    assert.equal(
      createHash("sha256").update(manifestBytes).digest("hex"),
      "d00d65a3d7e78b43d10de4e33d95debd7a1b3eeef53b7c5280b204c5441239f3",
    );
    const manifest = JSON.parse(manifestBytes.toString());
    assert.equal(
      manifest.producer_source_freeze_sha256,
      "4104d8dbead261a440de72ee7265118681a78460b8edbedfa9e62563a152504b",
    );
    assert.equal(manifest.records.length, 43);
    for (const record of manifest.records) {
      const bytes = await readFile(record.path);
      assert.equal(
        createHash("sha256").update(bytes).digest("hex"),
        record.sha256,
      );
      const wire = JSON.parse(bytes.toString());
      assert.deepEqual(JSON.parse(wire.raw_body || "null"), wire.body ?? null);
      const client = createTsAuthClient({
        apiBaseUrl: "https://auth.example.test",
        instanceSlug: "example",
        accessToken: "sk_component_replay",
        fetch: async () =>
          new Response(wire.raw_body || null, {
            status: wire.status,
            headers: wire.headers,
          }),
      });
      let result;
      switch (record.case) {
        case "client-create":
          result = await client.oauthClients.create({});
          break;
        case "client-list":
          result = await client.oauthClients.list();
          break;
        case "client-update":
          result = await client.oauthClients.update("01ARZ3NDEKTSV4RRFFQ69G5FAV", {});
          break;
        case "client-update-rotated":
          result = await client.oauthClients.update("01ARZ3NDEKTSV4RRFFQ69G5FAV", {
            rotate_secret: true,
          });
          break;
        case "resource-create":
          result = await client.oauthResources.create({
            audience: wire.body.resource.audience,
            allowed_client_ids: wire.body.resource.allowed_client_ids,
            allowed_grant_types: wire.body.resource.allowed_grant_types,
            allowed_scopes: wire.body.resource.allowed_scopes,
          });
          break;
        case "resource-list":
          result = await client.oauthResources.list();
          break;
        case "resource-update":
          result = await client.oauthResources.update("01ARZ3NDEKTSV4RRFFQ69G5FAV", {
            expected_auth_epoch: 1,
          });
          break;
        case "resource-update-rotated":
          result = await client.oauthResources.update("01ARZ3NDEKTSV4RRFFQ69G5FAV", {
            expected_auth_epoch: 1,
            rotate_secret: true,
          });
          break;
        case "resource-delete":
          result = await client.oauthResources.revoke("01ARZ3NDEKTSV4RRFFQ69G5FAV");
          break;
        case "client-access-introspection":
        case "client-refresh-introspection":
          result = await client.oidc.introspect("01ARZ3NDEKTSV4RRFFQ6000002", "secret", "opaque");
          break;
        default: {
          const active = wire.body?.active === true;
          result = await client.oidc.introspectResource(
            {
              issuer: active
                ? wire.body.iss
                : "https://issuer.example.test/example",
              audience: active
                ? wire.body.aud
                : "https://api.example.test/management",
              introspection_client_id: "01ARZ3NDEKTSV4RRFFQ6000003",
              introspection_secret: "component-replay",
            },
            "opaque",
          );
          // A retained active response expires honestly at the local caller time.
          if (
            active &&
            (wire.body.exp <= Date.now() / 1000 ||
              wire.body.iat > Date.now() / 1000)
          ) {
            assert.equal(result.ok, false);
            if (!result.ok) assert.equal(result.error.kind, "invalid-response");
            continue;
          }
        }
      }
      assert.equal(result.ok, wire.status < 400, record.path);
      if (result.ok)
        assert.deepEqual(result.value, wire.body ?? undefined, record.path);
      else {
        assert.equal(result.status, wire.status);
        assert.equal(result.error.code, wire.body.error);
      }
    }
  },
);

test.skipIf(componentManifestPath === undefined)(
  "resource binding and client PATCH branch are snapshotted before captured-byte delivery",
  async () => {
    const { readFile } = await import("node:fs/promises");
    const { createHash } = await import("node:crypto");
    assert.ok(componentManifestPath);
    const manifestBytes = await readFile(componentManifestPath);
    assert.equal(
      createHash("sha256").update(manifestBytes).digest("hex"),
      "d00d65a3d7e78b43d10de4e33d95debd7a1b3eeef53b7c5280b204c5441239f3",
    );
    const manifest = JSON.parse(manifestBytes.toString());
    for (const branch of ["resource", "client-patch"]) {
      const record = manifest.records.find(
        /** @param {{ case: string; active?: boolean }} record */
        (record) =>
          branch === "resource"
            ? record.case === "resource-introspection" && record.active === true
            : record.case === "client-update",
      );
      const bytes = await readFile(record.path);
      assert.equal(
        createHash("sha256").update(bytes).digest("hex"),
        record.sha256,
      );
      const wire = JSON.parse(bytes.toString());
      /** @type {(response: Response) => void} */
      let deliver = () => undefined;
      /** @type {() => void} */
      let dispatch = () => undefined;
      /** @type {Promise<void>} */
      const started = new Promise((resolve) => {
        dispatch = resolve;
      });
      /** @type {Promise<Response>} */
      const response = new Promise((resolve) => {
        deliver = resolve;
      });
      const client = createTsAuthClient({
        apiBaseUrl: "https://auth.example.test",
        instanceSlug: "example",
        fetch: async () => {
          dispatch();
          return response;
        },
      });
      if (branch === "resource") {
        const credentials = {
          issuer: "https://wrong.example.test/example",
          audience: "https://wrong.example.test/management",
          introspection_client_id: "01ARZ3NDEKTSV4RRFFQ6000003",
          introspection_secret: "component-replay",
        };
        const pending = client.oidc.introspectResource(credentials, "opaque");
        await started;
        credentials.issuer = wire.body.iss;
        credentials.audience = wire.body.aud;
        deliver(
          new Response(wire.raw_body, {
            status: wire.status,
            headers: wire.headers,
          }),
        );
        const result = await pending;
        assert.equal(result.ok, false);
        if (!result.ok) assert.equal(result.error.kind, "invalid-response");
      } else {
        /** @type {{ rotate_secret?: boolean }} */
        const patch = {};
        const pending = client.oauthClients.update("01ARZ3NDEKTSV4RRFFQ69G5FAV", patch);
        await started;
        patch.rotate_secret = true;
        deliver(
          new Response(wire.raw_body, {
            status: wire.status,
            headers: wire.headers,
          }),
        );
        const result = await pending;
        assert.equal(result.ok, true);
        if (result.ok) assert.deepEqual(result.value, wire.body);
      }
    }
  },
);

const supplementManifestPath = process.env.TERRASCALE_AUTH_COMPONENT_SUPPLEMENT;
// These are unchanged bytes captured from the accepted real Auth handlers.
// Playback establishes decoding interoperability only, without hosted access.
test.skipIf(supplementManifestPath === undefined)(
  "decodes actual code, refresh, service and device grant response shapes",
  async () => {
    const { readFile } = await import("node:fs/promises");
    const { createHash } = await import("node:crypto");
    assert.ok(supplementManifestPath);
    const manifestBytes = await readFile(supplementManifestPath);
    assert.equal(
      createHash("sha256").update(manifestBytes).digest("hex"),
      "a3f385e81b241dee10c8fa36bc69c2bf49b05aab758c87e869632fee2c8dda60",
    );
    const manifest = JSON.parse(manifestBytes.toString());
    assert.equal(
      manifest.source_freeze_sha256,
      "4104d8dbead261a440de72ee7265118681a78460b8edbedfa9e62563a152504b",
    );
    assert.equal(
      manifest.source_review_sha256,
      "f06b20a2cf1a00dcef41dc305a51e4767aa2b11e78103758536720273d4c1d17",
    );
    assert.equal(Object.keys(manifest.required_positive_cases).length, 6);
    const records =
      /** @type {{ case: string; path: string; sha256: string }[]} */ ([
        ...Object.values(manifest.required_positive_cases),
        manifest.observed_records.find(
          /** @param {{ case: string }} record */
          (record) => record.case === "client-access-introspection",
        ),
      ]);
    for (const record of records) {
      assert.ok(record);
      const bytes = await readFile(record.path);
      assert.equal(
        createHash("sha256").update(bytes).digest("hex"),
        record.sha256,
      );
      const wire = JSON.parse(bytes.toString());
      assert.deepEqual(JSON.parse(wire.raw_body), wire.body);
      const client = createTsAuthClient({
        apiBaseUrl: "https://auth.example.test",
        instanceSlug: "example",
        fetch: async () =>
          new Response(wire.raw_body, {
            status: wire.status,
            headers: wire.headers,
          }),
      });
      let result;
      switch (record.case) {
        case "browser-code-token":
          result = await client.oidc.token("01ARZ3NDEKTSV4RRFFQ6000002", "secret", {
            grant_type: "authorization_code",
            code: "component-replay",
            redirect_uri: "https://app.example.test/callback",
          });
          break;
        case "resource-user-code-token":
          result = await client.oidc.token("01ARZ3NDEKTSV4RRFFQ6000002", "secret", {
            grant_type: "authorization_code",
            code: "component-replay",
            redirect_uri: "https://app.example.test/callback",
            resource: "https://api.example.test/management",
          });
          break;
        case "resource-refresh-narrowed-token":
          result = await client.oidc.token("01ARZ3NDEKTSV4RRFFQ6000002", "secret", {
            grant_type: "refresh_token",
            refresh_token: "component-replay",
            scope: "openid management:read",
            resource: "https://api.example.test/management",
          });
          assert.equal(Object.hasOwn(wire.body, "id_token"), false);
          break;
        case "resource-service-token":
          result = await client.oidc.token("01ARZ3NDEKTSV4RRFFQ6000002", "secret", {
            grant_type: "client_credentials",
            scope: "management:read",
            resource: "https://api.example.test/management",
          });
          assert.equal(Object.hasOwn(wire.body, "id_token"), false);
          assert.equal(Object.hasOwn(wire.body, "refresh_token"), false);
          break;
        case "resource-device-start":
          result = await client.oidc.deviceAuthorization("01ARZ3NDEKTSV4RRFFQ6000002", "secret", {
            resource: "https://api.example.test/device-management",
            scope: "openid email",
          });
          break;
        case "resource-device-approved-token":
          result = await client.oidc.token("01ARZ3NDEKTSV4RRFFQ6000002", "secret", {
            grant_type: "urn:ietf:params:oauth:grant-type:device_code",
            device_code: "component-replay",
            resource: "https://api.example.test/device-management",
          });
          break;
        case "client-access-introspection":
          result = await client.oidc.introspect("01ARZ3NDEKTSV4RRFFQ6000002", "secret", "opaque");
          break;
        default:
          assert.fail(`Unexpected supplemental case ${record.case}`);
      }
      assert.equal(result.ok, true, record.case);
      if (result.ok) assert.deepEqual(result.value, wire.body, record.case);
    }
  },
);
