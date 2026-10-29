import { afterEach, expect, test, vi } from "vitest";
import { generateId, isValidId, requireId } from "./ids.js";
import { decodeDocument, decodeWrite } from "./v1/decode.js";
import { requireDocumentId, requireProject, requireStoredId } from "./v1/ids.js";
import { decodeShapePage, requireShapeOffset } from "./v1/shapes.js";
import { createTerraBaseClient } from "./index.js";
import { requireDeadLetterId, validateTsAuthIds } from "./ts-auth-ids.js";

afterEach(() => vi.useRealTimers());

test("ULID generation is unique and monotonic at one millisecond and after clock rollback", () => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date("2030-01-01T00:00:00Z"));
  const ids = Array.from({ length: 1000 }, () => generateId());
  expect(ids.every(isValidId)).toBe(true);
  expect(new Set(ids).size).toBe(ids.length);
  expect([...ids].sort()).toEqual(ids);
  expect(new Set(ids.map(id => id.slice(0, 10))).size).toBe(1);
  vi.setSystemTime(new Date("2029-12-31T00:00:00Z"));
  expect(generateId() > /** @type {string} */ (ids.at(-1))).toBe(true);
});

const id = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const invalidIds = [undefined, null, 1, "", id.toLowerCase(), `org_${id}`,
  "019a0000-0000-7000-8000-000000000002", id.slice(1), `${id}0`,
  `8${id.slice(1)}`, `I${id.slice(1)}`, `O${id.slice(1)}`, `U${id.slice(1)}`, `${id}\n`];

test("only canonical 128-bit ULIDs pass every entity ID boundary", () => {
  for (const value of [id, "00000000000000000000000000", "7ZZZZZZZZZZZZZZZZZZZZZZZZZ"]) {
    expect(isValidId(value)).toBe(true);
    expect(requireId(value)).toBe(value);
  }
  for (const value of invalidIds) {
    expect(isValidId(value)).toBe(false);
    for (const validate of [requireId, requireProject])
      expect(() => validate(value)).toThrow(TypeError);
  }
});

test("document boundaries reject malformed IDs", () => {
  const version = "opaque-version";
  for (const value of [undefined, null, 1, "", "\u0000", "\u0085", "\ud800", "\udc00", "a".repeat(257)]) {
    expect(() => requireDocumentId(value)).toThrow(TypeError);
    expect(() => decodeDocument({ id: /** @type {any} */ (value), version, state: "value", document: {} })).toThrow(TypeError);
    expect(() => decodeWrite({ status: 201, headers: new Headers(), value: { id: /** @type {any} */ (value), version, offset: "1", durability: "local_durable" } })).toThrow(TypeError);
    expect(() => decodeShapePage({ shape_id: "0".repeat(32), handle: `h1-${"0".repeat(16)}`, offset: "-1", next_offset: "1", up_to_date: true,
      changes: [{ offset: "1", ops: [{ op: "delete", id: /** @type {any} */ (value) }] }] }, new Headers())).toThrow(TypeError);
  }
});

test("snapshot continuation keys reject invalid bounds and controls without normalizing them", () => {
  expect(requireShapeOffset(`snap.1.${btoa(id).replace(/=+$/u, "")}`)).toContain("snap.1.");
  for (const invalid of ["\u0000", "\u0085", "a".repeat(257)]) {
    const encoded = btoa(String.fromCharCode(...new TextEncoder().encode(invalid))).replace(/=+$/u, "");
    expect(() => requireShapeOffset(`snap.1.${encoded}`)).toThrow(TypeError);
  }
});

test("invalid document and transaction IDs fail before any endpoint resolution", async () => {
  const resolver = vi.fn(() => "https://database.example");
  const database = createTerraBaseClient({ project: id, token: `tb_${"a".repeat(43)}`, endpointResolver: resolver }).database("app");
  const documents = database.collection("users");
  for (const invalid of ["", "\ud800", "\u007f", "a".repeat(257)]) {
    await expect(documents.get(invalid)).rejects.toThrow(TypeError);
    await expect(documents.put(invalid, {})).rejects.toThrow(TypeError);
    await expect(documents.patch(invalid, { merge: {} })).rejects.toThrow(TypeError);
    await expect(documents.delete(invalid)).rejects.toThrow(TypeError);
    for (const operation of ["create", "put", "delete", "assertAbsent"]) {
      const transaction = database.transaction();
      expect(() => operation === "create" ? transaction.create("users", {}, invalid) :
        operation === "put" ? transaction.put("users", invalid, {}) :
        operation === "delete" ? transaction.delete("users", invalid) : transaction.assertAbsent("users", invalid)).toThrow(TypeError);
    }
  }
  expect(resolver).not.toHaveBeenCalled();
});

