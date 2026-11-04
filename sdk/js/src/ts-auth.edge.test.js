// Edge-case tests for the ts-auth client, one group per section of the
// cross-SDK edge-case matrix (/tmp/ts-auth-edge-case-matrix.md). Pins marked
// "pins current behavior" document arguable outcomes as implemented today.

import assert from "node:assert/strict";
import { createHmac } from "node:crypto";
import { describe, test } from "vitest";

import {
  createTsAuthClient,
  TsAuthUserPatch,
  verifyTsAuthWebhook,
  verifyTsAuthWebhookWithTolerance,
} from "./ts-auth.js";

const baseUser = {
  id: "01ARZ3NDEKTSV4RRFFQ6000004",
  email: "ada@acme.test",
  email_verified: true,
  username: "ada",
  name: "Ada",
  banned: false,
  created_at: 1_788_220_800,
  public_metadata: {},
};

/**
 * @param {string} id
 * @param {Record<string, unknown>} [overrides]
 * @returns {typeof baseUser}
 */
function user(id, overrides = {}) {
  return { ...baseUser, id, ...overrides };
}

/**
 * @param {unknown} value
 * @param {number} [status]
 * @param {Record<string, string>} [headers]
 * @returns {Response}
 */
function json(value, status = 200, headers = {}) {
  return new Response(value === null ? null : JSON.stringify(value), {
    status,
    headers: { "content-type": "application/json", ...headers },
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
 * @param {Record<string, unknown>} [overrides]
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

/**
 * @param {string} secret
 * @param {string} timestamp
 * @param {Uint8Array} payload
 * @returns {string}
 */
function sign(secret, timestamp, payload) {
  return `sha256=${createHmac("sha256", secret).update(`${timestamp}.`).update(payload).digest("hex")}`;
}

describe("ts-auth client edge cases: A. response-decode edges", () => {
  test("A1 user JSON without an entity ID is rejected", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      () => json({ email: "ada@acme.test" }),
      requests,
    );
    const missing = await tsAuth.users.get("01ARZ3NDEKTSV4RRFFQ6000004");
    assert.equal(missing.ok, false);
    if (!missing.ok) assert.equal(missing.error.kind, "invalid-response");
  });

  test("A2 present-null and absent optional fields deserialize to null and undefined", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      (_request, index) =>
        json(
          index === 0
            ? { ...user("01ARZ3NDEKTSV4RRFFQ6000004"), ban_reason: null, last_sign_in_at: null }
            : user("01ARZ3NDEKTSV4RRFFQ6000004"),
        ),
      requests,
    );
    const presentNull = await tsAuth.users.get("01ARZ3NDEKTSV4RRFFQ6000004");
    assert.equal(presentNull.ok, true);
    if (presentNull.ok) {
      assert.equal(presentNull.value.ban_reason, null);
      assert.equal(presentNull.value.last_sign_in_at, null);
    }
    const absent = await tsAuth.users.get("01ARZ3NDEKTSV4RRFFQ6000004");
    assert.equal(absent.ok, true);
    if (absent.ok) {
      assert.equal(absent.value.ban_reason, undefined);
      assert.equal("ban_reason" in absent.value, false);
    }
  });

  test("A3 unknown extra response fields are ignored by the typed view", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      () => json({ ...user("01ARZ3NDEKTSV4RRFFQ6000004"), future_field: { x: 1 } }),
      requests,
    );
    const decoded = await tsAuth.users.get("01ARZ3NDEKTSV4RRFFQ6000004");
    assert.equal(decoded.ok, true);
    if (decoded.ok) {
      assert.equal(decoded.value.id, "01ARZ3NDEKTSV4RRFFQ6000004");
      // pins current behavior: unknown fields pass through verbatim (no stripping).
      assert.deepEqual(
        /** @type {Record<string, unknown>} */ (decoded.value).future_field,
        { x: 1 },
      );
    }
  });

  test("A4 wrong-typed field decodes verbatim without crashing or zeroing", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      () => json({ ...user("01ARZ3NDEKTSV4RRFFQ6000004"), email_verified: "yes", banned: 1 }),
      requests,
    );
    const decoded = await tsAuth.users.get("01ARZ3NDEKTSV4RRFFQ6000004");
    // pins current behavior: no runtime type checking of response fields.
    assert.equal(decoded.ok, true);
    if (decoded.ok) {
      assert.equal(decoded.value.email_verified, "yes");
      assert.equal(decoded.value.banned, 1);
    }
  });

  test("A5 malformed JSON body maps to an invalid-response error value", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      () =>
        new Response('{"data": [', {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
      requests,
    );
    const listed = await tsAuth.users.list();
    assert.equal(listed.ok, false);
    if (!listed.ok) {
      assert.equal(listed.status, 200);
      assert.equal(listed.error.kind, "invalid-response");
      assert.equal(listed.error.code, "invalid_response");
      assert.equal(
        listed.error.message,
        "ts-auth returned an invalid JSON response body.",
      );
      assert.equal(listed.body, undefined);
    }
  });

  test("A6 empty body on a JSON-expecting route is an invalid-response error", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      () => new Response(null, { status: 200 }),
      requests,
    );
    const got = await tsAuth.users.get("01ARZ3NDEKTSV4RRFFQ6000004");
    assert.equal(got.ok, false);
    if (!got.ok) {
      assert.equal(got.status, 200);
      assert.equal(got.error.kind, "invalid-response");
      assert.equal(got.error.code, "invalid_response");
      assert.equal(
        got.error.message,
        "ts-auth returned an empty response body.",
      );
    }
  });

  test("A7 non-JSON HTML body is an invalid-response error", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      () =>
        new Response("<html>gateway error</html>", {
          status: 200,
          headers: { "content-type": "text/html" },
        }),
      requests,
    );
    const got = await tsAuth.users.get("01ARZ3NDEKTSV4RRFFQ6000004");
    assert.equal(got.ok, false);
    if (!got.ok) {
      assert.equal(got.status, 200);
      assert.equal(got.error.kind, "invalid-response");
      assert.equal(got.error.code, "invalid_response");
      assert.equal(
        got.error.message,
        "ts-auth returned a non-JSON response body.",
      );
    }
  });

  test("A8 envelope mix-ups decode without a decode error", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient((request) => {
      const path = new URL(request.url).pathname;
      if (path === "/v1/acme/users")
        return json([user("01ARZ3NDEKTSV4RRFFQ6000004"), user("01ARZ3NDEKTSV4RRFFQ6000006")]);
      if (path === "/v1/acme/organizations") {
        return json({
          data: [
            {
              id: "01ARZ3NDEKTSV4RRFFQ6000002",
              name: "Acme",
              slug: "acme",
              created_at: 1_788_220_800,
            },
          ],
        });
      }
      return json({});
    }, requests);
    const listed = await tsAuth.users.list();
    // pins current behavior: a bare array where {data} was expected yields the
    // array itself; value.data is undefined rather than an invalid-response error.
    assert.equal(listed.ok, true);
    if (listed.ok) {
      assert.equal(Array.isArray(listed.value), true);
      assert.equal(listed.value.data, undefined);
    }
    const organizations = await tsAuth.organizations.list();
    // pins current behavior: {data} where a bare array was expected yields the
    // envelope object itself.
    assert.equal(organizations.ok, true);
    if (organizations.ok)
      assert.equal(Array.isArray(organizations.value), false);
  });

  test('A9 {"data": null} list decodes to null; listAll surfaces invalid-response instead of crashing', async () => {
    const nullData = tsAuthClient(() => json({ data: null }), []);
    const listed = await nullData.users.list();
    // pins current behavior for list(): no runtime schema validation, so data
    // is null (matrix finding noted in the report).
    assert.equal(listed.ok, true);
    if (listed.ok) assert.equal(listed.value.data, null);

    /** @type {Request[]} */

    const requests = [];
    const tsAuth = tsAuthClient(() => json({ data: null }), requests);
    const all = await tsAuth.users.listAll({ pageSize: 2 });
    assert.equal(all.ok, false);
    if (!all.ok) {
      assert.equal(all.error.kind, "invalid-response");
      assert.equal(all.error.code, "invalid_response");
      assert.equal(all.error.message, "The users page data must be an array.");
    }
    assert.equal(requests.length, 1);
  });

  test("A10 a 100-user page (~70 KB) decodes with every id verified", async () => {
    const page = Array.from({ length: 100 }, (_, index) =>
      user(`01ARZ3NDEKTSV4RRFFQ6${String(index + 1).padStart(6, "0")}`, {
        email: `user${index}@acme.test`,
        name: `User ${index}`,
        public_metadata: { tier: "pro", notes: "n".repeat(450) },
      }),
    );
    assert.ok(JSON.stringify({ data: page }).length > 60_000);
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(() => json({ data: page }), requests);
    const listed = await tsAuth.users.list({ limit: 100 });
    assert.equal(listed.ok, true);
    if (listed.ok) {
      assert.equal(listed.value.data.length, 100);
      assert.equal(listed.value.data[0]?.id, "01ARZ3NDEKTSV4RRFFQ6000001");
      assert.equal(listed.value.data[99]?.id, "01ARZ3NDEKTSV4RRFFQ6000100");
      assert.equal(
        new Set(listed.value.data.map((entry) => entry.id)).size,
        100,
      );
    }
  });

  test("A11 unicode names round-trip byte-identically in decode and PATCH bodies", async () => {
    const unicodeName = "Ada 🌸 中 é";
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      () => json(user("01ARZ3NDEKTSV4RRFFQ6000004", { name: unicodeName })),
      requests,
    );

    const got = await tsAuth.users.get("01ARZ3NDEKTSV4RRFFQ6000004");
    assert.equal(got.ok, true);
    if (got.ok) assert.equal(got.value.name, unicodeName);

    const expectedBytes = `{"name":${JSON.stringify(unicodeName)}}`;
    const updated = await tsAuth.users.update(
      "01ARZ3NDEKTSV4RRFFQ6000004",
      new TsAuthUserPatch().withName(unicodeName),
    );
    assert.equal(updated.ok, true);
    const wire = await /** @type {Request} */ (requests[1]).text();
    assert.equal(wire, expectedBytes);
    assert.equal(
      Buffer.byteLength(wire, "utf8"),
      Buffer.byteLength(expectedBytes, "utf8"),
    );
  });
});

