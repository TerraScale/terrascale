import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { describe, expect, it } from "vitest";
import { createTerraBaseClient } from "../index.js";
import { fileStorageRoutes } from "./file-storage-routes.js";
const project = "01J00000000000000000000000";
const bucket = "01J00000000000000000000001";
const file = "01J00000000000000000000002";
const record = { id: file, name: "hello.txt", contentType: "text/plain", size: 5, sha256: "", metadata: { owner: "alice" }, status: "ready", uploadExpiresAt: "2026-10-24T12:00:00Z" };

it("pins file operations to the producer's versioned endpoints and scopes", async () => {
  const root = new URL("../../contracts/file-storage/", import.meta.url);
  const pin = JSON.parse(await readFile(new URL("pin.json", root), "utf8"));
  const sources = new Map();
  for (const [name, input] of Object.entries(pin.inputs)) {
    const source = await readFile(new URL(name, root), "utf8");
    expect(createHash("sha256").update(source).digest("hex")).toBe(input.sha256);
    sources.set(name, source);
  }
  const paths = sources.get("FileStorageRoute.cs");
  expect(paths).toContain('ProjectApiRoute.Project + "/buckets"');
  expect(paths).toContain('Bucket = Buckets + "/{bucket}"');
  expect(paths).toContain('Files = Bucket + "/files"');
  expect(paths).toContain('File = Files + "/{file}"');
  /** @type {Record<string, string>} */
  const routePaths = { Buckets: "/v1/projects/{project}/buckets", Bucket: "/v1/projects/{project}/buckets/{bucket}",
    Files: "/v1/projects/{project}/buckets/{bucket}/files", File: "/v1/projects/{project}/buckets/{bucket}/files/{file}" };
  expect(fileStorageRoutes).toHaveLength(9);
  for (const route of fileStorageRoutes) {
    const name = route.operation.charAt(0).toUpperCase() + route.operation.slice(1) + "Endpoint.cs";
    const source = sources.get(name);
    expect(source).toContain(`Name("${route.operation}")`);
    expect(source).toContain(`RequireAuthorization("${route.permission}")`);
    expect(source).toContain(`${route.method.charAt(0) + route.method.slice(1).toLowerCase()}(FileStorageRoute.`);
    const declared = /(?:Get|Post|Put|Delete)\(FileStorageRoute\.(\w+)(?: \+ "([^"]+)")?\);/u.exec(source);
    expect(declared).not.toBeNull();
    expect(route.pathTemplate).toBe(`${routePaths[declared?.[1] ?? ""]}${declared?.[2] ?? ""}`);
    for (const status of route.successStatuses)
      expect(source).toContain(status === 204 ? "Produces(StatusCodes.Status204NoContent)" : `>(${status})`);
    expect(route.requestMediaTypes).toEqual(source.includes("new RequestBodyMetadata(") ? ["application/json"] : []);
    expect(route.pathTemplate).toMatch(/^\/v1\/projects\/\{project\}\/buckets/u);
  }
});

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

  it("rejects signed responses that request forwarding authorization", async () => {
    const client = createTerraBaseClient({ project, baseUrl: "https://api.example", token: "aaa.bbb.ccc", fetch: async () => Response.json({ id: file, url: "https://objects.example/download", expiresAt: record.uploadExpiresAt, headers: { Authorization: "never-forward" } }) });
    await expect(client.storage.bucket(bucket).downloadUrl(file)).rejects.toThrow("malformed");
  });

  it("downloads bytes with no project credentials and rejects insecure capabilities", async () => {
    const client = createTerraBaseClient({ project, baseUrl: "https://api.example", token: "aaa.bbb.ccc", fetch: async (input, init = {}) => {
      if (String(input).startsWith("https://api.example/"))
        return Response.json({ id: file, url: "https://objects.example/download", expiresAt: record.uploadExpiresAt, headers: {} });
      expect(new Headers(init.headers).has("Authorization")).toBe(false);
      expect(init.credentials).toBe("omit"); expect(init.redirect).toBe("error");
      return new Response("hello");
    } });
    expect(await (await client.storage.bucket(bucket).download(file)).text()).toBe("hello");
    const insecure = createTerraBaseClient({ project, baseUrl: "https://api.example", token: "aaa.bbb.ccc", fetch: async () =>
      Response.json({ id: file, url: "http://objects.example/download", expiresAt: record.uploadExpiresAt, headers: {} }) });
    await expect(insecure.storage.bucket(bucket).download(file)).rejects.toThrow("HTTPS");
  });
});
