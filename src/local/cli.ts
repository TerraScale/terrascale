#!/usr/bin/env node

import { resolve } from "node:path";
import { parseArgs } from "node:util";

import { createTerraScaleSqliteRuntime } from "./index.js";

const { values } = parseArgs({
  allowPositionals: false,
  strict: true,
  options: {
    "allow-remote": { type: "boolean", default: false },
    database: { type: "string" },
    help: { type: "boolean", short: "h", default: false },
    host: { type: "string" },
    port: { type: "string" },
    "require-auth": { type: "boolean", default: false },
    reset: { type: "boolean", default: false },
    seed: { type: "boolean", default: false },
  },
});

if (values.help === true) {
  process.stdout.write(`Usage: terrascale-local [options]

Run the opt-in SQLite-backed local TerraScale public API emulator.

Options:
  --database <path>  Absolute SQLite path, or :memory:
                     Default: .terrascale/local.sqlite under the current directory
  --host <host>      Listen host. Default: 127.0.0.1
  --port <port>      Listen port. Default: 0 (choose an available port)
  --seed             Create one local organization, project, database, repository,
                     and document before accepting requests
  --reset            Clear the selected database before optional seeding
  --require-auth     Require a normal bearer token or session cookie on protected routes
  --allow-remote     Allow a non-loopback listen host
  -h, --help         Show this help

Stop the emulator with Ctrl-C. No production SDK defaults are changed.
`);
  process.exit(0);
}

const databasePath = localDatabasePath(values.database);
const port = localPort(values.port);
const runtime = createTerraScaleSqliteRuntime({
  sqlite: { enabled: true, databasePath },
  ...(values["require-auth"] === true ? { authentication: "required" as const } : {}),
  ...(values["allow-remote"] === true ? { allowRemote: true } : {}),
});

let closing = false;
const close = async (): Promise<void> => {
  if (closing) {
    return;
  }
  closing = true;
  await runtime.close();
};

process.once("SIGINT", () => {
  void close().finally(() => process.exit(0));
});
process.once("SIGTERM", () => {
  void close().finally(() => process.exit(0));
});

try {
  if (values.reset === true) {
    await runtime.reset();
  }
  const seed = values.seed === true ? await runtime.seed() : undefined;
  const address = await runtime.start({
    ...(values.host === undefined ? {} : { host: values.host }),
    port,
  });
  process.stdout.write([
    "TerraScale SQLite development runtime is running.",
    `Database: ${databasePath}`,
    `API URL: ${address.origin}`,
    `export TERRASCALE_API_BASE_URL=${address.apiBaseUrl}`,
    ...(seed === undefined ? [] : [`Seed project: ${seed.projectId}`, `Seed repository: ${seed.repositoryId}`]),
    "Press Ctrl-C to stop.",
    "",
  ].join("\n"));
} catch (error) {
  await close();
  const message = error instanceof Error ? error.message : String(error);
  process.stderr.write(`terrascale-local: ${message}\n`);
  process.exitCode = 1;
}

function localDatabasePath(value: string | undefined): string {
  if (value === ":memory:") {
    return value;
  }
  return resolve(value ?? ".terrascale/local.sqlite");
}

function localPort(value: string | undefined): number {
  if (value === undefined) {
    return 0;
  }
  if (!/^\d{1,5}$/u.test(value)) {
    throw new TypeError("--port must be an integer between 0 and 65535.");
  }
  const parsed = Number(value);
  if (parsed > 65_535) {
    throw new TypeError("--port must be an integer between 0 and 65535.");
  }
  return parsed;
}
