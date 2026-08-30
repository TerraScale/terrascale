import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdtemp, mkdir, readFile, readdir, realpath, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

const execute = promisify(execFile);
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const options = parseArguments(process.argv.slice(2));
const output = path.resolve(root, options.output);
const workspace = await mkdtemp(path.join(tmpdir(), "terrascale-npm-qualification-"));
const packageDirectory = path.join(workspace, "package");
const consumer = path.join(workspace, "consumer");
const startedAt = new Date().toISOString();
const consumerJourney = String.raw`import assert from "node:assert/strict";
import { createTerraScaleClient } from "@terrascale/sdk";
import { createTerraScaleAdminClient } from "@terrascale/sdk/admin";
import { createTerraScaleBetterAuthAdapter } from "@terrascale/sdk/better-auth";

const options = { apiBaseUrl: "https://api.example.test" };
assert.equal(typeof createTerraScaleClient(options), "object");
assert.equal(typeof createTerraScaleAdminClient(options), "object");
assert.equal(typeof createTerraScaleBetterAuthAdapter, "function");
await assert.rejects(import("@terrascale/sdk/local").then(({ createTerraScaleSqliteRuntime }) => createTerraScaleSqliteRuntime({ sqlite: { enabled: true, databasePath: "/tmp/qualification.sqlite" } })), /disabled/u);
`;

try {
  const revision = (await run("git", ["rev-parse", "HEAD"], root)).stdout.trim();
  const tree = (await run("git", ["write-tree"], root)).stdout.trim();
  const status = (await run("git", ["status", "--porcelain", "--untracked-files=all"], root)).stdout;
  if (status !== "" && !options.allowDirty) {
    throw new Error("Artifact qualification requires a clean Git worktree; commit the candidate or pass --allow-dirty for diagnostic evidence.");
  }

  await mkdir(output, { recursive: true });
  const packed = JSON.parse((await run("npm", ["pack", "--json", "--pack-destination", output], root)).stdout);
  if (!Array.isArray(packed) || packed.length !== 1 || typeof packed[0]?.filename !== "string") {
    throw new Error("npm pack did not describe exactly one artifact.");
  }
  const tarball = path.join(output, packed[0].filename);
  const tarballRealPath = await realpath(tarball);
  if (path.dirname(tarballRealPath) !== await realpath(output)) throw new Error("npm pack artifact escaped the output directory.");
  await run("tar", ["-xzf", tarball, "-C", workspace], root);

  const manifest = JSON.parse(await readFile(path.join(packageDirectory, "package.json"), "utf8"));
  qualifyManifest(manifest);
  const files = await listFiles(packageDirectory);
  qualifyContents(files);
  await scanForSecrets(packageDirectory, files);
  const graphs = {
    root: await browserGraph(packageDirectory, "dist/index.js"),
    admin: await browserGraph(packageDirectory, "dist/admin.js"),
  };

  await mkdir(consumer);
  await writeFile(path.join(consumer, "package.json"), JSON.stringify({ private: true, type: "module" }));
  await run("npm", ["install", "--ignore-scripts", "--no-audit", "--no-fund", tarball], consumer);
  const journey = path.join(consumer, "qualify.mjs");
  await writeFile(journey, consumerJourney);

  const runtimes = [];
  for (const executable of options.nodes) {
    const version = (await run(executable, ["--version"], consumer)).stdout.trim();
    const major = Number.parseInt(version.replace(/^v/u, "").split(".")[0] ?? "", 10);
    if (major !== 22 && major !== 24) throw new Error(`${executable} resolved unsupported ${version}; expected Node 22 or 24.`);
    await run(executable, [journey], consumer, qualificationEnvironment());
    runtimes.push({ executable, version, status: "passed" });
  }
  if (new Set(runtimes.map(({ version }) => version.split(".")[0])).size !== runtimes.length) {
    throw new Error("Each --node executable must qualify a distinct supported major runtime.");
  }

  const dependencyTree = JSON.parse((await run("npm", ["ls", "--omit=dev", "--all", "--json"], consumer)).stdout);
  const installedSdk = dependencyTree.dependencies?.[manifest.name];
  if (installedSdk?.version !== manifest.version) throw new Error("Clean consumer did not install the exact candidate version.");
  const productionDependencies = installedSdk.dependencies ?? {};
  const auditRun = await runAllowFailure("npm", ["audit", "--omit=dev", "--json"], consumer);
  const audit = parseJsonOutput(auditRun.stdout, "npm audit");
  const vulnerabilities = audit?.metadata?.vulnerabilities;
  if (auditRun.exitCode !== 0 || vulnerabilities === undefined || Object.values(vulnerabilities).some((count) => count !== 0)) {
    throw new Error(`Production dependency audit failed (exit ${auditRun.exitCode}).`);
  }

  const licenseText = await readFile(path.join(packageDirectory, "LICENSE"), "utf8");
  if (manifest.license !== "Apache-2.0" || !/Apache License\s+Version 2\.0/iu.test(licenseText)) {
    throw new Error("Packed license metadata and LICENSE must both identify Apache-2.0.");
  }

  const receipt = {
    schemaVersion: 1,
    status: "passed",
    startedAt,
    completedAt: new Date().toISOString(),
    source: { revision, tree, clean: status === "", dirtyStatusSha256: status === "" ? null : sha256(Buffer.from(status)) },
    artifact: {
      name: manifest.name,
      version: manifest.version,
      filename: path.basename(tarball),
      sha256: sha256(await readFile(tarball)),
      integrity: packed[0].integrity,
      size: packed[0].size,
      unpackedSize: packed[0].unpackedSize,
      fileCount: files.length,
    },
    boundaries: {
      exports: manifest.exports,
      browserEntrypointGraphs: graphs,
      localSqliteExport: "node-only",
      betterAuthExport: "node-only",
      packedSecretScan: "passed",
    },
    runtimes,
    dependencies: { count: Object.keys(productionDependencies).length, packages: productionDependencies },
    license: { declared: manifest.license, fileSha256: sha256(Buffer.from(licenseText)) },
    security: { audit: audit.metadata, registry: auditRun.registry ?? "https://registry.npmjs.org/" },
    command: `npm run qualify:artifact -- ${process.argv.slice(2).join(" ")}`.trim(),
    environment: { platform: process.platform, architecture: process.arch, npm: (await run("npm", ["--version"], root)).stdout.trim() },
    limitations: runtimes.length < 2 ? ["Only the explicitly supplied Node runtime executed; Node 22 and Node 24 are both required for release admission."] : [],
    redaction: "No environment values or package contents are embedded; dirty paths are represented only by a digest.",
  };
  const receiptPath = path.join(output, `${manifest.name.replace(/^@/u, "").replaceAll("/", "-")}-${manifest.version}.qualification.json`);
  await writeFile(receiptPath, `${JSON.stringify(receipt, null, 2)}\n`, { mode: 0o600 });
  process.stdout.write(`${JSON.stringify({ tarball, receipt: receiptPath, sha256: receipt.artifact.sha256, runtimes, limitations: receipt.limitations }, null, 2)}\n`);
} finally {
  await rm(workspace, { recursive: true, force: true });
}