describe("ts-auth client edge cases: B. status-code and error-mapping edges", () => {
  test("B12 401 bad key maps to authentication with the body code and message", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      () =>
        json(
          {
            error: { code: "unauthorized", message: "Invalid management key." },
          },
          401,
        ),
      requests,
    );
    const listed = await tsAuth.users.list();
    assert.equal(listed.ok, false);
    if (!listed.ok) {
      assert.equal(listed.status, 401);
      assert.equal(listed.error.kind, "authentication");
      assert.equal(listed.error.code, "unauthorized");
      assert.equal(listed.error.message, "Invalid management key.");
    }
  });

  test("B13 403 insufficient_scope maps to authorization", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      () =>
        json(
          {
            error: {
              code: "insufficient_scope",
              message: "Missing scope users:read.",
            },
          },
          403,
        ),
      requests,
    );
    const got = await tsAuth.users.get("01ARZ3NDEKTSV4RRFFQ6000004");
    assert.equal(got.ok, false);
    if (!got.ok) {
      assert.equal(got.error.kind, "authorization");
      assert.equal(got.error.code, "insufficient_scope");
      assert.equal(got.error.message, "Missing scope users:read.");
    }
  });

  test("B14 404 user_not_found and generic not_found both map with codes preserved", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      (_request, index) =>
        json(
          index === 0
            ? { error: { code: "user_not_found", message: "No such user." } }
            : { error: { code: "not_found", message: "Not found." } },
          404,
        ),
      requests,
    );
    for (const code of ["user_not_found", "not_found"]) {
      const got = await tsAuth.users.get("01ARZ3NDEKTSV4RRFFQ6000010");
      assert.equal(got.ok, false);
      if (!got.ok) {
        assert.equal(got.error.kind, "not-found");
        assert.equal(got.error.code, code);
      }
    }
  });

  test("B15 409 email_taken maps to conflict", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      () =>
        json(
          {
            error: {
              code: "email_taken",
              message: "grace@acme.test is already registered.",
            },
          },
          409,
        ),
      requests,
    );
    const created = await tsAuth.users.create({ email: "grace@acme.test" });
    assert.equal(created.ok, false);
    if (!created.ok) {
      assert.equal(created.error.kind, "conflict");
      assert.equal(created.error.code, "email_taken");
      assert.equal(
        created.error.message,
        "grace@acme.test is already registered.",
      );
    }
  });

  test("B16 429 rate_limited with Retry-After surfaces immediately at maxRetryAttempts 0", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      () =>
        json({ error: { code: "rate_limited", message: "Slow down." } }, 429, {
          "retry-after": "2",
        }),
      requests,
      { maxRetryAttempts: 0 },
    );
    const listed = await tsAuth.users.list();
    assert.equal(requests.length, 1);
    assert.equal(listed.ok, false);
    if (!listed.ok) {
      assert.equal(listed.status, 429);
      assert.equal(listed.error.kind, "rate-limit");
      assert.equal(listed.error.code, "rate_limited");
      assert.equal(listed.error.message, "Slow down.");
      // Retry-After: 2 surfaces on the error as milliseconds.
      assert.equal(listed.error.retryAfter, 2_000);
      assert.equal(listed.headers?.get("retry-after"), "2");
    }
  });

  test("B17 503 temporarily_unavailable and billing_not_configured map to server kind", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      (request) => {
        const path = new URL(request.url).pathname;
        if (path === "/v1/acme/billing/checkout") {
          return json(
            {
              error: {
                code: "billing_not_configured",
                message: "No payment method.",
              },
            },
            503,
          );
        }
        return json(
          { error: { code: "temporarily_unavailable", message: "Try again." } },
          503,
        );
      },
      requests,
      { maxRetryAttempts: 0 },
    );
    const listed = await tsAuth.users.list();
    assert.equal(listed.ok, false);
    if (!listed.ok) {
      assert.equal(listed.error.kind, "server");
      assert.equal(listed.error.code, "temporarily_unavailable");
    }
    const checkout = await tsAuth.billing.checkout({ plan: "pro" });
    assert.equal(checkout.ok, false);
    if (!checkout.ok) {
      assert.equal(checkout.error.kind, "server");
      assert.equal(checkout.error.code, "billing_not_configured");
    }
  });

  test("B18 flat OAuth 401 with WWW-Authenticate: Basic maps the code without crashing on the header", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      () =>
        json(
          {
            error: "invalid_client",
            error_description: "Client authentication failed.",
          },
          401,
          {
            "www-authenticate": 'Basic realm="ts-auth"',
          },
        ),
      requests,
    );
    const token = await tsAuth.oidc.token("01ARZ3NDEKTSV4RRFFQ6000002", "cs_wrong", {
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

  test("B19 flat OAuth invalid_grant without error_description keeps the OAuth code", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      () => json({ error: "invalid_grant" }, 401),
      requests,
    );
    const token = await tsAuth.oidc.token("01ARZ3NDEKTSV4RRFFQ6000002", "cs_1", {
      grant_type: "refresh_token",
      refresh_token: "rt_old",
    });
    assert.equal(token.ok, false);
    if (!token.ok) {
      assert.equal(token.error.kind, "authentication");
      // RFC 6749 allows omitting error_description; the bare error is the code
      // and doubles as the message.
      assert.equal(token.error.code, "invalid_grant");
      assert.equal(token.error.message, "invalid_grant");
    }
  });

  test("B20 null error code and message fall back to status-derived defaults", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      () => json({ error: { code: null, message: null } }, 400),
      requests,
    );
    const got = await tsAuth.users.get("01ARZ3NDEKTSV4RRFFQ6000004");
    assert.equal(got.ok, false);
    if (!got.ok) {
      assert.equal(got.error.kind, "domain");
      assert.equal(got.error.code, "bad_request");
      assert.equal(got.error.message, "The TerraScale API request failed.");
    }
  });

  test("B21 empty non-JSON 500 body yields a status-derived server error", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      () => new Response(null, { status: 500 }),
      requests,
    );
    const listed = await tsAuth.users.list();
    assert.equal(listed.ok, false);
    if (!listed.ok) {
      assert.equal(listed.status, 500);
      assert.equal(listed.error.kind, "server");
      assert.equal(listed.error.code, "server_error");
      assert.equal(listed.error.message, "The TerraScale API request failed.");
    }
  });

  test("B22 DELETE answering 200 with a body succeeds; the TS layer pins no 204", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(() => json({ deleted: true }, 200), requests);
    const deleted = await tsAuth.users.delete("01ARZ3NDEKTSV4RRFFQ6000004");
    // pins current behavior: any 2xx (with or without a body) is a success.
    assert.equal(deleted.ok, true);
    if (deleted.ok) assert.equal(deleted.status, 200);
  });
});

