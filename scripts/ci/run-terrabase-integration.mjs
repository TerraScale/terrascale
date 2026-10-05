// Ouroboros: SDK tests consume an isolated real release process and its runtime OpenAPI.
import assert from "node:assert/strict";
import { spawn, execFileSync } from "node:child_process";
import { createHash, randomBytes } from "node:crypto";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { once } from "node:events";
import { openapiDigest } from "./contract-digest.mjs";
import { provisionTerraBase } from "./provision-terrabase.mjs";
import { captureProducerOutput, stopChild } from "./terrabase-lifecycle.mjs";
import { producerBootstrapEndpoints, provisioningRoute } from "../../test/support/terrabase-v1-setup.js";
import { setTimeout as delay } from "node:timers/promises";

const pin = JSON.parse(await readFile(new URL("./terrabase-release.json", import.meta.url), "utf8"));
const binary = process.env.TERRABASE_BINARY || await provisionTerraBase();
assert.ok(binary && path.isAbsolute(binary), "TERRABASE_BINARY must be an absolute path.");
assert.equal(createHash("sha256").update(await readFile(binary)).digest("hex"), pin.sha256, "TERRABASE_BINARY differs from the pinned release.");
const controller = new AbortController();
const { signal } = controller;
const onInterrupt = () => { if (!signal.aborted) process.exitCode = 130; controller.abort(); };
const onTerminate = () => { if (!signal.aborted) process.exitCode = 143; controller.abort(); };
process.on("SIGINT", onInterrupt);
process.on("SIGTERM", onTerminate);
const directory = await mkdtemp(path.join(tmpdir(), "ts498-integration-"));
let server;
let provision;
let checks;
const output = { log: "", started: false, admin: "", clientPort: "", pending: { stdout: "", stderr: "" } };
try {
  const certificate = path.join(directory, "certificate.pem");
  const privateKey = path.join(directory, "private-key.pem");
  execFileSync("openssl", ["req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1", "-subj", "/CN=localhost", "-addext", "subjectAltName=DNS:localhost,IP:127.0.0.1", "-keyout", privateKey, "-out", certificate], { stdio: "ignore" });
  const credential = randomBytes(32).toString("hex");
  const credentialFile = path.join(directory, "dashboard-credential");
  await writeFile(credentialFile, credential, { mode: 0o600, signal });
  const environment = Object.fromEntries(Object.entries(process.env).filter(([name]) => !name.startsWith("TERRABASE_") && !name.startsWith("Kestrel__")));
  signal.throwIfAborted();
  server = spawn(binary, ["--admin-port=0", "--client-port=0", `--data-directory=${directory}/data`, `--client-certificate=${certificate}`, `--client-private-key=${privateKey}`, `--dashboard-credential-file=${credentialFile}`], { env: environment, detached: true, stdio: ["ignore", "pipe", "pipe"] });
  server.stdout.on("data", chunk => captureProducerOutput(output, chunk, "stdout"));
  server.stderr.on("data", chunk => captureProducerOutput(output, chunk, "stderr"));
  let startupError;
  server.on("error", error => { startupError = error; });
  const deadline = Date.now() + 30_000;
  while (!output.started) {
    assert.ok(!startupError && server.exitCode === null && server.signalCode === null, `TerraBase failed to start: ${startupError?.message ?? output.log}`);
    assert.ok(Date.now() < deadline, `TerraBase startup timed out: ${output.log}`);
    await delay(50, undefined, { signal });
  }
  const { admin, clientPort } = output;
  assert.ok(admin && clientPort, "TerraBase did not report both real listeners.");
  const client = `https://localhost:${clientPort}`;
  const openapiResponse = await fetch(`${admin}${producerBootstrapEndpoints.openapi}`, { signal: AbortSignal.any([signal, AbortSignal.timeout(5000)]) });
  assert.equal(openapiResponse.status, 200);
  const openapi = Buffer.from(await openapiResponse.arrayBuffer());
  assert.equal(openapiDigest(openapi), pin.openapiSha256, "Runtime OpenAPI drifted from the pinned producer.");
  const healthRoute = provisioningRoute(JSON.parse(openapi.toString("utf8")), "healthz");
  const health = await fetch(`${admin}${healthRoute.path}`, { method: healthRoute.method, signal: AbortSignal.any([signal, AbortSignal.timeout(5000)]) }).then(response => response.json());
  assert.equal(health.version, pin.version);
  const openapiFile = path.join(directory, "openapi.json");
  await writeFile(openapiFile, openapi, { signal });
  // Trust this isolated certificate in a child process; never disable TLS validation.
  signal.throwIfAborted();
  provision = spawn(process.execPath, ["scripts/ci/provision-terrabase-tenant.mjs"], { env: { ...process.env, NODE_EXTRA_CA_CERTS: certificate, TB_V1_URL: client, TB_ADMIN_URL: admin, TB_DASHBOARD_CREDENTIAL: credential, TB_PROVISION_FILE: `${directory}/target.json`, TERRABASE_V1_OPENAPI: openapiFile }, detached: true, stdio: "inherit" });
  assert.equal((await once(provision, "exit", { signal }))[0], 0, "Real tenant provisioning failed.");
  const target = JSON.parse(await readFile(`${directory}/target.json`, { encoding: "utf8", signal }));
  const args = process.argv.slice(2);
  const command = args[0] ?? process.execPath;
  const commandArgs = args.length ? args.slice(1) : ["node_modules/vitest/vitest.mjs", "run", "test/v1-", "test/current-producer-contract.test.js", "test/openapi-drift.test.js", "test/public-contract-fixture.test.js", "test/contract-archive-pin.test.js"];
  signal.throwIfAborted();
  checks = spawn(command, commandArgs, { env: { ...process.env, ...target, TB_V1_REQUIRED: "1", NODE_EXTRA_CA_CERTS: certificate, TERRABASE_V1_OPENAPI: openapiFile }, detached: true, stdio: "inherit" });
  const [code, exitSignal] = await once(checks, "exit", { signal });
  assert.equal(exitSignal, null, "Integration command was terminated.");
  process.exitCode = code ?? 1;
} catch (error) {
  if (!signal.aborted) {
    console.error(output.log);
    throw error;
  }
} finally {
  const stopped = await Promise.allSettled([stopChild(checks, true), stopChild(provision, true), stopChild(server, true)]);
  for (const result of stopped) if (result.status === "rejected") throw result.reason;
  await rm(directory, { recursive: true, force: true });
  process.off("SIGINT", onInterrupt);
  process.off("SIGTERM", onTerminate);
}
