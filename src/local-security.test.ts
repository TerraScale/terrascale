import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

import {
  localSqlitePresenceIndicators,
  localSqliteStageIndicators,
} from "./local/environment.js";
import { createTerraScaleSqliteRuntime } from "./local/index.js";
import { localSqliteTestChildEnvironment } from "./local/test-environment.test.js";

const run = promisify(execFile);
const bearerToken = "local-security-token-0123456789-abcdefghijk";
const indicatorNames = [
  ...localSqlitePresenceIndicators,
  ...localSqliteStageIndicators,
] as const;

async function withEnvironment<T>(
  values: Readonly<Record<string, string>>,
  work: () => Promise<T> | T,
): Promise<T> {
  const names = new Set<string>([...indicatorNames, ...Object.keys(values)]);
  const previous = new Map<string, string | undefined>(
    [...names].map((name) => [name, process.env[name]]),
  );
  for (const name of names) {
    delete process.env[name];
  }
  process.env.NODE_ENV = "test";
  for (const [name, value] of Object.entries(values)) {
    process.env[name] = value;
  }
  try {
    return await work();
  } finally {
    for (const [name, value] of previous) {
      if (value === undefined) {
        delete process.env[name];
      } else {
        process.env[name] = value;
      }
    }
  }
}

async function withDatabasePath<T>(work: (databasePath: string) => Promise<T>): Promise<T> {
  const directory = await mkdtemp(join(tmpdir(), "terrascale-local-security-"));
  try {
    return await work(join(directory, "security.sqlite"));
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}

test("SQLite activation rejects production, CI, staging, deployment, and qualification indicators", async () => {
  await withDatabasePath(async (databasePath) => {
    for (const [name, value] of [
      ["NODE_ENV", "production"],
      ["CI", "true"],
      ["DEPLOYMENT_ENVIRONMENT", "staging"],
      ["VERCEL", "1"],
      ["TERRASCALE_QUALIFICATION", "true"],
    ] as const) {
      await withEnvironment({ [name]: value }, () => {
        assert.throws(
          () => createTerraScaleSqliteRuntime({
            sqlite: { enabled: true, databasePath },
          }),
          new RegExp(name, "u"),
        );
      });
    }

    await withEnvironment({ NODE_ENV: "development", CI: "false" }, async () => {
      const runtime = createTerraScaleSqliteRuntime({
        sqlite: { enabled: true, databasePath },
      });
      await runtime.close();
    });
  });
});

test("SQLite activation requires an absolute path and valid local-only options", async () => {
  await withEnvironment({}, async () => {
    assert.throws(
      () => createTerraScaleSqliteRuntime({ sqlite: { enabled: true } } as never),
      /databasePath must be an absolute path/u,
    );
    assert.throws(
      () => createTerraScaleSqliteRuntime({
        sqlite: { enabled: true, databasePath: "relative.sqlite" },
      }),
      /databasePath must be an absolute path/u,
    );
    assert.throws(
      () => createTerraScaleSqliteRuntime({
        sqlite: { enabled: true, databasePath: ":memory:" },
      }),
      /databasePath must be an absolute path/u,
    );
    await withDatabasePath(async (databasePath) => {
      assert.throws(
        () => createTerraScaleSqliteRuntime({
          sqlite: { enabled: true, databasePath },
          authentication: "invalid" as never,
        }),
        /authentication must be either/u,
      );
      assert.throws(
        () => createTerraScaleSqliteRuntime({
          sqlite: { enabled: true, databasePath },
          allowRemote: true,
        }),
        /allowRemote requires authentication/u,
      );
      assert.throws(
        () => createTerraScaleSqliteRuntime({
          sqlite: { enabled: true, databasePath },
          authentication: "required",
        }),
        /bearerToken is required/u,
      );
      assert.throws(
        () => createTerraScaleSqliteRuntime({
          sqlite: { enabled: true, databasePath },
          authentication: "required",
          bearerToken: "too-short",
        }),
        /between 32 and 512/u,
      );
    });
  });
});

test("remote binding authorizes only the configured bearer token", async () => {
  await withEnvironment({}, async () => {
    await withDatabasePath(async (databasePath) => {
      const runtime = createTerraScaleSqliteRuntime({
        sqlite: { enabled: true, databasePath },
        allowRemote: true,
        authentication: "required",
        bearerToken,
      });
      try {
        const address = await runtime.start({ host: "0.0.0.0", port: 0 });
        const origin = `http://127.0.0.1:${address.port}`;
        const organizations = `${origin}/api/v1/organizations`;
        assert.equal((await fetch(organizations)).status, 401);
        assert.equal((await fetch(organizations, {
          headers: { authorization: "Basic anything" },
        })).status, 401);
        assert.equal((await fetch(organizations, {
          headers: { authorization: "Bearer another-token-that-is-long-enough-123456" },
        })).status, 401);
        assert.equal((await fetch(organizations, {
          headers: { cookie: "better-auth.session_token=anything" },
        })).status, 401);
        assert.equal((await fetch(organizations, {
          method: "POST",
          headers: {
            "content-type": "application/json",
            "x-tb-principal": "caller-selected",
            "x-tb-tenant": "caller-selected",
          },
          body: JSON.stringify({ name: "Caller-selected identity" }),
        })).status, 401);
        assert.equal((await fetch(organizations, {
          headers: { authorization: `Bearer ${bearerToken}` },
        })).status, 200);

        const seed = await runtime.seed();
        const localClientResult = await runtime.createClient({
          projectId: seed.projectId,
        }).databases.list();
        assert.equal(localClientResult.ok, true);
      } finally {
        await runtime.close();
      }
    });
  });
});

test("the CLI refuses remote binding without explicit strong authentication", async () => {
  const cli = fileURLToPath(new URL("./local/cli.js", import.meta.url));
  await withDatabasePath(async (databasePath) => {
    const common = [cli, "--allow-remote", "--database", databasePath];
    await assert.rejects(
      run(process.execPath, common, { env: localSqliteTestChildEnvironment() }),
      (error: unknown) => error instanceof Error
        && "stderr" in error
        && String(error.stderr).includes("--allow-remote requires --require-auth"),
    );
    await assert.rejects(
      run(process.execPath, [...common, "--require-auth"], {
        env: localSqliteTestChildEnvironment(),
      }),
      (error: unknown) => error instanceof Error
        && "stderr" in error
        && String(error.stderr).includes("TERRASCALE_LOCAL_BEARER_TOKEN"),
    );
    await assert.rejects(
      run(process.execPath, [...common, "--require-auth"], {
        env: {
          ...localSqliteTestChildEnvironment(),
          TERRASCALE_LOCAL_BEARER_TOKEN: "too-short",
        },
      }),
      (error: unknown) => error instanceof Error
        && "stderr" in error
        && String(error.stderr).includes("between 32 and 512"),
    );
  });
});