describe("ts-auth client edge cases: C. request-construction edges", () => {
  test("C23 non-ULID paths reject punctuation, Unicode and dot segments before HTTP", () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(() => json({}), requests);
    for (const id of [".", "..", "a/..", "a/b?c#d%e&f+g 中", "usr_1", "01arz3ndektsv4rrffq69g5fav"]) {
      assert.throws(() => tsAuth.users.get(id), /user id/u);
      assert.throws(() => tsAuth.users.delete(id), /user id/u);
    }
    assert.equal(requests.length, 0);
  });

  test("C24 blank and whitespace-only ids and fields fail preflight with the field named, no HTTP issued", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(() => json(user("x")), requests);
    for (const id of ["", " ", "\t\n"]) {
      assert.throws(() => tsAuth.users.get(id), /user id/u, JSON.stringify(id));
    }
    assert.throws(() => tsAuth.organizations.create({ name: " " }), /name/u);
    assert.throws(
      () =>
        tsAuth.organizations.createInvitation("01ARZ3NDEKTSV4RRFFQ6000002", {
          email: "\t",
          role: "admin",
        }),
      /email/u,
    );
    assert.throws(() => tsAuth.apiKeys.create({ name: "" }), /name/u);
    assert.equal(requests.length, 0);
  });

  test("C25 non-ULID pagination cursors fail before HTTP", () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(() => json({ data: [] }), requests);
    assert.throws(() => tsAuth.users.list({ startingAfter: "a&b=c+d%e中" }), /startingAfter/u);
    assert.equal(requests.length, 0);
  });

  test("C26 limit 1 and 100 are sent as-is; 0 and 101 are rejected preflight consistently", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(() => json({ data: [] }), requests);
    await tsAuth.users.list({ limit: 1 });
    await tsAuth.users.list({ limit: 100 });
    assert.equal(
      new URL(/** @type {Request} */ (requests[0]).url).search,
      "?limit=1",
    );
    assert.equal(
      new URL(/** @type {Request} */ (requests[1]).url).search,
      "?limit=100",
    );
    // pins current behavior: out-of-range limits never reach the server.
    assert.throws(() => tsAuth.users.list({ limit: 0 }), /between 1 and 100/u);
    assert.throws(
      () => tsAuth.users.list({ limit: 101 }),
      /between 1 and 100/u,
    );
    assert.throws(
      () => tsAuth.users.list({ limit: 1.5 }),
      /between 1 and 100/u,
    );
    assert.equal(requests.length, 2);
  });

  test("C27 instance slug rejects whitespace, control chars, slashes, and dot segments at construction", () => {
    /** @returns {Promise<Response>} */
    const neverFetch = () => Promise.reject(new Error("no HTTP expected"));
    for (const instanceSlug of [
      "",
      " ",
      "ac me",
      "ac/me",
      "ac\u0007me",
      ".",
      "..",
    ]) {
      assert.throws(
        () =>
          createTsAuthClient({
            apiBaseUrl: "https://auth.acme.test",
            accessToken: "sk_x",
            instanceSlug,
            fetch: neverFetch,
          }),
        /instanceSlug/u,
        JSON.stringify(instanceSlug),
      );
    }
  });

  test("C27 an over-255-byte slug is accepted (pins current behavior: no slug length cap)", async () => {
    /** @type {Request[]} */
    const requests = [];
    const longSlug = "a".repeat(300);
    const tsAuth = tsAuthClient(() => json({ data: [] }), requests, {
      instanceSlug: longSlug,
    });
    await tsAuth.users.list();
    assert.equal(
      new URL(/** @type {Request} */ (requests[0]).url).pathname,
      `/v1/${longSlug}/users`,
    );
  });

  test("C28 a header-breaking API key never reaches fetch (pins current behavior: Headers layer rejects it)", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(() => json({ data: [] }), requests, {
      accessToken: "sk_bad\r\nevil: 1",
    });
    const listed = await tsAuth.users.list();
    assert.equal(requests.length, 0);
    assert.equal(listed.ok, false);
    if (!listed.ok) {
      // pins current behavior: the key is not validated at construction; the
      // fetch Headers layer blocks the injection at request time as a
      // transport-kind value.
      assert.equal(listed.error.kind, "transport");
      assert.equal(listed.error.code, "transport_error");
      assert.match(listed.error.message, /invalid header value/u);
    }
  });

  test("C29 tri-state patch sends exact bytes for all fields at once, then for exactly one field", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(() => json(user("01ARZ3NDEKTSV4RRFFQ6000004")), requests);
    const patch = new TsAuthUserPatch()
      .withName("Ada Lovelace")
      .unsetUsername()
      .withPassword("new-password-1")
      .withEmail("ada2@acme.test")
      .withBanned(true)
      .withBanReason("policy")
      .withPublicMetadata({ tier: "pro" })
      .unsetPrivateMetadata();
    const updated = await tsAuth.users.update("01ARZ3NDEKTSV4RRFFQ6000004", patch);
    assert.equal(updated.ok, true);
    assert.equal(
      await /** @type {Request} */ (requests[0]).text(),
      '{"name":"Ada Lovelace","username":null,"password":"new-password-1","email":"ada2@acme.test","banned":true,"ban_reason":"policy","public_metadata":{"tier":"pro"},"private_metadata":null}',
    );

    const single = await tsAuth.users.update(
      "01ARZ3NDEKTSV4RRFFQ6000004",
      new TsAuthUserPatch().withBanned(false),
    );
    assert.equal(single.ok, true);
    // C30: the body is valid JSON with exactly the configured fields, nothing else.
    const singleBody = await /** @type {Request} */ (requests[1]).text();
    assert.equal(singleBody, '{"banned":false}');
    assert.deepEqual(
      Object.keys(
        /** @type {Record<string, unknown>} */ (JSON.parse(singleBody)),
      ),
      ["banned"],
    );
  });

  test("C31 an empty patch is rejected preflight", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(() => json(user("01ARZ3NDEKTSV4RRFFQ6000004")), requests);
    assert.throws(
      () => tsAuth.users.update("01ARZ3NDEKTSV4RRFFQ6000004", new TsAuthUserPatch()),
      /patch is empty/u,
    );
    assert.equal(requests.length, 0);
  });

  test("C32 create with every optional field omitted sends {}", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(() => json(user("01ARZ3NDEKTSV4RRFFQ6000011")), requests);
    const created = await tsAuth.users.create({});
    assert.equal(created.ok, true);
    assert.equal(await /** @type {Request} */ (requests[0]).text(), "{}");
  });

  test("C33 OIDC form secrets round-trip with every reserved character encoded once", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(() => {
      throw new Error("Token form probe; no provider response.");
    }, requests);
    const token = await tsAuth.oidc.token("01ARZ3NDEKTSV4RRFFQ6000002", "s&=+% #1", {
      grant_type: "client_credentials",
      resource: "https://api.example.test/management",
      scope: "management:read",
    });
    assert.equal(token.ok, false);
    assert.equal(
      await /** @type {Request} */ (requests[0]).text(),
      "client_id=01ARZ3NDEKTSV4RRFFQ6000002&client_secret=s%26%3D%2B%25+%231&grant_type=client_credentials&resource=https%3A%2F%2Fapi.example.test%2Fmanagement&scope=management%3Aread",
    );
  });

  test("C34 nested metadata values serialize faithfully", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(() => json(user("01ARZ3NDEKTSV4RRFFQ6000004")), requests);
    const metadata = {
      plan: "pro",
      nested: {
        count: 3,
        flag: true,
        none: null,
        list: [1, "two", { deep: false }],
      },
      empty: [],
      zero: 0,
    };
    const updated = await tsAuth.users.update(
      "01ARZ3NDEKTSV4RRFFQ6000004",
      new TsAuthUserPatch().withPublicMetadata(metadata),
    );
    assert.equal(updated.ok, true);
    const bodyText = await /** @type {Request} */ (requests[0]).text();
    assert.deepEqual(
      /** @type {{ public_metadata: unknown }} */ (JSON.parse(bodyText)),
      {
        public_metadata: metadata,
      },
    );
    assert.equal(bodyText, `{"public_metadata":${JSON.stringify(metadata)}}`);
  });
});