test("document data may contain an unrelated id; the producer owns key normalization", () => {
  const resolver = vi.fn(() => "https://database.example");
  const database = createTerraBaseClient({ project: id, token: `tb_${"a".repeat(43)}`, endpointResolver: resolver }).database("app");
  for (const patch of [{ merge: { id: "customer-data" } },
    { ops: [{ op: /** @type {const} */ ("set"), path: "/id", value: "customer-data" }] }]) {
    expect(() => database.transaction().patch("users", id, patch)).not.toThrow();
  }
  expect(() => database.transaction().put("users", id, { id })).not.toThrow();
  expect(decodeDocument({ id, version: "opaque", state: "value", document: { id: "customer-data" } }).document)
    .toEqual({ id: "customer-data" });
  expect(resolver).not.toHaveBeenCalled();
});

test("hosted-auth validates entity references and leaves customer metadata untouched", () => {
  expect(() => validateTsAuthIds({ data: [{ id, user_id: id, organization: { id }, public_metadata: { id: "customer-key" } }] }, "tsauth.organizations.list")).not.toThrow();
  for (const invalid of invalidIds) {
    expect(() => validateTsAuthIds({ id: invalid }, "tsauth.users.get")).toThrow(TypeError);
    expect(() => validateTsAuthIds({ data: [{ id, user_id: invalid }] }, "tsauth.organizations.list")).toThrow(TypeError);
  }
  expect(() => validateTsAuthIds({ id, allowed_client_ids: ["client_1"] }, "tsauth.oauthResources.list")).toThrow(TypeError);
  expect(() => validateTsAuthIds({ data: [{ email: "user@example.test" }] }, "tsauth.users.list")).toThrow(TypeError);
  expect(() => validateTsAuthIds({ email: "user@example.test" }, "tsauth.oidc.userinfo")).toThrow(TypeError);
});

test("hosted-auth entity pages reject primitives and nested arrays while recovery codes remain strings", () => {
  for (const entry of [null, "legacy-id", 1, true, [], [ { id } ], undefined]) {
    expect(() => validateTsAuthIds({ data: [entry] }, "tsauth.users.list")).toThrow(TypeError);
    if (entry !== null)
      expect(() => validateTsAuthIds({ user: entry }, "tsauth.organizations.listMemberships")).toThrow(TypeError);
  }
  expect(() => validateTsAuthIds({ passkeys: [{ id }] }, "tsauth.users.passkeys")).not.toThrow();
  expect(() => validateTsAuthIds({ data: ["recovery-code"] }, "tsauth.users.regenerateRecoveryCodes")).not.toThrow();
  for (const entry of [null, 1, true, {}, []])
    expect(() => validateTsAuthIds({ data: [entry] }, "tsauth.users.regenerateRecoveryCodes")).toThrow(TypeError);
});

test("enrollment and userinfo require an object with their canonical reference ID", () => {
  for (const { route, field } of [{ route: "tsauth.organizations.enrollDomain", field: "org_id" },
    { route: "tsauth.oidc.userinfo", field: "sub" }]) {
    expect(() => validateTsAuthIds({ [field]: id }, route)).not.toThrow();
    for (const invalid of [...invalidIds, [], [{ id, sub: id, org_id: id }], {}])
      expect(() => validateTsAuthIds(invalid, route)).toThrow(TypeError);
    for (const invalid of invalidIds)
      expect(() => validateTsAuthIds({ [field]: invalid }, route)).toThrow(TypeError);
  }
});

