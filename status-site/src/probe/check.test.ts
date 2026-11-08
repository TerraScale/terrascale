import { describe, expect, it } from "vitest";
import { check, MAX_BODY_BYTES } from "./check";

const target = {
  key: "dash",
  url: "https://dash.test/health/ready",
  headers: '{"Cache-Control":"no-cache"}',
  body: false,
};

describe("check", () => {
  it("observes the status and forwards the target headers", async () => {
    let sent: Headers | undefined;
    const result = await check(target, async (_url, init) => {
      sent = new Headers(init?.headers);
      return new Response("ok", { status: 503 });
    });
    expect(result).toMatchObject({ key: "dash", status: 503, error: null });
    expect(result.body).toBeUndefined();
    expect(sent?.get("Cache-Control")).toBe("no-cache");
    expect(sent?.get("user-agent")).toMatch(/^TerraScale-Status/);
  });

  it("reads the start of the body on request", async () => {
    const result = await check({ ...target, body: true }, async () => new Response("x".repeat(MAX_BODY_BYTES * 3)));
    expect(result.body?.length).toBe(MAX_BODY_BYTES);
  });

  it("reports a network error without a status", async () => {
    expect(await check(target, async () => Promise.reject(new TypeError("fetch failed")))).toMatchObject({
      status: null,
      error: "Unreachable",
    });
  });
});
