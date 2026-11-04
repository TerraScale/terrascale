import { describe, expect, it, vi } from "vitest";
import { isCanaryConfig, parseCanaries, holdsRow } from "./canary";
import { isTerraBaseConfig, parseTerraBase } from "./terrabase";
import { isSdkProbeConfig, runSdkDocuments } from "./sdk";

const id = "01M4E7G1W82VKFPF5FJ9AVK1CV";
const canary = {
  origin: "https://api.terrascale.tech",
  region: "gru",
  project: id,
  database: "status_canary",
  collection: "canary",
  document: id,
  issuer: "https://auth.example.test/terrascale",
  key: `${id}_${"a".repeat(52)}`,
};
const sdk = {
  ...canary,
  issuer: "https://gru.auth.terrascale.tech/terrascale",
  key: `tsk_${"a".repeat(20)}_${"b".repeat(52)}`,
};

describe("probe entity IDs", () => {
  it("accepts canonical endpoint keys and rejects legacy secret lookup keys", () => {
    const error = vi.spyOn(console, "error").mockImplementation(() => {});
    try {
      expect(parseCanaries(JSON.stringify({ [id]: canary }))).toEqual({ [id]: canary });
      expect(parseCanaries(JSON.stringify({ "data-api-canary-gru": canary }))).toEqual({});
      expect(parseTerraBase(JSON.stringify({ [id]: canary }))).toEqual({ [id]: canary });
      expect(parseTerraBase(JSON.stringify({ "documents-api-gru": canary }))).toBeNull();
    } finally {
      error.mockRestore();
    }
  });

  it("does not write or attempt cleanup for an invalid SDK document ID", async () => {
    const collection = { put: vi.fn(), delete: vi.fn() };
    const result = await runSdkDocuments(
      collection as unknown as Parameters<typeof runSdkDocuments>[0],
      "invalid",
      "marker",
      true,
    );
    expect(result).toEqual({ ok: false, latencyMs: null, error: "Invalid probe config" });
    expect(collection.put).not.toHaveBeenCalled();
    expect(collection.delete).not.toHaveBeenCalled();
  });

  it("requires a deployment region on every canary and TerraBase config", () => {
    expect(isCanaryConfig({ ...canary, region: "sjc" })).toBe(true);
    expect(isCanaryConfig({ ...canary, region: "SJC" })).toBe(false);
    expect(isCanaryConfig({ ...canary, region: undefined })).toBe(false);
    expect(isTerraBaseConfig("health", { origin: canary.origin, region: "gru" })).toBe(true);
    expect(isTerraBaseConfig("health", { origin: canary.origin })).toBe(false);
  });

  it("requires canonical ULIDs for project and document identities", () => {
    expect(isCanaryConfig(canary)).toBe(true);
    expect(isSdkProbeConfig("documents", sdk)).toBe(true);
    for (const invalid of [
      id.toLowerCase(),
      `prj_${id}`,
      "b450c3c0-4427-41e4-b620-0ba7d93dfdb8",
      "8ZZZZZZZZZZZZZZZZZZZZZZZZZ",
    ]) {
      for (const field of ["project", "document"]) {
        expect(isCanaryConfig({ ...canary, [field]: invalid })).toBe(false);
        expect(isSdkProbeConfig("documents", { ...sdk, [field]: invalid })).toBe(false);
      }
      expect(holdsRow({ results: [{ rows: [[invalid]] }] }, invalid)).toBe(false);
    }
  });
});
