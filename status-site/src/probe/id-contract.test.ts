import { describe, expect, it, vi } from "vitest";
import { isCanaryConfig, parseCanaries, holdsRow } from "./canary";
import { isTerraBaseConfig, parseTerraBase } from "./terrabase";

const id = "01M4E7G1W82VKFPF5FJ9AVK1CV";
const canary = {
  origin: "https://api.terrascale.tech",
  region: "gru",
  project: id,
  database: "status_canary",
  issuer: "https://auth.example.test/terrascale",
  key: `${id}_${"a".repeat(52)}`,
};

describe("probe entity IDs", () => {
  it("accepts canonical endpoint keys and rejects legacy secret lookup keys", () => {
    const error = vi.spyOn(console, "error").mockImplementation(() => {});
    try {
      expect(parseCanaries(JSON.stringify({ [id]: canary }))).toEqual({ [id]: canary });
      expect(parseCanaries(JSON.stringify({ "data-api-canary-gru": canary }))).toEqual({});
      expect(parseTerraBase(JSON.stringify({ [id]: canary }))).toEqual({ [id]: canary });
      expect(parseTerraBase(JSON.stringify({ "sql-api-gru": canary }))).toBeNull();
    } finally {
      error.mockRestore();
    }
  });

  it("requires a deployment region on every canary and TerraBase config", () => {
    expect(isCanaryConfig({ ...canary, region: "sjc" })).toBe(true);
    expect(isCanaryConfig({ ...canary, region: "SJC" })).toBe(false);
    expect(isCanaryConfig({ ...canary, region: undefined })).toBe(false);
    expect(isTerraBaseConfig("health", { origin: canary.origin, region: "gru" })).toBe(true);
    expect(isTerraBaseConfig("health", { origin: canary.origin })).toBe(false);
  });

  it("requires canonical ULIDs for project identities", () => {
    expect(isCanaryConfig(canary)).toBe(true);
    for (const invalid of [
      id.toLowerCase(),
      `prj_${id}`,
      "b450c3c0-4427-41e4-b620-0ba7d93dfdb8",
      "8ZZZZZZZZZZZZZZZZZZZZZZZZZ",
    ]) {
      expect(isCanaryConfig({ ...canary, project: invalid })).toBe(false);
      expect(holdsRow({ results: [{ rows: [[invalid]] }] }, invalid)).toBe(false);
    }
  });
});
