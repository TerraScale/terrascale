import { fileURLToPath } from "node:url";
import { startVitest } from "vitest/node";

for (const name of ["TERRASCALE_AUTH_TEST_BASE_URL", "TERRASCALE_AUTH_TEST_DATABASE", "TERRASCALE_AUTH_TEST_ACCESS_TOKEN"]) {
  if (!process.env[name]) throw new Error(`${name} must be supplied by the disposable producer fixture.`);
}

const root = fileURLToPath(new URL("../", import.meta.url));
const runner = await startVitest("test", ["src/integration/auth-authority.test.ts"], {
  root,
  run: true,
  pool: "threads",
  maxWorkers: 1,
  fileParallelism: false,
  testTimeout: 30_000,
  hookTimeout: 30_000,
});
try {
  const files = runner.state.getFiles();
  if (files.length !== 1 || files.some(file => file.result?.state !== "pass") || runner.state.getUnhandledErrors().length > 0) {
    process.exitCode = 1;
  }
} finally {
  await runner.close();
}
