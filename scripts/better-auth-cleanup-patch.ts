import { createHash } from "node:crypto";
import { createRequire } from "node:module";
import { dirname, join, normalize } from "node:path";
import type { Plugin } from "vite";

const require = createRequire(import.meta.url);
export const BETTER_AUTH_CLEANUP_SOURCE_HASH = "2993733da66175a5e8a13180a15199ceb398e99439cf92619c24e08a012c9f33";
export const BETTER_AUTH_CLEANUP_PATCH_MARKER = "terrascale-better-auth-cleanup-patch-v1";
export const BETTER_AUTH_CLEANUP_MODULE = normalize(join(dirname(require.resolve("@better-auth/test-utils/adapter")), "adapter/create-test-suite.mjs"));

const ORIGINAL_CLEANUP = `\t\t\tconst cleanupCreatedRows = async () => {
\t\t\t\tadapter = await helpers.adapter();
\t\t\t\tfor (const model of Object.keys(createdRows)) for (const row of createdRows[model]) {
\t\t\t\t\tconst schema = getAuthTables(helpers.getBetterAuthOptions());
\t\t\t\t\tconst getDefaultModelName = initGetDefaultModelName({
\t\t\t\t\t\tschema,
\t\t\t\t\t\tusePlural: adapter.options?.adapterConfig.usePlural
\t\t\t\t\t});
\t\t\t\t\tlet defaultModelName;
\t\t\t\t\ttry {
\t\t\t\t\t\tdefaultModelName = getDefaultModelName(model);
\t\t\t\t\t} catch {
\t\t\t\t\t\tcontinue;
\t\t\t\t\t}
\t\t\t\t\tif (!schema[defaultModelName]) continue;
\t\t\t\t\ttry {
\t\t\t\t\t\tawait adapter.delete({
\t\t\t\t\t\t\tmodel,
\t\t\t\t\t\t\twhere: [{
\t\t\t\t\t\t\t\tfield: "id",
\t\t\t\t\t\t\t\tvalue: row.id
\t\t\t\t\t\t\t}]
\t\t\t\t\t\t});
\t\t\t\t\t} catch {}
\t\t\t\t\tif (createdRows[model].length === 1) delete createdRows[model];
\t\t\t\t}
\t\t\t};`;

const PATCHED_CLEANUP = `\t\t\tconst cleanupCreatedRows = async () => {
\t\t\t\tadapter = await helpers.adapter();
\t\t\t\tfor (const model of Object.keys(createdRows)) {
\t\t\t\t\tconst schema = getAuthTables(helpers.getBetterAuthOptions());
\t\t\t\t\tconst getDefaultModelName = initGetDefaultModelName({
\t\t\t\t\t\tschema,
\t\t\t\t\t\tusePlural: adapter.options?.adapterConfig.usePlural
\t\t\t\t\t});
\t\t\t\t\tlet defaultModelName;
\t\t\t\t\ttry {
\t\t\t\t\t\tdefaultModelName = getDefaultModelName(model);
\t\t\t\t\t} catch {
\t\t\t\t\t\tcontinue;
\t\t\t\t\t}
\t\t\t\t\tif (!schema[defaultModelName]) continue;
\t\t\t\t\tconst remaining = [];
\t\t\t\t\tfor (const row of createdRows[model]) {
\t\t\t\t\t\ttry {
\t\t\t\t\t\t\tawait adapter.delete({
\t\t\t\t\t\t\t\tmodel,
\t\t\t\t\t\t\t\twhere: [{
\t\t\t\t\t\t\t\t\tfield: "id",
\t\t\t\t\t\t\t\t\tvalue: row.id
\t\t\t\t\t\t\t\t}]
\t\t\t\t\t\t\t});
\t\t\t\t\t\t} catch {
\t\t\t\t\t\t\tremaining.push(row);
\t\t\t\t\t\t}
\t\t\t\t\t}
\t\t\t\t\tif (remaining.length === 0) delete createdRows[model];
\t\t\t\t\telse createdRows[model] = remaining;
\t\t\t\t}
\t\t\t};\n\t\t\t/* ${BETTER_AUTH_CLEANUP_PATCH_MARKER} */`;

export type BetterAuthCleanupPatchState = {
  applied: boolean;
  sourceHash?: string;
  marker: string;
  modulePath: string;
};

export function createBetterAuthCleanupPatchState(): BetterAuthCleanupPatchState {
  return { applied: false, marker: BETTER_AUTH_CLEANUP_PATCH_MARKER, modulePath: BETTER_AUTH_CLEANUP_MODULE };
}

export function createBetterAuthCleanupPatchPlugin(state = createBetterAuthCleanupPatchState()): Plugin {
  return {
    name: "terrascale-better-auth-cleanup-patch",
    enforce: "pre",
    transform(source, id) {
      const modulePath = normalize(id.split("?", 1)[0]!);
      if (modulePath !== BETTER_AUTH_CLEANUP_MODULE) return null;
      const sourceHash = createHash("sha256").update(source).digest("hex");
      if (sourceHash !== BETTER_AUTH_CLEANUP_SOURCE_HASH) {
        throw new Error(`Better Auth cleanup patch refused unexpected source hash: ${sourceHash}`);
      }
      if (source.split(ORIGINAL_CLEANUP).length !== 2) {
        throw new Error("Better Auth cleanup patch refused a missing or duplicate original block.");
      }
      const patched = source.replace(ORIGINAL_CLEANUP, PATCHED_CLEANUP);
      state.applied = true;
      state.sourceHash = createHash("sha256").update(patched).digest("hex");
      console.info(`[${BETTER_AUTH_CLEANUP_PATCH_MARKER}] patched ${modulePath} sourceHash=${state.sourceHash}`);
      return { code: patched, map: null };
    },
  };
}