describe("ts-auth client edge cases: D. retry / deadline / cancellation edges", () => {
  test("D35 GET with always-503 surfaces after exactly 3 attempts at maxRetryAttempts 2", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      () =>
        json({ error: { code: "unavailable", message: "Try again." } }, 503),
      requests,
      { maxRetryAttempts: 2, retryDelayMs: 0 },
    );
    const listed = await tsAuth.users.list();
    assert.equal(requests.length, 3);
    assert.equal(listed.ok, false);
    if (!listed.ok) {
      assert.equal(listed.status, 503);
      assert.equal(listed.error.kind, "server");
      assert.equal(listed.error.code, "unavailable");
    }
  });

  test("D36 Retry-After: 0 retries immediately despite a 5-second configured backoff", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      (_request, index) =>
        index === 0
          ? json(
              { error: { code: "unavailable", message: "Try again." } },
              503,
              { "retry-after": "0" },
            )
          : json({ data: [user("01ARZ3NDEKTSV4RRFFQ6000004")] }),
      requests,
      { maxRetryAttempts: 1, retryDelayMs: 5_000 },
    );
    // A real 5s sleep would blow the test timeout; the elapsed bound keeps the
    // Retry-After: 0 pin from silently vanishing behind a raised timeout.
    const startedAt = Date.now();
    const listed = await tsAuth.users.list();
    assert.ok(Date.now() - startedAt < 1_000, "Retry-After: 0 must not sleep");
    assert.equal(requests.length, 2);
    assert.equal(listed.ok, true);
    if (listed.ok) assert.equal(listed.value.data[0]?.id, "01ARZ3NDEKTSV4RRFFQ6000004");
  });

  test("D37 POST with 503 makes exactly one attempt even with retries enabled", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      () =>
        json({ error: { code: "unavailable", message: "Try again." } }, 503),
      requests,
      { maxRetryAttempts: 2, retryDelayMs: 0 },
    );
    const created = await tsAuth.users.create({ email: "grace@acme.test" });
    assert.equal(requests.length, 1);
    assert.equal(created.ok, false);
    if (!created.ok) assert.equal(created.error.kind, "server");
  });

  test("D38 400, 409, 401, and 403 are never retried for GET", async () => {
    /** @type {Request[]} */
    const requests = [];
    const statuses = [400, 409, 401, 403];
    const kinds = ["domain", "conflict", "authentication", "authorization"];
    const tsAuth = tsAuthClient(
      (_request, index) =>
        json(
          { error: { code: `code_${statuses[index]}`, message: "nope" } },
          statuses[index],
        ),
      requests,
      { maxRetryAttempts: 2, retryDelayMs: 0 },
    );
    for (const [index, status] of statuses.entries()) {
      const listed = await tsAuth.users.list();
      assert.equal(listed.ok, false, `status ${status}`);
      if (!listed.ok) {
        assert.equal(listed.status, status);
        assert.equal(listed.error.kind, kinds[index]);
        assert.equal(listed.error.code, `code_${status}`);
      }
      assert.equal(requests.length, index + 1);
    }
  });

  test("D39 a transport error on GET is retried when enabled; on POST it surfaces as a transport value", async () => {
    /** @type {Request[]} */
    const getRequests = [];
    let calls = 0;
    /**
     * @param {RequestInfo | URL} input
     * @returns {Promise<Response>}
     */
    const flaky = async (input) => {
      getRequests.push(new Request(input).clone());
      calls += 1;
      if (calls === 1) throw new TypeError("fetch failed");
      return json({ data: [user("01ARZ3NDEKTSV4RRFFQ6000004")] });
    };
    const retried = createTsAuthClient({
      apiBaseUrl: "https://auth.acme.test",
      accessToken: "sk_x",
      instanceSlug: "acme",
      fetch: flaky,
      maxRetryAttempts: 1,
      retryDelayMs: 0,
    });
    const listed = await retried.users.list();
    assert.equal(getRequests.length, 2);
    assert.equal(listed.ok, true);

    /** @type {Request[]} */

    const postRequests = [];
    /** @type {typeof fetch} */
    const alwaysDown = async (input) => {
      postRequests.push(new Request(input).clone());
      throw new TypeError("fetch failed");
    };
    const failing = createTsAuthClient({
      apiBaseUrl: "https://auth.acme.test",
      accessToken: "sk_x",
      instanceSlug: "acme",
      fetch: alwaysDown,
      maxRetryAttempts: 1,
      retryDelayMs: 0,
    });
    const created = await failing.users.create({ email: "grace@acme.test" });
    assert.equal(postRequests.length, 1);
    assert.equal(created.ok, false);
    if (!created.ok) {
      assert.equal(created.error.kind, "transport");
      assert.equal(created.error.code, "transport_error");
      assert.equal(created.error.message, "fetch failed");
    }
  });

  test("D40 a pre-cancelled signal fails without issuing fetch", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(() => json({ data: [] }), requests);
    const listed = await tsAuth.users.list({ signal: AbortSignal.abort() });
    // Cancellation stays a transport error value with a distinct code.
    assert.equal(requests.length, 0);
    assert.equal(listed.ok, false);
    if (!listed.ok) {
      assert.equal(listed.error.kind, "transport");
      assert.equal(listed.error.code, "request_aborted");
      assert.match(listed.error.message, /abort/iu);
    }
  });

  test("D41 cancellation during the retry sleep surfaces a transport abort value", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      (_request, index) =>
        index === 0
          ? json({ error: { code: "unavailable", message: "Try again." } }, 503)
          : json({ data: [] }),
      requests,
      { maxRetryAttempts: 1, retryDelayMs: 5_000 },
    );
    const controller = new AbortController();
    setTimeout(() => controller.abort(), 20);
    const listed = await tsAuth.users.list({ signal: controller.signal });
    // The second attempt never fires after cancellation.
    assert.equal(requests.length, 1);
    assert.equal(listed.ok, false);
    if (!listed.ok) {
      assert.equal(listed.error.kind, "transport");
      assert.equal(listed.error.code, "request_aborted");
      assert.match(listed.error.message, /abort/iu);
    }
  });
});