function parseArguments(args) {
  const result = { output: "artifacts/npm-qualification", nodes: [], allowDirty: false };
  for (let index = 0; index < args.length; index += 1) {
    const argument = args[index];
    if (argument === "--output") result.output = requiredValue(args, ++index, argument);
    else if (argument === "--node") result.nodes.push(requiredValue(args, ++index, argument));
    else if (argument === "--allow-dirty") result.allowDirty = true;
    else throw new Error(`Unknown argument: ${argument}`);
  }
  if (result.nodes.length === 0) result.nodes.push(process.execPath);
  return result;
}

function requiredValue(args, index, option) {
  const value = args[index];
  if (value === undefined || value.startsWith("--")) throw new Error(`${option} requires a value.`);
  return value;
}

function qualifyManifest(manifest) {
  if (manifest.name !== "@terrascale/sdk") throw new Error("Unexpected package name.");
  if (manifest.private !== false) throw new Error("Packed candidate must be public.");
  if (Object.keys(manifest.dependencies ?? {}).length !== 0) throw new Error("Runtime dependencies require explicit license qualification.");
  for (const name of [".", "./admin"]) {
    const entry = manifest.exports?.[name];
    if (typeof entry?.import !== "string" || typeof entry?.types !== "string") throw new Error(`${name} must expose import and types conditions.`);
    if ("node" in entry || "default" in entry) throw new Error(`${name} unexpectedly has a Node-only/default condition.`);
  }
  for (const name of ["./better-auth", "./local"]) {
    const entry = manifest.exports?.[name];
    if (typeof entry?.node !== "string" || typeof entry?.types !== "string") throw new Error(`${name} must be explicitly Node-only.`);
    if ("import" in entry || "browser" in entry || "default" in entry) throw new Error(`${name} is reachable without the Node condition.`);
  }
}

