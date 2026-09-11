#!/usr/bin/env node
// Deterministic, local-only cross-SDK journey fixture.
//
// This server is deliberately not a Dashboard or Database implementation. It
// exists to prove that independently packaged clients can share one public
// HTTP shape after a synthetic account/onboarding/key lifecycle. The emitted
// report is fixture evidence only and must never be used as cloud evidence.
import { createHash } from "node:crypto";
import { spawn } from "node:child_process";
import { createServer } from "node:http";
import { readFile, writeFile } from "node:fs/promises";
import { URL } from "node:url";

const JSON_HEADERS = { "content-type": "application/json; charset=utf-8" };
const RUN_ID_RE = /^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/u;

function json(res, status, body, headers = {}) {
  res.writeHead(status, { ...JSON_HEADERS, ...headers });
  res.end(JSON.stringify(body));
}

async function body(request) {
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  if (chunks.length === 0) return {};
  try { return JSON.parse(Buffer.concat(chunks).toString("utf8")); }
  catch { throw new Error("fixture received malformed JSON"); }
}

function bearer(request) {
  const value = request.headers.authorization;
  return typeof value === "string" && value.startsWith("Bearer ") ? value.slice(7) : "";
}

/** Start the deterministic fixture. The returned close function is async. */
export function createFixtureServer({ runId = "fixture-journey", key = `fixture-key-${runId}` } = {}) {
  if (!RUN_ID_RE.test(runId)) throw new Error("runId must match [A-Za-z0-9][A-Za-z0-9_-]{0,63}");
  const documents = new Map();
  let account = { created: false, verified: false, email: null };
  let onboarding = { completed: false, organizationId: null, projectId: null };
  let keyIssued = false;
  const server = createServer(async (request, response) => {
    try {
      const url = new URL(request.url ?? "/", "http://fixture.invalid");
      const method = request.method ?? "GET";
      if (method === "GET" && url.pathname === "/__fixture__/health") {
        return json(response, 200, { fixture: true, cloudQualification: false, runId });
      }
      if (method === "POST" && url.pathname === "/__fixture__/signup") {
        const input = await body(request);
        if (typeof input.email !== "string" || input.email.trim() === "") return json(response, 422, { error: "email_required" });
        account = { created: true, verified: false, email: input.email.trim() };
        return json(response, 201, { accountCreated: true, emailVerified: false, verificationToken: `verify-${runId}` });
      }
      if (method === "POST" && url.pathname === "/__fixture__/verify") {
        const input = await body(request);
        if (!account.created || input.token !== `verify-${runId}`) return json(response, 400, { error: "invalid_verification" });
        account.verified = true;
        return json(response, 200, { accountCreated: true, emailVerified: true });
      }
      if (method === "POST" && url.pathname === "/__fixture__/onboarding") {
        if (!account.verified) return json(response, 403, { error: "email_not_verified" });
        onboarding = { completed: true, organizationId: `org-${runId}`, projectId: `project-${runId}` };
        return json(response, 201, onboarding);
      }
      if (method === "POST" && url.pathname === "/__fixture__/api-key") {
        if (!onboarding.completed) return json(response, 403, { error: "onboarding_required" });
        keyIssued = true;
        // This is a synthetic test credential, not a production secret.
        return json(response, 201, { apiKey: key, projectId: onboarding.projectId, synthetic: true });
      }
      const match = url.pathname.match(/^\/api\/v1\/databases\/([^/]+)\/collections\/([^/]+)\/documents(?:\/([^/]+))?$/u);
      if (match) {
        if (!keyIssued || bearer(request) !== key) return json(response, 401, { error: "unauthorized" });
        const id = match[3] === undefined ? null : decodeURIComponent(match[3]);
        const collection = decodeURIComponent(match[2]);
        if (id === null) {
          if (method !== "GET") return json(response, 405, { error: "method_not_allowed" });
          const rows = [...documents.values()].filter((entry) => entry.collection === collection).map((entry) => ({ id: entry.id, version: 1, value: entry.value }));
          return json(response, 200, { documents: rows });
        }
        const documentKey = `${collection}/${id}`;
        if (method === "GET") {
          const entry = documents.get(documentKey);
          return entry === undefined ? json(response, 404, { error: "not_found" }) : json(response, 200, entry.value, { etag: '"fixture-etag"' });
        }
        if (method === "PUT") {
          const value = await body(request);
          documents.set(documentKey, { id, collection, value });
          return json(response, 200, { revision: "fixture-revision" }, { etag: '"fixture-etag"' });
        }
        return json(response, 405, { error: "method_not_allowed" });
      }
      return json(response, 404, { error: "fixture_route_not_found" });
    } catch (error) {
      return json(response, 500, { error: error instanceof Error ? error.message : "fixture_failure" });
    }
  });
  return {
    server,
    // Evidence is intentionally metadata-only: document bodies and the
    // synthetic key must never be copied into a retained qualification report.
    state: () => ({ account: { created: account.created, verified: account.verified }, onboarding, keyIssued, documentIds: [...documents.values()].map(({ collection, id }) => `${collection}/${id}`) }),
    listen: () => new Promise((resolve, reject) => {
      server.once("error", reject);
      server.listen(0, "127.0.0.1", () => resolve(server.address().port));
    }),
    close: () => new Promise((resolve, reject) => server.close((error) => error ? reject(error) : resolve())),
  };
}