describe("ts-auth client edge cases: E. pagination edges", () => {
  test("E43 pages 2,2,empty walk to 4 users over 3 requests; a hostile repeated cursor stops the walk", async () => {
    /** @type {Request[]} */
    const requests = [];
    const pages = [
      { data: [user("01ARZ3NDEKTSV4RRFFQ6000004"), user("01ARZ3NDEKTSV4RRFFQ6000006")] },
      { data: [user("01ARZ3NDEKTSV4RRFFQ6000007"), user("01ARZ3NDEKTSV4RRFFQ6000008")] },
      { data: [] },
    ];
    const tsAuth = tsAuthClient(
      (_request, index) => json(pages[index] ?? { data: [] }),
      requests,
    );
    const all = await tsAuth.users.listAll({ pageSize: 2 });
    assert.equal(all.ok, true);
    if (all.ok)
      assert.deepEqual(
        all.value.map((entry) => entry.id),
        ["01ARZ3NDEKTSV4RRFFQ6000004", "01ARZ3NDEKTSV4RRFFQ6000006", "01ARZ3NDEKTSV4RRFFQ6000007", "01ARZ3NDEKTSV4RRFFQ6000008"],
      );
    assert.equal(requests.length, 3);

    /** @type {Request[]} */

    const hostileRequests = [];
    const hostile = tsAuthClient(
      () => json({ data: [user("01ARZ3NDEKTSV4RRFFQ6000004"), user("01ARZ3NDEKTSV4RRFFQ6000006")] }),
      hostileRequests,
    );
    const repeated = await hostile.users.listAll({ pageSize: 2 });
    // The server ignores the cursor and repeats the same full page: the walk
    // stops at the repeated cursor instead of looping forever, surfacing the
    // duplicated rows it already collected.
    assert.equal(repeated.ok, true);
    if (repeated.ok)
      assert.deepEqual(
        repeated.value.map((entry) => entry.id),
        ["01ARZ3NDEKTSV4RRFFQ6000004", "01ARZ3NDEKTSV4RRFFQ6000006", "01ARZ3NDEKTSV4RRFFQ6000004", "01ARZ3NDEKTSV4RRFFQ6000006"],
      );
    assert.equal(hostileRequests.length, 2);
  });

  test("E44 a short final page terminates the walk without refetching it", async () => {
    /** @type {Request[]} */
    const requests = [];
    const pages = [
      { data: [user("01ARZ3NDEKTSV4RRFFQ6000004"), user("01ARZ3NDEKTSV4RRFFQ6000006")] },
      { data: [user("01ARZ3NDEKTSV4RRFFQ6000007")] },
    ];
    const tsAuth = tsAuthClient(
      (_request, index) => json(pages[index] ?? { data: [] }),
      requests,
    );
    const all = await tsAuth.users.listAll({ pageSize: 2 });
    assert.equal(all.ok, true);
    if (all.ok)
      assert.deepEqual(
        all.value.map((entry) => entry.id),
        ["01ARZ3NDEKTSV4RRFFQ6000004", "01ARZ3NDEKTSV4RRFFQ6000006", "01ARZ3NDEKTSV4RRFFQ6000007"],
      );
    assert.equal(requests.length, 2);
    assert.equal(
      new URL(/** @type {Request} */ (requests[1]).url).search,
      "?limit=2&starting_after=01ARZ3NDEKTSV4RRFFQ6000006",
    );
  });

  test("E45 an unknown cursor with a server resuming from top decodes without crashing", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      () => json({ data: [user("01ARZ3NDEKTSV4RRFFQ6000004"), user("01ARZ3NDEKTSV4RRFFQ6000006"), user("01ARZ3NDEKTSV4RRFFQ6000007")] }),
      requests,
    );
    const listed = await tsAuth.users.list({
      limit: 2,
      startingAfter: "01ARZ3NDEKTSV4RRFFQ6000009",
    });
    assert.equal(listed.ok, true);
    if (listed.ok)
      assert.deepEqual(
        listed.value.data.map((entry) => entry.id),
        ["01ARZ3NDEKTSV4RRFFQ6000004", "01ARZ3NDEKTSV4RRFFQ6000006", "01ARZ3NDEKTSV4RRFFQ6000007"],
      );
    assert.equal(
      new URL(/** @type {Request} */ (requests[0]).url).search,
      "?limit=2&starting_after=01ARZ3NDEKTSV4RRFFQ6000009",
    );
  });
});

