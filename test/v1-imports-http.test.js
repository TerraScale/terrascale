import { expect, test } from "vitest";
import ts from "typescript";
import { entity, fixture, json, prefix, project } from "./helpers/v1-http.js";
const input = { format: /** @type {const} */ ("sqlite"), sourceBytes: 4, sourceSha256: "a".repeat(64) };
const at = "2026-10-22T10:00:00Z";
const job = { id: entity, project, database: "app", region: "gru", state: "awaiting_source", cancelRequested: false,
  options: { exclude: [], collection: null }, source: { format: "sqlite", bytes: 4, sha256: input.sourceSha256, receivedBytes: 0, staged: false, retainedUntil: null },
  preview: null, confirmedPreviewId: null, progress: null, validation: null, error: null, createdAt: at, updatedAt: at, startedAt: null, finishedAt: null };
test("all seven import routes preserve binary chunks, offsets, preview confirmation and 202 jobs", async () => {
  const { client, requests } = await fixture((_r, response) => json(response, job, [201, 200, 200, 202, 202, 202, 202][requests.length - 1]));
  const imports = client.database("app").imports;
  expect(await imports.create(input)).toEqual(job);
  expect(await imports.get()).toEqual(job);
  const bytes = new Uint8Array([0, 127, 128, 255]);
  expect(await imports.uploadSource(0n, bytes)).toEqual(job);
  expect(await imports.update({ options: { exclude: ["public.secret"] } })).toEqual(job);
  expect(await imports.start({ previewId: entity })).toEqual(job);
  expect(await imports.cancel()).toEqual(job);
  expect(await imports.resume()).toEqual(job);
  const base = `${prefix}/databases/app/import`;
  expect(requests.map(r => [r.method, r.url.pathname])).toEqual([
    ["POST", base], ["GET", base], ["PUT", `${base}/source`], ["PATCH", base], ["POST", `${base}/start`], ["POST", `${base}/cancel`], ["POST", `${base}/resume`],
  ]);
  expect(requests[2]?.headers["content-type"]).toBe("application/octet-stream");
  expect(requests[2]?.headers["content-length"]).toBe("4");
  expect(Array.from(requests[2]?.bytes ?? [])).toEqual([0, 127, 128, 255]);
  expect(requests[2]?.url.search).toBe("?offset=0");
  expect(requests[4]?.body).toBe(JSON.stringify({ previewId: entity }));
});
const preview = { id: entity, tables: [], skipped: [], skippedTruncated: false, blocking: 0, warnings: 0 };
test("import decodes generated preview and confirmed preview ULIDs", async () => {
  const response = { ...job, preview, confirmedPreviewId: entity };
  const { client } = await fixture((_r, reply) => json(reply, response));
  expect(await client.database("app").imports.get()).toEqual(response);
});
test.each(["preview-sha256", `preview_${entity}`, entity.toLowerCase(), "8".repeat(26)])("import refuses noncanonical preview ID %s before dispatch", async id => {
  const { client, requests } = await fixture((_r, response) => json(response, job, 202));
  await expect(client.database("app").imports.start({ previewId: id })).rejects.toThrow(TypeError);
  expect(requests).toHaveLength(0);
});
test.each([
  { preview: { ...preview, id: "preview-sha256" }, confirmedPreviewId: null },
  { preview, confirmedPreviewId: "preview-sha256" },
])("import rejects invalid preview response identities %j", async fields => {
  const { client, requests } = await fixture((_r, response) => json(response, { ...job, ...fields }));
  await expect(client.database("app").imports.get()).rejects.toThrow(TypeError);
  expect(requests).toHaveLength(1);
});
test.each([400, 401, 403, 404, 409, 413, 415, 421, 429, 503])("import failure %i is never replayed", async status => {
  const { client, requests } = await fixture((_r, response) => json(response, { error: { code: "offset_mismatch", message: "Refused" } }, status));
  await expect(client.database("app").imports.uploadSource(0, new Uint8Array([1]))).rejects.toMatchObject({ status, code: "offset_mismatch" });
  expect(requests).toHaveLength(1);
});
test("import bounds and pre-cancelled calls fail without dispatch", async () => {
  const { client, requests } = await fixture((_r, response) => json(response, job));
  const imports = client.database("app").imports;
  await expect(imports.create({ ...input, sourceBytes: 0 })).rejects.toThrow(TypeError);
  await expect(imports.uploadSource(-1, new Uint8Array([1]))).rejects.toThrow(TypeError);
  await expect(imports.uploadSource(0, new Uint8Array(4194305))).rejects.toThrow(TypeError);
  await expect(imports.start({ previewId: "" })).rejects.toThrow(TypeError);
  await expect(imports.get({ signal: AbortSignal.abort("stop") })).rejects.toBe("stop");
  expect(requests).toHaveLength(0);
});
/** @type {{readonly options?: import("../src/v1/imports.js").V1ImportOptions | null}[]} */
const defaultRequests = [{}, { options: null }];
test.each(defaultRequests)("import options can reset to producer defaults with %j", async request => {
  const { client, requests } = await fixture((_r, response) => json(response, job, 202));
  expect(await client.database("app").imports.update(request)).toEqual(job);
  expect(requests[0]?.method).toBe("PATCH");
  expect(JSON.parse(requests[0]?.body ?? "")).toEqual(request);
});