async function run(command, env, cwd) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, { shell: true, cwd, env, stdio: ["ignore", "pipe", "pipe"] });
    let output = "";
    child.stdout.on("data", (chunk) => { output += chunk; });
    child.stderr.on("data", (chunk) => { output += chunk; });
    child.once("error", reject);
    child.once("exit", (code) => code === 0 ? resolve(output.slice(-2048)) : reject(new Error(`${command} exited with ${code}: ${output.slice(-512)}`)));
  });
}

/** Run the complete fixture lifecycle and caller-supplied language drivers. */
export async function runJourney({ runId = "fixture-journey", resultPath, cwd = process.cwd(), drivers = {} } = {}) {
  const fixture = createFixtureServer({ runId });
  const port = await fixture.listen();
  const apiUrl = `http://127.0.0.1:${port}`;
  const key = `fixture-key-${runId}`;
  const report = { evidence: "deterministic-local-fixture-only", cloudQualification: false, runId, apiUrl, phases: [], complete: false };
  try {
    const request = (path, options = {}) => fetch(`${apiUrl}${path}`, { ...options, headers: { "content-type": "application/json", ...(options.headers ?? {}) } });
    const signup = await request("/__fixture__/signup", { method: "POST", body: JSON.stringify({ email: `${runId}@fixture.invalid` }) });
    if (signup.status !== 201) throw new Error(`signup failed: ${signup.status}`);
    report.phases.push("signup");
    const verify = await request("/__fixture__/verify", { method: "POST", body: JSON.stringify({ token: `verify-${runId}` }) });
    if (!verify.ok) throw new Error(`verification failed: ${verify.status}`);
    report.phases.push("email-verification");
    const onboard = await request("/__fixture__/onboarding", { method: "POST", body: "{}" });
    if (!onboard.ok) throw new Error(`onboarding failed: ${onboard.status}`);
    report.phases.push("onboarding");
    const issued = await request("/__fixture__/api-key", { method: "POST", body: "{}" });
    if (!issued.ok) throw new Error(`api-key issuance failed: ${issued.status}`);
    report.phases.push("api-key-issuance");
    const driverEnv = { ...process.env, TERRASCALE_API_URL: apiUrl, TERRASCALE_API_KEY: key, TERRASCALE_PROJECT_ID: `project-${runId}`, TERRASCALE_DATABASE_ID: `database-${runId}`, TERRASCALE_JOURNEY_RUN_ID: runId, QUALIFICATION_EVIDENCE: "deterministic-local-fixture-only" };
    for (const name of ["typescript", "go", "rust", "dotnet", "cli"]) {
      if (typeof drivers[name] !== "string" || drivers[name].trim() === "") throw new Error(`missing ${name} driver; fixture journey fails closed`);
      await run(drivers[name], driverEnv, cwd);
      // A successful process is insufficient evidence: each driver must have
      // made one authenticated write through its packaged SDK/CLI.
      if (!fixture.state().documentIds.includes(`sdk_journey/${runId}-${name}`)) throw new Error(`${name} driver exited successfully without writing its required document`);
      report.phases.push(`${name}-sdk`);
    }
    report.complete = true;
    return report;
  } finally {
    report.state = fixture.state();
    if (resultPath) await writeFile(resultPath, `${JSON.stringify(report, null, 2)}\n`, { mode: 0o600 });
    await fixture.close();
  }
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const runId = process.env.TERRASCALE_FIXTURE_RUN_ID ?? "fixture-journey";
  const resultPath = process.env.TERRASCALE_FIXTURE_RESULT;
  const drivers = Object.fromEntries(["typescript", "go", "rust", "dotnet", "cli"].map((name) => [name, process.env[`TERRASCALE_FIXTURE_DRIVER_${name.toUpperCase()}`]]));
  try {
    const report = await runJourney({ runId, resultPath, drivers });
    console.log(JSON.stringify({ ...report, apiUrl: undefined, state: undefined }));
  } catch (error) {
    console.error(error instanceof Error ? error.message : "fixture journey failed");
    process.exitCode = 1;
  }
}