test("only connection and directory routes allow a null organization reference", () => {
  for (const route of ["tsauth.connections.create", "tsauth.connections.update", "tsauth.connections.list",
    "tsauth.directories.create", "tsauth.directories.rotate", "tsauth.directories.list"])
    expect(() => validateTsAuthIds({ id, org_id: null }, route)).not.toThrow();
  for (const route of ["tsauth.users.get", "tsauth.organizations.get", "tsauth.organizations.enrollDomain"])
    expect(() => validateTsAuthIds({ id, org_id: null }, route)).toThrow(TypeError);
});

test("single hosted-auth entities require their ID without imposing it on acknowledgments or wrappers", () => {
  for (const route of ["tsauth.organizations.get", "tsauth.organizations.create", "tsauth.organizations.update",
    "tsauth.instance.get", "tsauth.instance.update", "tsauth.apiKeys.create", "tsauth.directories.rotate"])
    for (const value of [{}, null, [], "legacy-id", { id: undefined }, { id: "org_legacy" }])
      expect(() => validateTsAuthIds(value, route)).toThrow(TypeError);
  expect(() => validateTsAuthIds({ replayed: true }, "tsauth.webhooks.replayDeadLetter")).not.toThrow();
  expect(() => validateTsAuthIds({ data: [{ id }] }, "tsauth.users.list")).not.toThrow();
  expect(() => validateTsAuthIds({ client: { id, client_id: id }, client_secret: null }, "tsauth.oauthClients.create")).not.toThrow();
});

test("hosted-auth entity references are required on mutations and page entries", () => {
  const schemas = [
    { routes: ["createMembership", "updateMembership", "listMemberships"].map(name => `tsauth.organizations.${name}`),
      references: { organization_id: id, user_id: id } },
    { routes: ["createInvitation", "revokeInvitation", "listInvitations", "createDomain", "listDomains"].map(name => `tsauth.organizations.${name}`),
      references: { organization_id: id } },
    { routes: ["tsauth.auditEvents.list"], references: { instance_id: id } },
    { routes: ["tsauth.webhooks.deadLetter"], references: { endpoint_id: id, event_id: id } },
    { routes: ["create", "update", "list"].map(name => `tsauth.oauthClients.${name}`), references: { client_id: id } },
    { routes: ["create", "update", "list"].map(name => `tsauth.oauthResources.${name}`), references: { introspection_client_id: id } },
    { routes: ["tsauth.connections.create", "tsauth.connections.update", "tsauth.connections.list",
      "tsauth.directories.create", "tsauth.directories.rotate", "tsauth.directories.list"], references: { org_id: null } },
  ];
  for (const { routes, references } of schemas) {
    for (const route of routes) {
      const entityId = route === "tsauth.webhooks.deadLetter" ? `${id}:${id}` : id;
      const body = { id: entityId, ...references };
      const page = route.endsWith(".list") || route.endsWith(".deadLetter") || /\.list[A-Z]/u.test(route);
      const wrap = (/** @type {unknown} */ value) => page ? [value] : value;
      expect(() => validateTsAuthIds(wrap(body), route)).not.toThrow();
      for (const [field, reference] of Object.entries(references)) {
        const omitted = Object.fromEntries(Object.entries(body).filter(([key]) => key !== field));
        expect(() => validateTsAuthIds(wrap(omitted), route)).toThrow(TypeError);
        for (const invalid of invalidIds) {
          if (invalid === null && reference === null) continue;
          expect(() => validateTsAuthIds(wrap({ ...body, [field]: invalid }), route)).toThrow(TypeError);
        }
      }
    }
  }
  const membership = { id, organization_id: id, user_id: id, user: { id } };
  expect(() => validateTsAuthIds([membership], "tsauth.organizations.listMemberships")).not.toThrow();
  expect(() => validateTsAuthIds({ data: [{ id, instance_id: id, data: { id: "customer-key" } }] }, "tsauth.auditEvents.list")).not.toThrow();
  expect(() => validateTsAuthIds({ data: [{ id }] }, "tsauth.auditEvents.list")).toThrow(TypeError);
});

