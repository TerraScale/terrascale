import { describe, expect, it } from "vitest";
import { secure } from "./security";

describe("response security", () => {
  it.each([200, 302, 404, 405, 503])("protects status %s including non-HTML responses", (status) => {
    const response = secure(new Response("body", { status }), false);
    expect(response.status).toBe(status);
    expect(response.headers.get("strict-transport-security")).toBe("max-age=31536000");
    expect(response.headers.get("permissions-policy")).toBe(
      "camera=(), microphone=(), geolocation=(), payment=(), usb=()",
    );
    expect(response.headers.get("content-security-policy")).toBe(
      "default-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
    );
    expect(response.headers.get("x-frame-options")).toBe("DENY");
    expect(response.headers.get("x-content-type-options")).toBe("nosniff");
    expect(response.headers.get("referrer-policy")).toBe("strict-origin-when-cross-origin");
    expect(response.headers.get("cache-control")).toBe("no-store");
  });

  it("preserves route policies and public API CORS without adding credentials", () => {
    const policy = "default-src 'none'; script-src 'sha256-example'; frame-ancestors *";
    const response = secure(
      new Response("widget", {
        headers: { "content-security-policy": policy, "access-control-allow-origin": "*", "referrer-policy": "no-referrer" },
      }),
      true,
    );
    expect(response.headers.get("content-security-policy")).toBe(policy);
    expect(response.headers.get("referrer-policy")).toBe("no-referrer");
    expect(response.headers.get("x-frame-options")).toBeNull();
    expect(response.headers.get("access-control-allow-origin")).toBe("*");
    expect(response.headers.get("access-control-allow-credentials")).toBeNull();
    expect(response.headers.get("cache-control")).toBe("public, max-age=10");
  });
});
