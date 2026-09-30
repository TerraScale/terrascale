import { assertLocalSqliteEnvironment } from "./environment.js";
/** Local development tests never relax CI/deployment/qualification guards. */
export const localSqliteUnitTestsAllowed = (() => {
  try {
    assertLocalSqliteEnvironment();
    return true;
  } catch {
    return false;
  }
})();
import { mkdtemp, rm } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { randomBytes, randomUUID } from "node:crypto";
import {
  createTerraScaleSqliteRuntime,
  type TerraScaleSqliteRuntimeOptions,
} from "./index.js";
import type {
  DatabaseCommit,
  DatabaseField,
  DatabasePoint,
} from "../database-types.js";
export async function localFixture(
  options: Omit<TerraScaleSqliteRuntimeOptions, "sqlite"> = {},
) {
  const directory = await mkdtemp(join(tmpdir(), "terrascale-local-native-"));
  const databasePath = join(directory, "development.sqlite");
  const runtime = createTerraScaleSqliteRuntime({
    ...options,
    sqlite: { enabled: true, databasePath },
  });
  const collection = randomUUID();
  const binding = {
    collection,
    schema_generation: 1n,
    schema_digest: "a".repeat(64),
  };
  await runtime.bindCollection(binding);
  const client = runtime.createClient();
  const commit = (
    document: string,
    fields: readonly DatabaseField[],
    point?: DatabasePoint,
  ): DatabaseCommit => ({
    transaction_id: {
      expires_at_unix_ms: BigInt(Date.now() + 60_000),
      nonce: randomBytes(16).toString("hex"),
    },
    authority: runtime.authority,
    writes: [
      {
        ...binding,
        document,
        condition:
          point === undefined
            ? { kind: "absent" }
            : { kind: "version", version: point.state_version! },
        observed: point?.context ?? [],
        value: { kind: "live", fields },
      },
    ],
  });
  return {
    runtime,
    client,
    collection,
    binding,
    directory,
    databasePath,
    commit,
    close: async () => {
      await runtime.close();
      await rm(directory, { recursive: true, force: true });
    },
  };
}
