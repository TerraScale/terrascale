import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import {
  BETTER_AUTH_CLEANUP_MODULE,
  BETTER_AUTH_CLEANUP_PATCH_MARKER,
  BETTER_AUTH_CLEANUP_SOURCE_HASH,
  createBetterAuthCleanupPatchPlugin,
  createBetterAuthCleanupPatchState,
} from "./better-auth-cleanup-patch.js";

describe("Better Auth cleanup patch", () => {
  it("patches only the pinned upstream module and records an auditable state", () => {
    const source = readFileSync(BETTER_AUTH_CLEANUP_MODULE, "utf8");
    expect(createHash("sha256").update(source).digest("hex")).toBe(BETTER_AUTH_CLEANUP_SOURCE_HASH);
    const state = createBetterAuthCleanupPatchState();
    const result = createBetterAuthCleanupPatchPlugin(state).transform!(source, BETTER_AUTH_CLEANUP_MODULE);
    expect(result).not.toBeNull();
    const code = typeof result === "string" ? result : result!.code;
    expect(code).toContain(BETTER_AUTH_CLEANUP_PATCH_MARKER);
    expect(state.applied).toBe(true);
    expect(state.sourceHash).toBe(createHash("sha256").update(code).digest("hex"));
    expect(code).toContain("const remaining = [];");
  });

  it("refuses a different module id or changed upstream source", () => {
    const plugin = createBetterAuthCleanupPatchPlugin();
    expect(plugin.transform!("export {}", "/tmp/create-test-suite.mjs")).toBeNull();
    expect(() => plugin.transform!("export {}", BETTER_AUTH_CLEANUP_MODULE)).toThrow(/unexpected source hash/u);
  });

  it("executes cleanup with success removal, failure retention, and unknown-model retention", async () => {
    const source = readFileSync(BETTER_AUTH_CLEANUP_MODULE, "utf8");
    const state = createBetterAuthCleanupPatchState();
    const transformed = createBetterAuthCleanupPatchPlugin(state).transform!(source, BETTER_AUTH_CLEANUP_MODULE);
    const code = typeof transformed === "string" ? transformed : transformed!.code;
    const start = code.indexOf("const cleanupCreatedRows = async () => {");
    const marker = `/* ${BETTER_AUTH_CLEANUP_PATCH_MARKER} */`;
    const end = code.indexOf(marker, start);
    expect(start).toBeGreaterThanOrEqual(0);
    expect(end).toBeGreaterThan(start);
    const runtime: { adapter?: unknown; helpers?: unknown; createdRows?: unknown } = {};
    const cleanup = new Function("runtime", "getAuthTables", "initGetDefaultModelName", `let adapter; let helpers; let createdRows; ${code.slice(start, end)}; return async () => { adapter = runtime.adapter; helpers = runtime.helpers; createdRows = runtime.createdRows; await cleanupCreatedRows(); };`)(
      runtime,
      (options: { schema: Record<string, unknown> }) => options.schema,
      ({ schema }: { schema: Record<string, unknown> }) => (model: string) => model in schema ? model : (() => { throw new Error("unknown model"); })(),
    ) as () => Promise<void>;
    const attempts: number[] = [];
    let failOnce = true;
    const createdRows: Record<string, Array<{ id: number }>> = {
      user: [{ id: 1 }, { id: 2 }],
      failed: [{ id: 3 }],
      unknown: [{ id: 4 }],
    };
    const adapter = {
      options: { adapterConfig: { usePlural: false } },
      delete: async ({ model, where }: { model: string; where: Array<{ value: number }> }) => {
        const id = where[0]!.value;
        attempts.push(id);
        if (model === "failed" && failOnce) {
          failOnce = false;
          throw new Error("transient cleanup failure");
        }
      },
    };
    const helpers = {
      adapter: async () => adapter,
      getBetterAuthOptions: () => ({}),
    };
    const schema = { user: {}, failed: {} };
    const schemaHelpers = { ...helpers, getBetterAuthOptions: () => ({ schema }) };
    runtime.adapter = adapter;
    runtime.helpers = schemaHelpers;
    runtime.createdRows = createdRows;
    await cleanup();
    expect(createdRows).toEqual({ failed: [{ id: 3 }], unknown: [{ id: 4 }] });
    await cleanup();
    expect(createdRows).toEqual({ unknown: [{ id: 4 }] });
    expect(attempts).toEqual([1, 2, 3, 3]);

    const suffixStart = source.indexOf("let currentAppliedOptions = null;");
    const transformedSuffixStart = code.indexOf("let currentAppliedOptions = null;");
    expect(transformedSuffixStart).toBeGreaterThan(-1);
    expect(code.slice(transformedSuffixStart)).toBe(source.slice(suffixStart));
  });
});
