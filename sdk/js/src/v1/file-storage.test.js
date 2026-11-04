import { createHash } from "node:crypto";
import { describe, expect, it } from "vitest";
import { createTerraBaseClient } from "../index.js";
const project = "01J00000000000000000000000";
const bucket = "01J00000000000000000000001";
const file = "01J00000000000000000000002";
const record = { id: file, name: "hello.txt", contentType: "text/plain", size: 5, sha256: "", metadata: { owner: "alice" }, status: "ready", uploadExpiresAt: "2026-10-24T12:00:00Z" };

describe("file SDK", () => {
  it("uploads a checksum-bound snapshot and completes it without leaking the project token", async () => {
    /** @type {{url:string, init:RequestInit}[]} */ const requests = [];
    const client = createTerraBaseClient({ project, baseUrl: "https://api.example", token: "aaa.bbb.ccc", fetch: async (input, init = {}) => {
      const url = String(input); requests.push({ url, init });
      if (url.endsWith("/files/upload")) {
        const body = await new Request(input, init).json();
        expect(body.size).toBe(5);
        expect(body.sha256).toBe(createHash("sha256").update("hello").digest("base64"));
        return Response.json({ id: file, url: "https://objects.example/upload", expiresAt: record.uploadExpiresAt, headers: { "Content-Length": "5", "x-amz-checksum-sha256": body.sha256 } }, { status: 201 });
      }
      if (url === "https://objects.example/upload") {
        expect(new Headers(init.headers).has("Authorization")).toBe(false);
        expect(init.credentials).toBe("omit"); expect(init.redirect).toBe("error");
        expect(new Uint8Array(await new Request(input, init).arrayBuffer())).toEqual(new TextEncoder().encode("hello"));
        return new Response(null, { status: 200 });
      }
      expect(url).toBe(`https://api.example/v1/projects/${project}/buckets/${bucket}/files/${file}/complete`);
      return Response.json(record);
    } });
    expect((await client.storage.bucket(bucket).upload("hello.txt", new TextEncoder().encode("hello"), { metadata: { owner: "alice" }, contentType: "text/plain" })).id).toBe(file);
    expect(requests).toHaveLength(3);
  });

  it("propagates forbidden downloads without contacting object storage or retrying", async () => {
    let calls = 0;
    const client = createTerraBaseClient({ project, baseUrl: "https://api.example", token: "aaa.bbb.ccc", fetch: async () => {
      calls++; return Response.json({ error: { code: "access_denied", message: "denied" } }, { status: 403 });
    } });
    await expect(client.storage.bucket(bucket).download(file)).rejects.toMatchObject({ status: 403 });
    expect(calls).toBe(1);
  });
});
