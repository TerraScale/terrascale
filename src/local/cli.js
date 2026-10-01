#!/usr/bin/env node
import { resolve } from "node:path";
import { parseArgs } from "node:util";
import { createTerraScaleSqliteRuntime } from "./index.js";
const { values } = parseArgs({
  options: {
    database: { type: "string" },
    host: { type: "string" },
    port: { type: "string" },
    "require-auth": { type: "boolean", default: false },
    "allow-remote": { type: "boolean", default: false },
    reset: { type: "boolean", default: false },
    help: { type: "boolean", short: "h", default: false },
  },
});
if (values.help) {
  process.stdout.write(
    "Usage: terrascale-local [--database PATH] [--host HOST] [--port PORT] [--require-auth] [--allow-remote] [--reset]\nExplicit local SQLite development listener for native document requests.\n",
  );
} else {
  const runtime = createTerraScaleSqliteRuntime({
    sqlite: {
      enabled: true,
      databasePath: resolve(values.database ?? ".terrascale/local.sqlite"),
    },
    allowRemote: values["allow-remote"],
    ...(values["require-auth"]
      ? {
          authentication: "required",
          bearerToken: process.env.TERRASCALE_LOCAL_BEARER_TOKEN ?? "",
        }
      : {}),
  });
  try {
    if (values.reset) await runtime.reset();
    const address = await runtime.start({
      ...(values.host === undefined ? {} : { host: values.host }),
      port: values.port === undefined ? 0 : Number(values.port),
    });
    process.stdout.write(
      `Local SQLite development listener: ${address.origin}\nScope: ${runtime.scope.tenant}/${runtime.scope.database}/${runtime.scope.shard}\nCollection bindings must be explicitly installed by the local application.\n`,
    );
    for (const signal of /** @type {const} */ (["SIGINT", "SIGTERM"]))
      process.once(signal, () => {
        void runtime.close().then(() => process.exit(0));
      });
  } catch (error) {
    await runtime.close();
    throw error;
  }
}