test("conflict siblings retain unrelated id data and validate document object shapes", () => {
  const base = { id, version: "opaque", state: "conflict", document: { name: "winner" } };
  const siblings = [
    { deleted: false, version: "other", document: { name: "sibling", metadata: { id: "customer-key" } }, future: true },
    { deleted: true, event: { replica: "replica", incarnation: "incarnation", sequence: "14" } },
  ];
  expect(decodeDocument({ ...base, siblings }).siblings).toEqual(siblings);
  for (const deleted of [false, undefined, "true"])
    expect(() => decodeDocument({ ...base, siblings: [{ deleted: /** @type {any} */ (deleted) }] })).toThrow(TypeError);
  for (const body of [{ id: "customer-data" }, { id }])
    expect(decodeDocument({ ...base, siblings: [{ document: body }] }).siblings).toEqual([{ document: body }]);
  for (const body of [null, []])
    for (const deleted of [false, true])
      expect(() => decodeDocument({ ...base, siblings: [{ deleted, document: /** @type {any} */ (body) }] })).toThrow(TypeError);
});

test("wrapped OAuth responses validate entity and reference ULIDs", () => {
  for (const route of ["tsauth.oauthClients.create", "tsauth.oauthClients.update", "tsauth.oauthResources.create", "tsauth.oauthResources.update"]) {
    const field = route.startsWith("tsauth.oauthClients.") ? "client" : "resource";
    const reference = field === "client" ? "client_id" : "introspection_client_id";
    const entity = { id, [reference]: id, public_metadata: { id: "customer-key" } };
    expect(() => validateTsAuthIds({ [field]: entity }, route)).not.toThrow();
    const omittedReference = Object.fromEntries(Object.entries(entity).filter(([key]) => key !== reference));
    expect(() => validateTsAuthIds({ [field]: omittedReference }, route)).toThrow(TypeError);
    for (const invalid of invalidIds) {
      expect(() => validateTsAuthIds({ [field]: { ...entity, id: invalid } }, route)).toThrow(TypeError);
      expect(() => validateTsAuthIds({ [field]: { ...entity, [reference]: invalid } }, route)).toThrow(TypeError);
    }
    for (const malformed of [null, [], {}, "legacy-id"])
      expect(() => validateTsAuthIds({ [field]: malformed }, route)).toThrow(TypeError);
  }
});

test("dead-letter selectors contain exactly two canonical ULID entity references", () => {
  const event = "01ARZ3NDEKTSV4RRFFQ6000002";
  const selector = `${id}:${event}`;
  expect(requireDeadLetterId(selector)).toBe(selector);
  expect(() => validateTsAuthIds([{ id: selector, endpoint_id: id, event_id: event }], "tsauth.webhooks.deadLetter")).not.toThrow();
  for (const invalid of [...invalidIds, selector.toLowerCase(), `${selector}:${id}`, `${id}:`, `:${event}`, `${id}:event_1`]) {
    expect(() => requireDeadLetterId(invalid)).toThrow(TypeError);
    expect(() => validateTsAuthIds([{ id: invalid, endpoint_id: id, event_id: event }], "tsauth.webhooks.deadLetter")).toThrow(TypeError);
  }
  expect(() => validateTsAuthIds([{ id: selector }], "tsauth.users.list")).toThrow(TypeError);
});

test("document inputs require ULIDs while stored SQL IDs remain readable", async () => {
  const version = "opaque-version";
  const stored = "a/b+é";
  expect(() => requireDocumentId(stored)).toThrow(TypeError);
  expect(requireStoredId(stored)).toBe(stored);
  expect(decodeDocument({ id: stored, version, state: "value", document: {} }).id).toBe(stored);
  expect(() => decodeWrite({ status: 201, headers: new Headers(), value: { id: stored, version, offset: "1", durability: "local_durable" } })).toThrow(TypeError);
  let requests = 0;
  const client = createTerraBaseClient({ project: id, token: `tb_${"a".repeat(43)}`, baseUrl: "https://fixture.invalid", fetch: async () => {
    requests++;
    throw new Error("invalid document ID reached HTTP");
  } });
  const collection = client.database("app").collection("notes");
  await expect(collection.get(stored)).rejects.toThrow(TypeError);
  await expect(collection.put(stored, {})).rejects.toThrow(TypeError);
  expect(requests).toBe(0);
});
