import { describe, expect, it } from "vitest";
import { createRouter } from "./router";

describe("createRouter", () => {
  const route = createRouter({ "/": "home", "/incidents/:id": "incident", "/badge/:id.svg": "badge" });

  it("matches static paths exactly", () => {
    expect(route("/")).toEqual({ value: "home", params: {} });
    expect(route("/history")).toBeNull();
  });

  it("captures and decodes parameters", () => {
    expect(route("/incidents/a%20b")).toEqual({ value: "incident", params: { id: "a b" } });
    expect(route("/badge/dashboard.svg")).toEqual({ value: "badge", params: { id: "dashboard" } });
  });

  it("keeps parameters inside one segment", () => {
    expect(route("/incidents/a/b")).toBeNull();
    expect(route("/incidents/")).toBeNull();
  });

  it("rejects malformed escapes", () => {
    expect(route("/incidents/%E0%A4%A")).toBeNull();
  });
});
