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
  bail: 1,
  // Filtered diagnostic runs still fail qualification because cases are skipped.
  testNamePattern: process.env.TERRASCALE_AUTH_TEST_NAME_PATTERN,
  reporters: ["verbose"],
  disableConsoleIntercept: true,
  testTimeout: 30_000,
  hookTimeout: 30_000,
});
try {
  const files = runner.state.getFiles();
  const counts = { passed: 0, failed: 0, skipped: 0 };
  const visit = task => {
    if (task.type === "test") {
      if (task.result?.state === "pass") counts.passed += 1;
      else if (task.result?.state === "fail") counts.failed += 1;
      else counts.skipped += 1;
    }
    for (const child of task.tasks ?? []) visit(child);
  };
  for (const file of files) visit(file);
  console.log(JSON.stringify({ qualification: "better-auth-1.7.2-public-authority", ...counts }));
  if (files.length !== 1 || counts.passed === 0 || counts.failed !== 0 || counts.skipped !== 0 || files.some(file => file.result?.state !== "pass") || runner.state.getUnhandledErrors().length > 0) {
    process.exitCode = 1;
  }
} finally {
  await runner.close();
}