describe("ts-auth client edge cases: F. webhook verifier edges", () => {
  const secret = `whsec_${"k".repeat(40)}`;
  const timestamp = "1758340000";
  const payload = new TextEncoder().encode(
    JSON.stringify({ id: "01ARZ3NDEKTSV4RRFFQ6000001", type: "user.created", data: {} }),
  );
  const signature = sign(secret, timestamp, payload);

  test("F47 uppercase hex signature is rejected (exact lowercase match)", async () => {
    const upper = `sha256=${signature.slice("sha256=".length).toUpperCase()}`;
    assert.notEqual(upper, signature);
    assert.equal(
      await verifyTsAuthWebhook(secret, timestamp, upper, payload),
      false,
    );
  });

  test("F48 a signature missing the sha256= prefix is rejected", async () => {
    assert.equal(
      await verifyTsAuthWebhook(
        secret,
        timestamp,
        signature.slice("sha256=".length),
        payload,
      ),
      false,
    );
  });

  test("F49 signatures of 63 and 65 hex characters are rejected", async () => {
    const hex = signature.slice("sha256=".length);
    assert.equal(
      await verifyTsAuthWebhook(
        secret,
        timestamp,
        `sha256=${hex.slice(0, 63)}`,
        payload,
      ),
      false,
    );
    assert.equal(
      await verifyTsAuthWebhook(secret, timestamp, `sha256=${hex}0`, payload),
      false,
    );
  });

  test("F50 a non-numeric timestamp is rejected by both verifier variants", async () => {
    const nonNumeric = sign(secret, "not-a-number", payload);
    assert.equal(
      await verifyTsAuthWebhook(secret, "not-a-number", nonNumeric, payload),
      false,
    );
    assert.equal(
      await verifyTsAuthWebhookWithTolerance(
        secret,
        "not-a-number",
        nonNumeric,
        payload,
        60_000,
        Number(timestamp) * 1_000,
      ),
      false,
    );
    // pins current behavior: the numeric-only timestamp regex also rejects a
    // negative timestamp in the plain variant (stricter than the matrix floor).
    const negative = sign(secret, "-1", payload);
    assert.equal(
      await verifyTsAuthWebhook(secret, "-1", negative, payload),
      false,
    );
    // A far-future timestamp passes the plain variant but fails tolerance.
    const farFuture = "9999999999";
    const farSignature = sign(secret, farFuture, payload);
    assert.equal(
      await verifyTsAuthWebhook(secret, farFuture, farSignature, payload),
      true,
    );
    assert.equal(
      await verifyTsAuthWebhookWithTolerance(
        secret,
        farFuture,
        farSignature,
        payload,
        60_000,
        Number(timestamp) * 1_000,
      ),
      false,
    );
  });

  test("F51 an empty payload with a correctly computed signature verifies", async () => {
    const empty = new Uint8Array(0);
    assert.equal(
      await verifyTsAuthWebhook(
        secret,
        timestamp,
        sign(secret, timestamp, empty),
        empty,
      ),
      true,
    );
  });

  test("F52 an empty or missing secret returns false without throwing", async () => {
    assert.equal(
      await verifyTsAuthWebhook("", timestamp, signature, payload),
      false,
    );
    assert.equal(
      await verifyTsAuthWebhook(
        /** @type {string} */ (/** @type {unknown} */ (undefined)),
        timestamp,
        signature,
        payload,
      ),
      false,
    );
  });

  test("F53 tolerance boundary: exactly at tolerance passes, past it fails", async () => {
    const now = Number(timestamp) * 1_000;
    assert.equal(
      await verifyTsAuthWebhookWithTolerance(
        secret,
        timestamp,
        signature,
        payload,
        60_000,
        now + 60_000,
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
        now - 60_000,
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
        now + 60_001,
      ),
      false,
    );
    assert.equal(
      await verifyTsAuthWebhookWithTolerance(
        secret,
        timestamp,
        signature,
        payload,
        60_000,
        now - 60_001,
      ),
      false,
    );
  });

  test("F54 multi-byte UTF-8 payload bytes verify; a flipped byte fails", async () => {
    const unicode = new TextEncoder().encode('{"name":"Ada 🌸 中 é"}');
    const unicodeSignature = sign(secret, timestamp, unicode);
    assert.equal(
      await verifyTsAuthWebhook(secret, timestamp, unicodeSignature, unicode),
      true,
    );
    const tampered = new Uint8Array(unicode);
    tampered[tampered.length - 2] =
      /** @type {number} */ (tampered[tampered.length - 2]) ^ 1;
    assert.equal(
      await verifyTsAuthWebhook(secret, timestamp, unicodeSignature, tampered),
      false,
    );
  });

  test("F55 two different secrets produce different signatures with no cross-verification", async () => {
    const other = `whsec_${"j".repeat(40)}`;
    const otherSignature = sign(other, timestamp, payload);
    assert.notEqual(signature, otherSignature);
    assert.equal(
      await verifyTsAuthWebhook(secret, timestamp, signature, payload),
      true,
    );
    assert.equal(
      await verifyTsAuthWebhook(other, timestamp, otherSignature, payload),
      true,
    );
    assert.equal(
      await verifyTsAuthWebhook(other, timestamp, signature, payload),
      false,
    );
    assert.equal(
      await verifyTsAuthWebhook(secret, timestamp, otherSignature, payload),
      false,
    );
  });
});

