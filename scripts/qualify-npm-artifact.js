import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import {
  lstat,
  mkdtemp,
  mkdir,
  readFile,
  readdir,
  realpath,
  rm,
  writeFile,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { loadNpmContractEvidence } from "./npm-contract-evidence.js";
import { requireCurrentContractInputs } from "./openapi-contract-paths.js";
import { inspectNpmCli } from "./npm-cli-evidence.js";
import {
  npmReleasePolicy,
  qualificationFilename,
} from "./npm-release-policy.js";
import { inspectReleaseSource } from "./npm-release-source.js";
import { qualifyRuntimeDependencies, requireRuntimeDependencyPins } from "./npm-runtime-dependencies.js";
/** @import { NpmCli } from "./npm-cli-evidence.js" */

/**
 * @typedef {object} QualificationOptions
 * @property {string} output
 * @property {string[]} nodes
 * @property {boolean} allowDirty
 * @property {boolean} release
 * @property {string | undefined} tarball
 */

/**
 * @typedef {object} CommandOutput
 * @property {string} stdout
 * @property {string} stderr
 */

const execute = promisify(execFile);
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const options = parseArguments(process.argv.slice(2));
const output = path.resolve(root, options.output);
if (
  options.release &&
  options.tarball === undefined &&
  output !== path.join(root, npmReleasePolicy.outputRoot)
) {
  throw new Error(
    `Release qualification output must be ${npmReleasePolicy.outputRoot}.`,
  );
}
await requireAbsent(output);
const workspace = await mkdtemp(
  path.join(tmpdir(), "terrascale-npm-qualification-"),
);
const packageDirectory = path.join(workspace, "package");
const consumer = path.join(workspace, "consumer");
const startedAt = new Date().toISOString();
const consumerJourney = String.raw`import assert from "node:assert/strict";
import { createTerraBaseClient, TerraScaleCapabilityUnavailableError } from "@terrascale/sdk";
import { requireTerraScaleAdministration } from "@terrascale/sdk/admin";
import { createTsAuthClient } from "@terrascale/sdk/ts-auth";
const scope = { tenant: "00000000000000000000000001", database: "00000000000000000000000002", shard: 1n };
const client = createTerraBaseClient({ ...scope, endpoint: "https://gru.api.terrascale.tech", apiKey: "qualification-constructor-only" });
assert.equal(typeof client.read, "function");
assert.equal(typeof client.prepareCommit, "function");
for (const name of ["request", "documents", "executeSql", "sync", "http"]) assert.equal(name in client, false);
assert.equal(typeof createTsAuthClient({ apiBaseUrl: "https://auth.terrascale.tech", instanceSlug: "qualification" }), "object");
assert.throws(requireTerraScaleAdministration, TerraScaleCapabilityUnavailableError);
await assert.rejects(import("@terrascale/sdk/better-auth"), { code: "ERR_PACKAGE_PATH_NOT_EXPORTED" });
`;

try {
  if (options.release) await requireCurrentContractInputs();
  const source = await inspectReleaseSource(root, {
    allowDirty: options.allowDirty,
  });
  const npmCli = await inspectNpmCli({
    archiveIntegrity: options.release
      ? npmReleasePolicy.npm.archiveIntegrity
      : null,
  });
  if (options.release) {
    if (npmCli.evidence.version !== npmReleasePolicy.npm.version) {
      throw new Error(
        `Release qualification requires npm ${npmReleasePolicy.npm.version}.`,
      );
    }
    // Release mode always appends the Node 22 and Node 24 executables.
    const releaseNode24 = await realpath(
      /** @type {string} */ (options.nodes[1]),
    );
    if ((await realpath(process.execPath)) !== releaseNode24) {
      throw new Error(
        "Release qualification and npm packing must execute with the declared Node 24 binary.",
      );
    }
  }

  await mkdir(output, { recursive: true });
  /** @type {string} */
  let tarball;
  /** @type {Buffer | undefined} */
  let originalArtifact;
  if (options.tarball !== undefined) {
    if (!path.isAbsolute(options.tarball))
      throw new Error("--tarball must be an absolute path.");
    originalArtifact = await readFile(options.tarball);
    tarball = path.join(output, path.basename(options.tarball));
    await writeFile(tarball, originalArtifact, { flag: "wx" });
  } else {
    const packed = JSON.parse(
      (
        await runNpm(
          npmCli,
          ["pack", "--ignore-scripts", "--json", "--pack-destination", output],
          root,
        )
      ).stdout,
    );
    if (
      !Array.isArray(packed) ||
      packed.length !== 1 ||
      typeof packed[0]?.filename !== "string"
    ) {
      throw new Error("npm pack did not describe exactly one artifact.");
    }
    tarball = path.join(output, packed[0].filename);
  }
  const tarballRealPath = await realpath(tarball);
  if (path.dirname(tarballRealPath) !== (await realpath(output)))
    throw new Error("npm pack artifact escaped the output directory.");
  await run("tar", ["-xzf", tarball, "-C", workspace], root);

  const manifest = JSON.parse(
    await readFile(path.join(packageDirectory, "package.json"), "utf8"),
  );
  qualifyManifest(manifest);
  const files = await listFiles(packageDirectory);
  qualifyContents(files, manifest);
  await scanForSecrets(packageDirectory, files);
  if (
    !Buffer.from(
      await readFile(path.join(packageDirectory, "sdk-current-contract.json")),
    ).equals(await readFile(path.join(root, "sdk-current-contract.json")))
  )
    throw new Error(
      "Packed current producer pin differs from qualified source bytes.",
    );
  const contract = await loadNpmContractEvidence(
    root,
    path.join(packageDirectory, "sdk-route-manifest.json"),
  );
  const graphs = {
    root: await browserGraph(packageDirectory, "src/index.js"),
    admin: await browserGraph(packageDirectory, "src/admin.js"),
    // Retain the v1 receipt field; no adapter export means no adapter graph.
    betterAuthWorkerd: [],
  };

  await mkdir(consumer);
  await writeFile(
    path.join(consumer, "package.json"),
    JSON.stringify({
      private: true,
      type: "module",
    }),
  );
  await runNpm(
    npmCli,
    ["install", "--ignore-scripts", "--no-audit", "--no-fund", tarball],
    consumer,
  );
  const journey = path.join(consumer, "qualify.js");
  await writeFile(journey, consumerJourney);

  // Exercise the installed package's condition selection, including browser
  // rejection even when Node's own condition is also present.
  await run(
    process.execPath,
    ["--conditions=workerd", "--conditions=browser", journey],
    consumer,
    qualificationEnvironment(),
  );
  await run(
    process.execPath,
    [
      "--conditions=workerd",
      "--input-type=module",
      "-e",
      `
    import assert from "node:assert/strict";
    for (const entry of ["local", "postgres"]) await assert.rejects(import("@terrascale/sdk/" + entry), { code: "ERR_PACKAGE_PATH_NOT_EXPORTED" });
  `,
    ],
    consumer,
    qualificationEnvironment(),
  );
  await run(
    process.execPath,
    [
      "--conditions=browser",
      "--input-type=module",
      "-e",
      `
    import assert from "node:assert/strict";
    for (const entry of ["better-auth", "local", "postgres"]) await assert.rejects(import("@terrascale/sdk/" + entry), { code: "ERR_PACKAGE_PATH_NOT_EXPORTED" });
  `,
    ],
    consumer,
    qualificationEnvironment(),
  );

  /** @type {{ major: number; executable: string; executableSha256: string; version: string; status: string }[]} */
  const runtimes = [];
  for (const executable of options.nodes) {
    if (!path.isAbsolute(executable))
      throw new Error(
        `Node executable must be an absolute path: ${executable}`,
      );
    const resolvedExecutable = await realpath(executable);
    const executableMetadata = await lstat(resolvedExecutable);
    if (!executableMetadata.isFile() || executableMetadata.isSymbolicLink()) {
      throw new Error(
        `Node executable must resolve to a regular file: ${executable}`,
      );
    }
    const version = (
      await run(resolvedExecutable, ["--version"], consumer)
    ).stdout.trim();
    const major = Number.parseInt(
      version.replace(/^v/u, "").split(".")[0] ?? "",
      10,
    );
    if (major !== 22 && major !== 24)
      throw new Error(
        `${executable} resolved unsupported ${version}; expected Node 22 or 24.`,
      );
    if (options.release && npmReleasePolicy.nodeVersions[major] !== version) {
      throw new Error(
        `${executable} resolved ${version}; release qualification requires ${npmReleasePolicy.nodeVersions[major]}.`,
      );
    }
    await run(
      resolvedExecutable,
      [journey],
      consumer,
      qualificationEnvironment(),
    );
    runtimes.push({
      major,
      executable: resolvedExecutable,
      executableSha256: sha256(await readFile(resolvedExecutable)),
      version,
      status: "passed",
    });
  }
  if (new Set(runtimes.map(({ major }) => major)).size !== runtimes.length) {
    throw new Error(
      "Each --node executable must qualify a distinct supported major runtime.",
    );
  }
  if (
    options.release &&
    runtimes
      .map(({ major }) => major)
      .sort()
      .join(",") !== "22,24"
  ) {
    throw new Error(
      "Release qualification requires exactly one Node 22 and one Node 24 executable.",
    );
  }

  const dependencyTree = JSON.parse(
    (await runNpm(npmCli, ["ls", "--omit=dev", "--all", "--json"], consumer))
      .stdout,
  );
  const installedSdk = dependencyTree.dependencies?.[manifest.name];
  if (installedSdk?.version !== manifest.version)
    throw new Error(
      "Clean consumer did not install the exact candidate version.",
    );
  const productionDependencies = await qualifyRuntimeDependencies(consumer, manifest.dependencies);
  const auditRun = await runNpmAllowFailure(
    npmCli,
    ["audit", "--omit=dev", "--json"],
    consumer,
  );
  const audit = parseJsonOutput(auditRun.stdout, "npm audit");
  const vulnerabilities = audit?.metadata?.vulnerabilities;
  if (
    auditRun.exitCode !== 0 ||
    vulnerabilities === undefined ||
    Object.values(vulnerabilities).some((count) => count !== 0)
  ) {
    throw new Error(
      `Production dependency audit failed (exit ${auditRun.exitCode}).`,
    );
  }

  const licenseText = await readFile(
    path.join(packageDirectory, "LICENSE"),
    "utf8",
  );
  if (
    manifest.license !== "Apache-2.0" ||
    !/Apache License\s+Version 2\.0/iu.test(licenseText)
  ) {
    throw new Error(
      "Packed license metadata and LICENSE must both identify Apache-2.0.",
    );
  }

  const artifactBytes = await readFile(tarball);
  if (
    originalArtifact !== undefined &&
    (!artifactBytes.equals(originalArtifact) ||
      // A retained original artifact implies a prepacked --tarball path.
      !(await readFile(/** @type {string} */ (options.tarball))).equals(
        originalArtifact,
      ))
  )
    throw new Error("Prepacked npm artifact changed during qualification.");
  let unpackedSize = 0;
  for (const file of files)
    unpackedSize += (await readFile(path.join(packageDirectory, file))).length;

  /** @type {string[]} */
  const limitations = [];
  if (!options.release)
    limitations.push(
      "Diagnostic qualification was not produced in fail-closed release mode.",
    );
  if (!source.clean)
    limitations.push(
      "The qualified Git worktree contained uncommitted source changes.",
    );
  if (runtimes.length < 2)
    limitations.push(
      "Node 22 and Node 24 are both required for release admission.",
    );
  const receipt = {
    schemaVersion: npmReleasePolicy.qualificationSchemaVersion,
    mediaType: npmReleasePolicy.qualificationMediaType,
    status: "passed",
    startedAt,
    completedAt: new Date().toISOString(),
    source,
    release: {
      eligible: options.release && source.clean && limitations.length === 0,
      nodeVersions: npmReleasePolicy.nodeVersions,
    },
    artifact: {
      name: manifest.name,
      version: manifest.version,
      filename: path.basename(tarball),
      sha256: sha256(artifactBytes),
      integrity: `sha512-${createHash("sha512").update(artifactBytes).digest("base64")}`,
      size: artifactBytes.length,
      unpackedSize,
      fileCount: files.length,
    },
    boundaries: {
      exports: manifest.exports,
      browserEntrypointGraphs: graphs,
      betterAuthExport: "absent",
      packedSecretScan: "passed",
    },
    contract,
    runtimes,
    dependencies: productionDependencies,
    license: {
      declared: manifest.license,
      fileSha256: sha256(Buffer.from(licenseText)),
    },
    security: {
      audit: audit.metadata,
      registry: auditRun.registry ?? "https://registry.npmjs.org/",
    },
    command:
      `npm run qualify:artifact -- ${process.argv.slice(2).join(" ")}`.trim(),
    environment: {
      platform: process.platform,
      architecture: process.arch,
      npm: npmCli.evidence,
    },
    limitations,
    redaction:
      "No environment values or package contents are embedded; dirty paths and workflow tooling are represented only by bounded classification evidence.",
  };
  const receiptPath = path.join(
    output,
    qualificationFilename(manifest.version),
  );
  await writeFile(receiptPath, `${JSON.stringify(receipt, null, 2)}\n`, {
    flag: "wx",
    mode: 0o600,
  });
  process.stdout.write(
    `${JSON.stringify({ tarball, receipt: receiptPath, sha256: receipt.artifact.sha256, runtimes, limitations: receipt.limitations }, null, 2)}\n`,
  );
} finally {
  await rm(workspace, { recursive: true, force: true });
}

/**
 * @param {readonly string[]} args
 * @returns {QualificationOptions}
 */
function parseArguments(args) {
  /** @type {QualificationOptions} */
  const result = {
    output: "artifacts/npm-qualification",
    nodes: [],
    allowDirty: false,
    release: false,
    tarball: undefined,
  };
  for (let index = 0; index < args.length; index += 1) {
    const argument = args[index];
    if (argument === "--tarball")
      result.tarball = requiredValue(args, ++index, argument);
    else if (argument === "--output")
      result.output = requiredValue(args, ++index, argument);
    else if (argument === "--node")
      result.nodes.push(requiredValue(args, ++index, argument));
    else if (argument === "--allow-dirty") result.allowDirty = true;
    else if (argument === "--release") result.release = true;
    else throw new Error(`Unknown argument: ${argument}`);
  }
  if (result.release) {
    if (result.allowDirty)
      throw new Error("--allow-dirty cannot be combined with --release.");
    if (result.nodes.length !== 0)
      throw new Error(
        "--release obtains its exact Node executables from TERRASCALE_NODE_22 and TERRASCALE_NODE_24.",
      );
    result.nodes.push(
      requiredEnvironmentPath("TERRASCALE_NODE_22"),
      requiredEnvironmentPath("TERRASCALE_NODE_24"),
    );
    if (
      process.env.TERRASCALE_NPM_ARCHIVE_INTEGRITY !==
      npmReleasePolicy.npm.archiveIntegrity
    ) {
      throw new Error(
        "TERRASCALE_NPM_ARCHIVE_INTEGRITY must match the reviewed npm release archive.",
      );
    }
  } else if (result.nodes.length === 0) {
    result.nodes.push(process.execPath);
  }
  return result;
}

/**
 * @param {string} name
 * @returns {string}
 */
function requiredEnvironmentPath(name) {
  const value = process.env[name];
  if (value === undefined || value.trim() === "" || !path.isAbsolute(value)) {
    throw new Error(
      `${name} must name an absolute Node executable for release qualification.`,
    );
  }
  return value;
}

/**
 * @param {readonly string[]} args
 * @param {number} index
 * @param {string} option
 * @returns {string}
 */
function requiredValue(args, index, option) {
  const value = args[index];
  if (value === undefined || value.startsWith("--"))
    throw new Error(`${option} requires a value.`);
  return value;
}

/**
 * @param {any} manifest
 * @returns {void}
 */
function qualifyManifest(manifest) {
  if (manifest.name !== npmReleasePolicy.packageName)
    throw new Error("Unexpected package name.");
  if (manifest.private !== false)
    throw new Error("Packed candidate must be public.");
  requireRuntimeDependencyPins(manifest.dependencies);
  for (const name of [".", "./admin"]) {
    const entry = manifest.exports?.[name];
    if (typeof entry?.import !== "string" || typeof entry?.types !== "string")
      throw new Error(`${name} must expose import and types conditions.`);
    if ("node" in entry || "default" in entry)
      throw new Error(
        `${name} unexpectedly has a Node-only/default condition.`,
      );
  }
  if (Object.hasOwn(manifest.exports, "./better-auth"))
    throw new Error("The planned Better Auth adapter must not be a public export.");
  for (const name of ["./local", "./postgres"]) {
    const entry = manifest.exports?.[name];
    if (
      typeof entry?.node !== "string" ||
      typeof entry?.types !== "string" ||
      entry.browser !== null ||
      entry.workerd !== null ||
      "import" in entry ||
      "default" in entry
    ) {
      throw new Error(
        `${name} must expose only explicit Node conditions and block browsers.`,
      );
    }
  }
}

/**
 * @param {readonly string[]} files
 * @param {any} manifest
 * @returns {void}
 */
function qualifyContents(files, manifest) {
  const forbidden = [
    /(^|\/)node_modules\//u,
    /(^|\/)integration\//u,
    /(^|\/)test(?:s)?\//u,
    /\.env(?:\.|$)/u,
    /\.map$/u,
    /\.test\./u,
    /capture/iu,
    /^(?:src\/better-auth\.js|types\/better-auth\.d\.ts)$/u,
  ];
  for (const file of files)
    for (const pattern of forbidden)
      if (pattern.test(file)) throw new Error(`Forbidden packed path: ${file}`);
  for (const required of [
    "LICENSE",
    "README.md",
    "package.json",
    "sdk-route-manifest.json",
    "sdk-current-contract.json",
    "sdk-v1-route-manifest.json",
    "src/index.js",
    "types/index.d.ts",
    "src/admin.js",
    "types/admin.d.ts",
  ]) {
    if (!files.includes(required))
      throw new Error(`Required packed path is missing: ${required}`);
  }
  for (const conditions of Object.values(manifest.exports)) {
    for (const target of Object.values(
      /** @type {Record<string, unknown>} */ (conditions),
    )) {
      if (
        typeof target === "string" &&
        !files.includes(target.replace(/^\.\//u, ""))
      ) {
        throw new Error(
          `Declared public export is absent from the packed artifact: ${target}`,
        );
      }
    }
  }
}

/**
 * @param {string} directory
 * @param {readonly string[]} files
 * @returns {Promise<void>}
 */
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
    for (const pattern of patterns)
      if (pattern.test(text))
        throw new Error(`Credential-like material found in ${file}.`);
  }
}

/**
 * @param {string} directory
 * @param {string} entry
 * @returns {Promise<string[]>}
 */
async function browserGraph(directory, entry) {
  /** @type {Set<string>} */
  const visited = new Set();
  const pending = [entry];
  while (pending.length > 0) {
    const relative = /** @type {string} */ (pending.pop());
    if (relative === "src/better-auth.js") {
      throw new Error(`${entry} reaches the removed Better Auth entrypoint.`);
    }
    if (visited.has(relative)) continue;
    visited.add(relative);
    const source = await readFile(path.join(directory, relative), "utf8");
    for (const dynamicImport of source.matchAll(/\bimport\s*\(\s*/gu)) {
      const quote = source[dynamicImport.index + dynamicImport[0].length];
      if (quote !== '"' && quote !== "'")
        throw new Error(
          `${entry} has an unresolved dynamic import through ${relative}.`,
        );
    }
    const specifiers = [
      ...source.matchAll(/(?:from\s*|import\s*(?:\(\s*)?)["']([^"']+)["']/gu),
    ].map((match) => /** @type {string} */ (match[1]));
    for (const specifier of specifiers) {
      if (specifier.startsWith("node:") || specifier === "better-sqlite3")
        throw new Error(
          `${entry} browser graph imports ${specifier} through ${relative}.`,
        );
      if (!specifier.startsWith(".")) continue;
      const target = path.posix.normalize(
        path.posix.join(path.posix.dirname(relative), specifier),
      );
      if (target.startsWith("../"))
        throw new Error(
          `${entry} reaches a module outside the browser graph at ${target}.`,
        );
      pending.push(target);
    }
  }
  return [...visited].sort();
}

/**
 * @param {string} directory
 * @param {string} [prefix]
 * @returns {Promise<string[]>}
 */
async function listFiles(directory, prefix = "") {
  /** @type {string[]} */
  const result = [];
  for (const entry of await readdir(path.join(directory, prefix), {
    withFileTypes: true,
  })) {
    const relative = path.posix.join(prefix, entry.name);
    if (entry.isDirectory())
      result.push(...(await listFiles(directory, relative)));
    else if (entry.isFile()) result.push(relative);
    else
      throw new Error(
        `Packed artifact contains a non-regular path: ${relative}`,
      );
  }
  return result.sort();
}

/**
 * @param {string} command
 * @param {readonly string[]} args
 * @param {string} cwd
 * @param {NodeJS.ProcessEnv} [env]
 * @returns {Promise<CommandOutput>}
 */
async function run(command, args, cwd, env = undefined) {
  return execute(command, args, { cwd, env, maxBuffer: 16 * 1024 * 1024 });
}

/**
 * @param {string} command
 * @param {readonly string[]} args
 * @param {string} cwd
 * @returns {Promise<CommandOutput & { exitCode: number; registry?: string }>}
 */
async function runAllowFailure(command, args, cwd) {
  try {
    const result = await run(command, args, cwd);
    return { ...result, exitCode: 0 };
  } catch (error) {
    if (
      typeof error === "object" &&
      error !== null &&
      "code" in error &&
      "stdout" in error
    ) {
      return {
        stdout: String(error.stdout),
        stderr: String(
          /** @type {{ stderr?: unknown }} */ (error).stderr ?? "",
        ),
        exitCode: Number(error.code),
      };
    }
    throw error;
  }
}

/**
 * @param {NpmCli} npmCli
 * @param {readonly string[]} args
 * @param {string} cwd
 * @returns {Promise<CommandOutput>}
 */
async function runNpm(npmCli, args, cwd) {
  return run(process.execPath, [npmCli.entrypoint, ...args], cwd);
}

/**
 * @param {NpmCli} npmCli
 * @param {readonly string[]} args
 * @param {string} cwd
 * @returns {ReturnType<typeof runAllowFailure>}
 */
async function runNpmAllowFailure(npmCli, args, cwd) {
  return runAllowFailure(process.execPath, [npmCli.entrypoint, ...args], cwd);
}

/**
 * @param {string} output
 * @param {string} command
 * @returns {any}
 */
function parseJsonOutput(output, command) {
  try {
    return JSON.parse(output);
  } catch {
    throw new Error(`${command} did not return JSON.`);
  }
}

/** @returns {NodeJS.ProcessEnv} */
function qualificationEnvironment() {
  const env = {
    PATH: process.env.PATH ?? "",
    NODE_ENV: "production",
    CI: "true",
  };
  return env;
}

/**
 * @param {Buffer} bytes
 * @returns {string}
 */
function sha256(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}

/**
 * @param {string} target
 * @returns {Promise<void>}
 */
async function requireAbsent(target) {
  try {
    await lstat(target);
  } catch (error) {
    if (
      error !== null &&
      typeof error === "object" &&
      "code" in error &&
      error.code === "ENOENT"
    )
      return;
    throw error;
  }
  throw new Error(`Qualification output already exists: ${target}`);
}