// Response DTOs from producer 841dd0d99 ImportModels.cs, not a producer qualification substitute.
test("current preview and completed/error DTOs decode without removed legacy fields", async () => {
  const table = { source: "public.q11_rows", collection: "q11_rows", excluded: false, rows: 2, primaryKey: ["id"],
    columns: [{ name: "id", sourceType: "text", mapping: "string", nullable: false, excluded: false }],
    constraints: [{ kind: "primary_key", name: null, columns: ["id"], references: null, enforced: false }],
    issues: [{ severity: "warning", code: "constraint_not_enforced", column: null, row: null, message: "Recorded only" }] };
  const currentPreview = { ...preview, tables: [table] };
  const completed = { ...job, state: "completed", preview: currentPreview, confirmedPreviewId: entity,
    progress: { tablesDone: 1, tablesTotal: 1, rowsWritten: 2, rowsTotal: 2, currentTable: null },
    validation: { passed: true, tables: [{ source: table.source, collection: table.collection, sourceRows: 2, documents: 2,
      sourceChecksum: "a".repeat(64), documentChecksum: "a".repeat(64), match: true }] }, startedAt: at, finishedAt: at };
  const failed = { ...job, state: "failed", options: { exclude: null, collection: "q11_rows" },
    error: { code: "malformed_source", message: "Bad line", retryable: false, table: null, row: null, column: null } };
  const responses = [{ ...job, state: "ready", preview: currentPreview }, completed, failed];
  const { client, requests } = await fixture((_r, reply) => json(reply, responses[requests.length - 1]));
  for (const response of responses) expect(await client.database("app").imports.get()).toEqual(response);
});

test.each(["postgres_plain", "sqlite", "ndjson"])("create sends the exact %s request and current options", async format => {
  const request = { ...input, format, options: { collection: "q11_rows", exclude: ['"my.schema"."a""b"', "public.rows.note"] } };
  const response = { ...job, options: request.options, source: { ...job.source, format } };
  const { client, requests } = await fixture((_r, reply) => json(reply, response, 201));
  expect(await client.database("app").imports.create(/** @type {import("../src/v1/imports.js").V1CreateImportRequest} */ (request))).toEqual(response);
  expect(JSON.parse(requests[0]?.body ?? "")).toEqual(request);
  expect(requests[0]?.headers["content-type"]).toBe("application/json");
});

test.each([{}, { exclude: null, collection: null }, { exclude: [], collection: "a".repeat(63) }])("update preserves legitimate optional/null options %j", async options => {
  const { client, requests } = await fixture((_r, reply) => json(reply, job, 202));
  await client.database("app").imports.update({ options });
  expect(JSON.parse(requests[0]?.body ?? "")).toEqual({ options });
});

test.each([
  { missingPrimaryKey: "generate" }, { sourceRef: "anything" }, { collection: 1 }, { collection: "Uppercase" },
  { collection: "a".repeat(64) }, { collection: "sqlite_schema" }, { collection: "turso_rows" },
  { collection: "collections" }, { collection: "commit_offset" }, { collection: "terrascale-import" },
  { exclude: "public.rows" }, { exclude: [null] }, { exclude: [1] }, { exclude: [""] }, { exclude: ["table_only"] },
  { exclude: ["public.rows."] }, { exclude: ["public.rows.col.extra"] }, { exclude: ['public."unfinished'] },
  { exclude: ['public.""'] }, { exclude: ['public."rows"\n'] }, { exclude: ["public." + "a".repeat(506)] },
  { exclude: Array(1001).fill("public.rows") }, { exclude: Array(1000).fill("public." + "a".repeat(59)) },
  [], "options", 1,
])("create/update reject unsupported or malformed options before dispatch %#", async options => {
  const { client, requests } = await fixture((_r, reply) => json(reply, job));
  const imports = client.database("app").imports;
  await expect(imports.create({ ...input, options: /** @type {never} */ (options) })).rejects.toThrow(TypeError);
  await expect(imports.update({ options: /** @type {never} */ (options) })).rejects.toThrow(TypeError);
  expect(requests).toHaveLength(0);
});

test.each([
  { format: "csv" }, { sourceBytes: 1073741825 }, { sourceBytes: 1.5 }, { sourceSha256: "A".repeat(64) }, { sourceRef: "unsupported" },
])("create rejects invalid format/integrity/unknown members before dispatch %j", async fields => {
  const { client, requests } = await fixture((_r, reply) => json(reply, job));
  await expect(client.database("app").imports.create(/** @type {never} */ ({ ...input, ...fields }))).rejects.toThrow(TypeError);
  expect(requests).toHaveLength(0);
});

test.each([
  { exclude: [], collection: 1 }, { exclude: [], collection: undefined }, { exclude: "not-an-array", collection: null },
  { exclude: undefined, collection: null }, { exclude: [null], collection: null },
])("current response keeps declared option types and required nullable fields %#", async options => {
  const { client, requests } = await fixture((_r, reply) => json(reply, { ...job, options }));
  await expect(client.database("app").imports.get()).rejects.toThrow(TypeError);
  expect(requests).toHaveLength(1);
});

test("import consumer types match current request/response DTOs", () => {
  const program = ts.createProgram(["test/fixtures/imports.ts"], { strict: true, exactOptionalPropertyTypes: true, allowJs: true,
    baseUrl: process.cwd(), paths: { "@terrascale/sdk": ["./src/index.js"] },
    target: ts.ScriptTarget.ESNext, module: ts.ModuleKind.NodeNext, skipLibCheck: true, noEmit: true });
  expect(ts.getPreEmitDiagnostics(program).map(d => ts.flattenDiagnosticMessageText(d.messageText, "\n"))).toEqual([]);
});