describe("ts-auth client edge cases: G. configuration edges", () => {
  /** @returns {Promise<Response>} */
  const neverFetch = () => Promise.reject(new Error("no HTTP expected"));

  test("G56 userinfo and secret-bearing query origins are construction errors; path, query, and fragment are rejected", async () => {
    assert.throws(
      () =>
        createTsAuthClient({
          apiBaseUrl: "https://user:pass@auth.acme.test",
          accessToken: "sk_x",
          instanceSlug: "acme",
          fetch: neverFetch,
        }),
      /credentials/u,
    );
    assert.throws(
      () =>
        createTsAuthClient({
          apiBaseUrl: "https://auth.acme.test/hooks?token=x",
          accessToken: "sk_x",
          instanceSlug: "acme",
          fetch: neverFetch,
        }),
      /secret-bearing/u,
    );

    // Mirrors the ts-auth service rule: the deployment origin is a bare origin.
    for (const base of [
      "https://auth.acme.test/prefix",
      "https://auth.acme.test?limit=5",
      "https://auth.acme.test/#frag",
    ]) {
      assert.throws(
        () =>
          createTsAuthClient({
            apiBaseUrl: base,
            accessToken: "sk_x",
            instanceSlug: "acme",
            fetch: neverFetch,
          }),
        /bare origin/u,
        base,
      );
    }
  });

  test("G56 a missing or blank apiBaseUrl is a construction error; there is no default origin", () => {
    for (const apiBaseUrl of [undefined, "", "   "]) {
      assert.throws(
        () =>
          createTsAuthClient({
            apiBaseUrl: /** @type {string} */ (
              /** @type {unknown} */ (apiBaseUrl)
            ),
            accessToken: "sk_x",
            instanceSlug: "acme",
            fetch: neverFetch,
          }),
        /apiBaseUrl/u,
        JSON.stringify(apiBaseUrl),
      );
    }
  });

  test("G57 plain-http origins are allowed only for loopback or .test with an explicit test transport", () => {
    for (const apiBaseUrl of [
      "http://localhost:9000",
      "http://127.0.0.1",
      "http://[::1]:1234",
      "http://auth.acme.test",
      "http://test",
    ]) {
      const client = createTsAuthClient({
        apiBaseUrl,
        accessToken: "sk_x",
        instanceSlug: "acme",
        fetch: neverFetch,
      });
      assert.ok(client);
    }
    assert.throws(
      () =>
        createTsAuthClient({
          apiBaseUrl: "http://auth.example.com",
          accessToken: "sk_x",
          instanceSlug: "acme",
          fetch: neverFetch,
        }),
      /HTTPS/u,
    );
    // .test over plain http without an explicit test transport is still rejected.
    assert.throws(
      () =>
        createTsAuthClient({
          apiBaseUrl: "http://auth.acme.test",
          accessToken: "sk_x",
          instanceSlug: "acme",
        }),
      /HTTPS/u,
    );
  });

  test("G58 base URLs with and without trailing slashes behave identically", async () => {
    for (const apiBaseUrl of [
      "https://auth.acme.test",
      "https://auth.acme.test/",
      "https://auth.acme.test///",
    ]) {
      /** @type {Request[]} */
      const requests = [];
      const tsAuth = tsAuthClient(() => json({ data: [] }), requests, {
        apiBaseUrl,
      });
      await tsAuth.users.list();
      assert.equal(
        /** @type {Request} */ (requests[0]).url,
        "https://auth.acme.test/v1/acme/users",
        apiBaseUrl,
      );
    }
  });

  test("G60 mutating the config record after construction does not affect the client", async () => {
    /** @type {Request[]} */
    const requests = [];
    const config = {
      apiBaseUrl: "https://auth.acme.test",
      accessToken: "sk_original",
      instanceSlug: "acme",
      fetch: capture(requests, () => json({ data: [] })),
    };
    const tsAuth = createTsAuthClient(config);
    const mutable = /** @type {Record<string, unknown>} */ (config);
    mutable.apiBaseUrl = "https://elsewhere.test";
    mutable.instanceSlug = "other";
    mutable.accessToken = "sk_mutated";
    const listed = await tsAuth.users.list();
    assert.equal(listed.ok, true);
    assert.equal(
      /** @type {Request} */ (requests[0]).url,
      "https://auth.acme.test/v1/acme/users",
    );
    assert.equal(
      /** @type {Request} */ (requests[0]).headers.get("authorization"),
      "Bearer sk_original",
    );
  });
});

