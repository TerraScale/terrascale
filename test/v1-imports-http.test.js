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
  expect(await imports.start({ previewId: "preview-sha256" })).toEqual(job);
  expect(await imports.cancel()).toEqual(job);
  expect(await imports.resume()).toEqual(job);
  const base = `${prefix}/databases/app/import`;
  expect(requests.map(r => [r.method, r.url.pathname])).toEqual([
    ["POST", base], ["GET", base], ["PUT", `${base}/source`], ["PATCH", base], ["POST", `${base}/start`], ["POST", `${base}/cancel`], ["POST", `${base}/resume`],
  ]);
  expect(requests[2]?.headers["content-type"]).toBe("application/octet-stream");
  expect(requests[2]?.headers["content-length"]).toBe("4");
  expect(requests[2]?.url.search).toBe("?offset=0");
  expect(requests[4]?.body).toBe('{"previewId":"preview-sha256"}');
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
