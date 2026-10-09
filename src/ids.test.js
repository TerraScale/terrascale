import { afterEach, expect, test, vi } from "vitest";
import { generateId, isValidId, requireId } from "./ids.js";
import { decodeDocument, decodeWrite } from "./v1/decode.js";
import { requireDocumentId, requireProject } from "./v1/ids.js";
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
  const version = "opaque-version";
  for (const value of invalidIds) {
    expect(isValidId(value)).toBe(false);
    for (const validate of [requireId, requireProject, requireDocumentId])
      expect(() => validate(value)).toThrow(TypeError);
    expect(() => decodeDocument({ id: /** @type {any} */ (value), version, state: "value", document: {} })).toThrow(TypeError);
    expect(() => decodeWrite({ status: 201, headers: new Headers(), value: { id: /** @type {any} */ (value), version, offset: "1", durability: "local_durable" } })).toThrow(TypeError);
    expect(() => decodeShapePage({ shape_id: "0".repeat(32), handle: `h1-${"0".repeat(16)}`, offset: "-1", next_offset: "1", up_to_date: true,
      changes: [{ offset: "1", ops: [{ op: "delete", id: /** @type {any} */ (value) }] }] }, new Headers())).toThrow(TypeError);
  }
});

test("snapshot continuation IDs reject legacy keys instead of normalizing them", () => {
  expect(requireShapeOffset(`snap.1.${btoa(id).replace(/=+$/u, "")}`)).toContain("snap.1.");
  for (const invalid of ["legacy", id.toLowerCase(), `org_${id}`, "\uFEFFlegacy"]) {
    const encoded = btoa(String.fromCharCode(...new TextEncoder().encode(invalid))).replace(/=+$/u, "");
    expect(() => requireShapeOffset(`snap.1.${encoded}`)).toThrow(TypeError);
  }
});

test("invalid document and transaction IDs fail before any endpoint resolution", async () => {
  const resolver = vi.fn(() => "https://database.example");
  const database = createTerraBaseClient({ project: id, token: `tb_${"a".repeat(43)}`, endpointResolver: resolver }).database("app");
  const documents = database.collection("users");
  for (const invalid of [id.toLowerCase(), `usr_${id}`, "019a0000-0000-7000-8000-000000000002"]) {
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

test("the document key is the only entity ID, including patches and transactions", async () => {
  const resolver = vi.fn(() => "https://database.example");
  const database = createTerraBaseClient({ project: id, token: `tb_${"a".repeat(43)}`, endpointResolver: resolver }).database("app");
  const documents = database.collection("users");
  await expect(documents.put(id, { id })).rejects.toThrow(/document key/u);
  await expect(documents.create({ id })).rejects.toThrow(/document key/u);
  for (const patch of [{ merge: { id } }, { ops: [{ op: /** @type {const} */ ("set"), path: "/id", value: id }] },
    { ops: [{ op: /** @type {const} */ ("set"), path: "", value: { id } }] }]) {
    await expect(documents.patch(id, patch)).rejects.toThrow(/document key/u);
    expect(() => database.transaction().patch("users", id, patch)).toThrow(/document key/u);
  }
  expect(() => database.transaction().put("users", id, { id })).toThrow(/document key/u);
  expect(() => decodeDocument({ id, version: "opaque", state: "value", document: { id } })).toThrow(/document key/u);
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

test("single hosted-auth entities require their ID without imposing it on acknowledgments or wrappers", () => {
  for (const route of ["tsauth.organizations.get", "tsauth.organizations.create", "tsauth.organizations.update",
    "tsauth.instance.get", "tsauth.instance.update", "tsauth.apiKeys.create", "tsauth.directories.rotate"])
    for (const value of [{}, null, [], "legacy-id", { id: undefined }, { id: "org_legacy" }])
      expect(() => validateTsAuthIds(value, route)).toThrow(TypeError);
  expect(() => validateTsAuthIds({ replayed: true }, "tsauth.webhooks.replayDeadLetter")).not.toThrow();
  expect(() => validateTsAuthIds({ data: [{ id }] }, "tsauth.users.list")).not.toThrow();
  expect(() => validateTsAuthIds({ client: { id }, client_secret: null }, "tsauth.oauthClients.create")).not.toThrow();
});

test("conflict siblings obey the same key/payload separation as the winning document", () => {
  const base = { id, version: "opaque", state: "conflict", document: { name: "winner" } };
  const siblings = [
    { deleted: false, version: "other", document: { name: "sibling", metadata: { id: "customer-key" } }, future: true },
    { deleted: true, event: { replica: "replica", incarnation: "incarnation", sequence: "14" } },
  ];
  expect(decodeDocument({ ...base, siblings }).siblings).toEqual(siblings);
  for (const deleted of [false, undefined, "true"])
    expect(() => decodeDocument({ ...base, siblings: [{ deleted: /** @type {any} */ (deleted) }] })).toThrow(TypeError);
  for (const body of [{ id: "legacy" }, { id }, null, []])
    for (const deleted of [false, true])
      expect(() => decodeDocument({ ...base, siblings: [{ deleted, document: /** @type {any} */ (body) }] })).toThrow(TypeError);
});

test("wrapped OAuth responses validate entity and reference ULIDs", () => {
  for (const route of ["tsauth.oauthClients.create", "tsauth.oauthClients.update", "tsauth.oauthResources.create", "tsauth.oauthResources.update"]) {
    const field = route.startsWith("tsauth.oauthClients.") ? "client" : "resource";
    const reference = field === "client" ? "client_id" : "introspection_client_id";
    const entity = { id, [reference]: id, public_metadata: { id: "customer-key" } };
    expect(() => validateTsAuthIds({ [field]: entity }, route)).not.toThrow();
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
