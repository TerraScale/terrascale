import { expect, test } from "vitest";
import { entity, fixture, json, prefix, project } from "./helpers/v1-http.js";
const input = { format: /** @type {const} */ ("sqlite"), sourceBytes: 4, sourceSha256: "a".repeat(64) };
const at = "2026-10-22T10:00:00Z";
const job = { id: entity, project, database: "app", region: "gru", state: "awaiting_source", cancelRequested: false,
  options: { missingPrimaryKey: "reject", exclude: [] }, source: { format: "sqlite", bytes: 4, sha256: input.sourceSha256, receivedBytes: 0, staged: false, retainedUntil: null },
  preview: null, confirmedPreviewId: null, progress: null, validation: null, error: null, createdAt: at, updatedAt: at, startedAt: null, finishedAt: null };
test("all seven import routes preserve binary chunks, offsets, preview confirmation and 202 jobs", async () => {
  const { client, requests } = await fixture((_r, response) => json(response, job, [201, 200, 200, 202, 202, 202, 202][requests.length - 1]));
  const imports = client.database("app").imports;
  expect(await imports.create(input)).toEqual(job);
  expect(await imports.get()).toEqual(job);
  const bytes = new Uint8Array([0, 127, 128, 255]);
  expect(await imports.uploadSource(0n, bytes)).toEqual(job);
  expect(await imports.update({ options: { missingPrimaryKey: "generate", exclude: ["public.secret"] } })).toEqual(job);
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
test.each([400, 403, 404, 409, 413, 415, 421, 503])("import failure %i is never replayed", async status => {
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