function qualifyContents(files) {
  const forbidden = [/(^|\/)node_modules\//u, /(^|\/)src\//u, /(^|\/)test(?:s)?\//u, /\.env(?:\.|$)/u, /\.map$/u, /\.test\./u, /capture/iu];
  for (const file of files) for (const pattern of forbidden) if (pattern.test(file)) throw new Error(`Forbidden packed path: ${file}`);
  for (const required of ["LICENSE", "README.md", "package.json", "dist/index.js", "dist/index.d.ts", "dist/admin.js", "dist/admin.d.ts", "dist/better-auth.js", "dist/local/index.js"]) {
    if (!files.includes(required)) throw new Error(`Required packed path is missing: ${required}`);
  }
}

async function scanForSecrets(directory, files) {
  const patterns = [
    /-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----/u,
    /\b(?:npm|ghp|github_pat|glpat|sk_live|rk_live)_[A-Za-z0-9_-]{16,}\b/u,
    /\bAKIA[0-9A-Z]{16}\b/u,
    /(?:password|client_secret|access_token|api_key)\s*[:=]\s*["'][^"'\s]{8,}["']/iu,
  ];
  for (const file of files) {
    const bytes = await readFile(path.join(directory, file));
    if (bytes.includes(0)) continue;
    const text = bytes.toString("utf8");
    for (const pattern of patterns) if (pattern.test(text)) throw new Error(`Credential-like material found in ${file}.`);
  }
}

async function browserGraph(directory, entry) {
  const visited = new Set();
  const pending = [entry];
  while (pending.length > 0) {
    const relative = pending.pop();
    if (visited.has(relative)) continue;
    visited.add(relative);
    const source = await readFile(path.join(directory, relative), "utf8");
    const specifiers = [...source.matchAll(/(?:from\s*|import\s*)["']([^"']+)["']/gu)].map((match) => match[1]);
    for (const specifier of specifiers) {
      if (specifier.startsWith("node:") || specifier === "better-sqlite3") throw new Error(`${entry} browser graph imports ${specifier} through ${relative}.`);
      if (!specifier.startsWith(".")) continue;
      const target = path.posix.normalize(path.posix.join(path.posix.dirname(relative), specifier));
      if (target.startsWith("../") || target.includes("/local/") || target.startsWith("dist/local/")) throw new Error(`${entry} reaches local-only module ${target}.`);
      pending.push(target);
    }
  }
  return [...visited].sort();
}

async function listFiles(directory, prefix = "") {
  const result = [];
  for (const entry of await readdir(path.join(directory, prefix), { withFileTypes: true })) {
    const relative = path.posix.join(prefix, entry.name);
    if (entry.isDirectory()) result.push(...await listFiles(directory, relative));
    else if (entry.isFile()) result.push(relative);
    else throw new Error(`Packed artifact contains a non-regular path: ${relative}`);
  }
  return result.sort();
}

async function run(command, args, cwd, env = undefined) {
  return execute(command, args, { cwd, env, maxBuffer: 16 * 1024 * 1024 });
}

async function runAllowFailure(command, args, cwd) {
  try {
    const result = await run(command, args, cwd);
    return { ...result, exitCode: 0 };
  } catch (error) {
    if (typeof error === "object" && error !== null && "code" in error && "stdout" in error) {
      return { stdout: String(error.stdout), stderr: String(error.stderr ?? ""), exitCode: Number(error.code) };
    }
    throw error;
  }
}

function parseJsonOutput(output, command) {
  try { return JSON.parse(output); } catch { throw new Error(`${command} did not return JSON.`); }
}

function qualificationEnvironment() {
  const env = { PATH: process.env.PATH ?? "", NODE_ENV: "production", CI: "true" };
  return env;
}

function sha256(bytes) { return createHash("sha256").update(bytes).digest("hex"); }