// Hostile response controls intentionally return invalid envelopes. They do
// not provide successful provider behavior or qualification evidence.
test("resource introspection rejects missing, extra, misbound and unhealthy principal claims", async () => {
  const credentials = {
    issuer: "https://issuer.example.test/example",
    audience: "https://api.example.test/management",
    introspection_client_id: "01ARZ3NDEKTSV4RRFFQ6000003",
    introspection_secret: "secret",
  };
  const now = Math.floor(Date.now() / 1000);
  const claims = {
    active: true,
    token_type: "Bearer",
    iss: credentials.issuer,
    sub: "01ARZ3NDEKTSV4RRFFQ6000001",
    aud: credentials.audience,
    client_id: "01ARZ3NDEKTSV4RRFFQ6000002",
    scope: "management:read",
    exp: now + 60,
    iat: now - 10,
    sid: "01ARZ3NDEKTSV4RRFFQ6000004",
    principal_kind: "user",
  };
  const withoutSid = Object.fromEntries(
    Object.entries(claims).filter(([key]) => key !== "sid"),
  );
  for (const hostile of [
    withoutSid,
    { ...claims, aud: [credentials.audience] },
    { ...claims, aud: "https://wrong.example.test" },
    { ...claims, iss: "https://wrong.example.test/example" },
    { ...claims, exp: now },
    { ...claims, exp: now + 0.5 },
    { ...claims, iat: now + 60 },
    { ...claims, sid: null },
    { ...claims, sid: "" },
    { ...claims, principal_kind: "service", sid: "01ARZ3NDEKTSV4RRFFQ6000004" },
    { ...claims, principal_kind: "service", sid: null, sub: "wrong-service" },
    { ...claims, principal_kind: "unknown" },
    { ...claims, family_id: "internal-family" },
    { ...claims, resource_auth_epoch: 1 },
    { ...claims, token_use: "access" },
    { active: false, sub: "retained-principal" },
    { active: "false" },
    { ...claims, exp: Number.MAX_SAFE_INTEGER + 1 },
  ]) {
    const client = createTsAuthClient({
      apiBaseUrl: "https://auth.example.test",
      instanceSlug: "example",
      fetch: async () => Response.json(hostile),
    });
    const result = await client.oidc.introspectResource(credentials, "opaque");
    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.error.kind, "invalid-response");
      assert.equal(result.body, undefined);
    }
  }
});

test("OAuth policy decoding denies lossy epochs and wrong rotation response branches", async () => {
  // Missing required view fields remain invalid even when an epoch looks valid.
  const malformed = [
    { auth_epoch: Number.MAX_SAFE_INTEGER + 1 },
    { clientId: "obsolete-camel-case" },
    { client: {}, client_secret: "rotated" },
  ];
  for (const body of malformed) {
    const client = createTsAuthClient({
      apiBaseUrl: "https://auth.example.test",
      instanceSlug: "example",
      fetch: async () => Response.json(body),
    });
    assert.equal((await client.oauthClients.update("01ARZ3NDEKTSV4RRFFQ69G5FAV", {})).ok, false);
    assert.equal(
      (await client.oauthClients.update("01ARZ3NDEKTSV4RRFFQ69G5FAV", { rotate_secret: true })).ok,
      false,
    );
    assert.equal(
      (await client.oauthResources.update("01ARZ3NDEKTSV4RRFFQ69G5FAV", { expected_auth_epoch: 1 })).ok,
      false,
    );
  }
});
