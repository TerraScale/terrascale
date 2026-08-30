#!/usr/bin/env node

import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import { lstat, mkdtemp, readFile, realpath, readdir, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { loadNpmContractEvidence } from "./npm-contract-evidence.mjs";
import { inspectNpmCli } from "./npm-cli-evidence.mjs";
import {
  npmReleasePolicy,
  qualificationFilename,
  tarballFilename,
} from "./npm-release-policy.mjs";
import { gitTreeDigest, inspectReleaseSource } from "./npm-release-source.mjs";
import { parseStrictJson, requireExactKeys } from "./strict-json.mjs";

const execute = promisify(execFile);
const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const options = parseOptions(process.argv.slice(2));
const publishToken = process.env.NPM_TOKEN;
delete process.env.NPM_TOKEN;

if (options.checkAvailability) {
  if (options.package === undefined || options.version === undefined || options.registry === undefined) {
    console.error("Usage: node scripts/promote-npm-candidate.mjs --check-availability --package <name> --version <version> --registry <url>");
    process.exitCode = 2;
  } else {
    const { npmCli } = await inspectPromotionToolchain();
    await ensureVersionAvailable(options.package, options.version, options.registry, npmCli);
  }
} else if (options.buildReceipt === undefined || options.artifacts === undefined || options.registry === undefined) {
  console.error("Usage: node scripts/promote-npm-candidate.mjs --build-receipt <path> --artifacts <path> --registry <url> [--verify-only]");
  process.exitCode = 2;
} else {
  const candidate = await verifyCandidate(options);
  if (options.verifyOnly) {
    console.log(`Verified ${candidate.name}@${candidate.version} candidate ${candidate.tarball} and its qualification receipt.`);
  } else {
    await ensureVersionAvailable(candidate.name, candidate.version, options.registry, candidate.npmCli);
    await publishCandidate(candidate, options.registry, publishToken);
  }
}

function parseOptions(arguments_) {
  const options = {};
  const allowedValues = new Set(["--package", "--version", "--registry", "--build-receipt", "--artifacts"]);
  for (let index = 0; index < arguments_.length; index += 1) {
    const argument = arguments_[index];
    if (argument === "--verify-only" || argument === "--check-availability") {
      const key = argument === "--verify-only" ? "verifyOnly" : "checkAvailability";
      if (options[key] !== undefined) throw new Error(`Duplicate argument: ${argument}`);
      options[key] = true;
      continue;
    }
    if (!allowedValues.has(argument) || arguments_[index + 1] === undefined || arguments_[index + 1].startsWith("--")) {
      throw new Error(`Invalid argument: ${argument}`);
    }
    const key = argument.slice(2).replace(/-([a-z])/gu, (_, letter) => letter.toUpperCase());
    if (options[key] !== undefined) throw new Error(`Duplicate argument: ${argument}`);
    options[key] = arguments_[index + 1];
    index += 1;
  }
  return options;
}

async function verifyCandidate(candidateOptions) {
  requirePublicRegistry(candidateOptions.registry);
  const [source, toolchain] = await Promise.all([
    inspectReleaseSource(repositoryRoot),
    inspectPromotionToolchain(),
  ]);
  const { node, npmCli } = toolchain;
  const buildReceiptPath = await verifiedStandaloneFile(candidateOptions.buildReceipt, "Candidate build receipt");
  const buildReceiptBytes = await readFile(buildReceiptPath);
  const buildReceipt = parseBuildReceipt(buildReceiptBytes);
  await verifyBuildReceiptSource(buildReceipt, source);

  const sourceManifest = parseStrictJson(
    await readFile(path.join(repositoryRoot, "package.json")),
    "Checked-in package manifest",
  );
  if (sourceManifest.name !== npmReleasePolicy.packageName || typeof sourceManifest.version !== "string") {
    throw new Error("Checked-in package identity is invalid.");
  }

  const expectedTarball = tarballFilename(sourceManifest.version);
  const expectedQualification = qualificationFilename(sourceManifest.version);
  const output = await snapshotCandidateOutput(candidateOptions.artifacts);
  if (
    output.files.length !== 2 ||
    output.files[0].name !== expectedQualification ||
    output.files[1].name !== expectedTarball
  ) {
    throw new Error("Candidate output must contain exactly the versioned npm tarball and qualification receipt.");
  }
  verifyBuildOutput(buildReceipt, output);

  const tarball = output.files.find(({ name }) => name === expectedTarball);
  const qualification = output.files.find(({ name }) => name === expectedQualification);
  if (tarball === undefined || qualification === undefined) throw new Error("Candidate output is incomplete.");

  const qualificationReceipt = parseStrictJson(
    qualification.bytesBuffer,
    "npm qualification receipt",
  );
  const packageJsonBytes = await readTarEntry(tarball.path, "package/package.json");
  const packageJson = parseStrictJson(packageJsonBytes, "Packed package manifest");
  const packedRouteManifest = await readTarEntry(tarball.path, "package/sdk-route-manifest.json");
  const packedLicense = await readTarEntry(tarball.path, "package/LICENSE");
  const contract = await loadNpmContractEvidence(repositoryRoot);
  verifyQualificationReceipt({
    receipt: qualificationReceipt,
    receiptFilename: qualification.name,
    tarball,
    packageJson,
    packedRouteManifest,
    packedLicense,
    source,
    buildReceipt,
    contract,
    sourceManifest,
    node,
    npmCli,
  });

  return {
    name: packageJson.name,
    version: packageJson.version,
    tarball: tarball.path,
    npmCli,
  };
}

function parseBuildReceipt(bytes) {
  const receipt = requireExactKeys(parseStrictJson(bytes, "Candidate build receipt"), [
    "schemaVersion",
    "mediaType",
    "repository",
    "source",
    "configurationDigest",
    "deployCli",
    "target",
    "output",
    "startedAt",
    "completedAt",
  ], "Candidate build receipt");
  requireExactKeys(receipt.source, ["commit", "tree", "treeDigest"], "Candidate build receipt source");
  requireExactKeys(receipt.deployCli, ["package", "version", "executableDigest"], "Candidate build receipt CLI");
  requireExactKeys(receipt.output, ["root", "digest", "fileCount", "bytes", "files"], "Candidate build receipt output");
  if (!Array.isArray(receipt.output.files)) throw new Error("Candidate build receipt files must be an array.");
  for (const file of receipt.output.files) {
    requireExactKeys(file, ["path", "digest", "bytes"], "Candidate build receipt file");
  }

  if (
    receipt.schemaVersion !== npmReleasePolicy.buildReceiptSchemaVersion ||
    receipt.mediaType !== npmReleasePolicy.buildReceiptMediaType ||
    receipt.repository !== "TerraScale.TypeScript" ||
    receipt.target !== npmReleasePolicy.buildTarget ||
    receipt.output.root !== npmReleasePolicy.outputRoot
  ) {
    throw new Error("Candidate build receipt has the wrong schema, repository, target, or output root.");
  }
  requireLowerHex(receipt.source.commit, 40, "Candidate source commit");
  if (typeof receipt.source.tree !== "string" || !/^(?:[0-9a-f]{40}|[0-9a-f]{64})$/u.test(receipt.source.tree)) {
    throw new Error("Candidate source tree is invalid.");
  }
  requireDigest(receipt.source.treeDigest, "Candidate source tree digest");
  requireDigest(receipt.configurationDigest, "Candidate configuration digest");
  if (
    receipt.deployCli.package !== "TerraScale.Deploy.Cli" ||
    typeof receipt.deployCli.version !== "string" ||
    !/^[0-9A-Za-z.-]{1,64}$/u.test(receipt.deployCli.version) ||
    receipt.deployCli.version.split(".").length < 3
  ) {
    throw new Error("Candidate build receipt CLI identity is invalid.");
  }
  requireDigest(receipt.deployCli.executableDigest, "Candidate Deploy CLI digest");
  const startedAt = parseUtc(receipt.startedAt, "Candidate build start");
  const completedAt = parseUtc(receipt.completedAt, "Candidate build completion");
  if (completedAt < startedAt || completedAt > Date.now() + 5 * 60 * 1000) {
    throw new Error("Candidate build receipt time evidence is invalid.");
  }
  requireDigest(receipt.output.digest, "Candidate output aggregate digest");
  requireSafeInteger(receipt.output.fileCount, 1, 20_000, "Candidate output file count");
  requireSafeInteger(receipt.output.bytes, 0, 8 * 1024 * 1024 * 1024, "Candidate output byte count");
  if (receipt.output.files.length !== receipt.output.fileCount) {
    throw new Error("Candidate output file count disagrees with its file evidence.");
  }
  let previous = "";
  let totalBytes = 0;
  for (const file of receipt.output.files) {
    if (typeof file.path !== "string" || !/^[^/\\\s:]+$/u.test(file.path) || file.path <= previous) {
      throw new Error("Candidate output file evidence is invalid or unsorted.");
    }
    requireDigest(file.digest, `Candidate output digest for ${file.path}`);
    requireSafeInteger(file.bytes, 0, 2 * 1024 * 1024 * 1024, `Candidate output size for ${file.path}`);
    totalBytes += file.bytes;
    previous = file.path;
  }
  if (totalBytes !== receipt.output.bytes || buildOutputDigest(receipt.output.root, receipt.output.files) !== receipt.output.digest) {
    throw new Error("Candidate output aggregate evidence is invalid.");
  }
  return receipt;
}

async function verifyBuildReceiptSource(receipt, source) {
  if (!source.clean || receipt.source.commit !== source.revision || receipt.source.tree !== source.tree) {
    throw new Error("Candidate build receipt does not match the clean checked-out source.");
  }
  const [treeDigest, configurationDigest] = await Promise.all([
    gitTreeDigest(repositoryRoot, source.revision),
    sha256File(path.join(repositoryRoot, ".terrascale", "deploy.json")),
  ]);
  if (
    receipt.source.treeDigest !== treeDigest ||
    receipt.configurationDigest !== `sha256:${configurationDigest}`
  ) {
    throw new Error("Candidate build receipt tree or configuration evidence is stale.");
  }
}

function verifyBuildOutput(receipt, actual) {
  const described = receipt.output.files;
  if (
    actual.files.length !== described.length ||
    actual.files.some((file, index) =>
      described[index].path !== file.name ||
      described[index].digest !== `sha256:${file.sha256}` ||
      described[index].bytes !== file.bytes) ||
    receipt.output.bytes !== actual.bytes ||
    receipt.output.digest !== buildOutputDigest(receipt.output.root, described)
  ) {
    throw new Error("Candidate output bytes do not match the Deploy build receipt.");
  }
}

function verifyQualificationReceipt(context) {
  const {
    receipt,
    receiptFilename,
    tarball,
    packageJson,
    packedRouteManifest,
    packedLicense,
    source,
    buildReceipt,
    contract,
    sourceManifest,
    node,
    npmCli,
  } = context;
  requireExactKeys(receipt, [
    "schemaVersion",
    "mediaType",
    "status",
    "startedAt",
    "completedAt",
    "source",
    "release",
    "artifact",
    "boundaries",
    "contract",
    "runtimes",
    "dependencies",
    "license",
    "security",
    "command",
    "environment",
    "limitations",
    "redaction",
  ], "npm qualification receipt");
  if (
    receipt.schemaVersion !== npmReleasePolicy.qualificationSchemaVersion ||
    receipt.mediaType !== npmReleasePolicy.qualificationMediaType ||
    receipt.status !== "passed"
  ) {
    throw new Error("npm qualification receipt did not pass schema 2 qualification.");
  }
  const qualificationStarted = parseUtc(receipt.startedAt, "npm qualification start");
  const qualificationCompleted = parseUtc(receipt.completedAt, "npm qualification completion");
  const buildStarted = parseUtc(buildReceipt.startedAt, "Candidate build start");
  const buildCompleted = parseUtc(buildReceipt.completedAt, "Candidate build completion");
  if (
    qualificationCompleted < qualificationStarted ||
    qualificationStarted < buildStarted ||
    qualificationCompleted > buildCompleted
  ) {
    throw new Error("npm qualification receipt is stale or outside its candidate build interval.");
  }

  requireExactKeys(receipt.source, [
    "revision",
    "tree",
    "clean",
    "dirtyStatusSha256",
    "toolingExclusion",
  ], "npm qualification source");
  requireExactKeys(receipt.source.toolingExclusion, [
    "root",
    "classification",
    "trackedFiles",
  ], "npm qualification tooling exclusion");
  if (
    receipt.source.revision !== source.revision ||
    receipt.source.tree !== source.tree ||
    receipt.source.revision !== buildReceipt.source.commit ||
    receipt.source.tree !== buildReceipt.source.tree ||
    receipt.source.clean !== true ||
    receipt.source.dirtyStatusSha256 !== null ||
    receipt.source.toolingExclusion.root !== ".tools" ||
    receipt.source.toolingExclusion.classification !== "ignored-workflow-tooling" ||
    receipt.source.toolingExclusion.trackedFiles !== 0
  ) {
    throw new Error("npm qualification receipt source is dirty, stale, or mismatched.");
  }

  requireExactKeys(receipt.release, ["eligible", "nodeVersions"], "npm qualification release policy");
  requireExactKeys(receipt.release.nodeVersions, ["22", "24"], "npm qualification Node policy");
  if (
    receipt.release.eligible !== true ||
    receipt.release.nodeVersions["22"] !== npmReleasePolicy.nodeVersions[22] ||
    receipt.release.nodeVersions["24"] !== npmReleasePolicy.nodeVersions[24]
  ) {
    throw new Error("npm qualification receipt is diagnostic or uses the wrong Node release policy.");
  }

  requireExactKeys(receipt.artifact, [
    "name",
    "version",
    "filename",
    "sha256",
    "integrity",
    "size",
    "unpackedSize",
    "fileCount",
  ], "npm qualification artifact");
  if (
    packageJson.name !== npmReleasePolicy.packageName ||
    packageJson.version !== sourceManifest.version ||
    packageJson.private !== false ||
    receipt.artifact.name !== packageJson.name ||
    receipt.artifact.version !== packageJson.version ||
    receipt.artifact.filename !== tarball.name ||
    receiptFilename !== qualificationFilename(packageJson.version) ||
    tarball.name !== tarballFilename(packageJson.version) ||
    receipt.artifact.sha256 !== tarball.sha256 ||
    receipt.artifact.size !== tarball.bytes
  ) {
    throw new Error("npm qualification receipt tarball, package, or version evidence is mismatched.");
  }
  if (
    receipt.artifact.integrity !== `sha512-${sha512(tarball.bytesBuffer).toString("base64")}` ||
    !Number.isSafeInteger(receipt.artifact.unpackedSize) ||
    receipt.artifact.unpackedSize < 1 ||
    !Number.isSafeInteger(receipt.artifact.fileCount) ||
    receipt.artifact.fileCount < 1
  ) {
    throw new Error("npm qualification receipt package size or integrity evidence is invalid.");
  }

  requireExactKeys(receipt.contract, [
    "descriptorVersion",
    "descriptorSha256",
    "openApiSha256",
    "routeManifestSha256",
    "routeCount",
  ], "npm qualification contract");
  if (
    JSON.stringify(receipt.contract) !== JSON.stringify(contract) ||
    sha256(packedRouteManifest) !== contract.routeManifestSha256
  ) {
    throw new Error("npm qualification receipt contract evidence is stale or substituted.");
  }

  requireExactKeys(receipt.boundaries, [
    "exports",
    "browserEntrypointGraphs",
    "localSqliteExport",
    "betterAuthExport",
    "packedSecretScan",
  ], "npm qualification boundaries");
  requireExactKeys(receipt.boundaries.browserEntrypointGraphs, ["root", "admin"], "npm browser graph evidence");
  for (const graph of [receipt.boundaries.browserEntrypointGraphs.root, receipt.boundaries.browserEntrypointGraphs.admin]) {
    if (!Array.isArray(graph) || graph.length < 1 || graph.some((entry) => typeof entry !== "string")) {
      throw new Error("npm qualification browser graph evidence is invalid.");
    }
  }
  if (
    JSON.stringify(receipt.boundaries.exports) !== JSON.stringify(packageJson.exports) ||
    receipt.boundaries.localSqliteExport !== "node-only" ||
    receipt.boundaries.betterAuthExport !== "node-only" ||
    receipt.boundaries.packedSecretScan !== "passed"
  ) {
    throw new Error("npm qualification package-boundary evidence is invalid.");
  }

  if (!Array.isArray(receipt.runtimes) || receipt.runtimes.length !== 2) {
    throw new Error("npm qualification receipt must bind both supported Node runtimes.");
  }
  for (const [index, expectedMajor] of [22, 24].entries()) {
    const runtime = requireExactKeys(receipt.runtimes[index], [
      "major",
      "executable",
      "executableSha256",
      "version",
      "status",
    ], "npm qualification runtime");
    if (
      runtime.major !== expectedMajor ||
      runtime.version !== npmReleasePolicy.nodeVersions[expectedMajor] ||
      runtime.status !== "passed" ||
      typeof runtime.executable !== "string" ||
      !path.isAbsolute(runtime.executable)
    ) {
      throw new Error("npm qualification runtime identity is missing or unsupported.");
    }
    requireLowerHex(runtime.executableSha256, 64, "Qualified Node executable digest");
  }
  if (
    receipt.runtimes[1].version !== node.version ||
    receipt.runtimes[1].executableSha256 !== node.executableSha256
  ) {
    throw new Error("Promotion Node bytes differ from the qualified Node 24 runtime.");
  }

  requireExactKeys(receipt.dependencies, ["count", "packages"], "npm qualification dependencies");
  if (
    receipt.dependencies.count !== 0 ||
    Object.keys(requireExactKeys(receipt.dependencies.packages, [], "npm production dependency set")).length !== 0
  ) {
    throw new Error("npm qualification receipt contains unqualified production dependencies.");
  }
  requireExactKeys(receipt.license, ["declared", "fileSha256"], "npm qualification license");
  if (
    receipt.license.declared !== "Apache-2.0" ||
    receipt.license.fileSha256 !== sha256(packedLicense)
  ) {
    throw new Error("npm qualification license evidence is invalid.");
  }
  requireExactKeys(receipt.security, ["audit", "registry"], "npm qualification security");
  if (
    receipt.security.registry !== "https://registry.npmjs.org/" ||
    typeof receipt.security.audit !== "object" ||
    receipt.security.audit === null ||
    Array.isArray(receipt.security.audit) ||
    typeof receipt.security.audit.vulnerabilities !== "object" ||
    receipt.security.audit.vulnerabilities === null ||
    Object.values(receipt.security.audit.vulnerabilities).some((count) => count !== 0)
  ) {
    throw new Error("npm qualification security audit evidence is missing or failed.");
  }
  requireExactKeys(receipt.environment, ["platform", "architecture", "npm"], "npm qualification environment");
  requireExactKeys(receipt.environment.npm, [
    "version",
    "archiveIntegrity",
    "entrypoint",
    "entrypointSha256",
    "packageJsonSha256",
    "packageTreeSha256",
    "fileCount",
    "bytes",
  ], "qualified npm CLI");
  if (
    typeof receipt.environment.platform !== "string" ||
    typeof receipt.environment.architecture !== "string" ||
    receipt.environment.npm.version !== npmReleasePolicy.npm.version ||
    receipt.environment.npm.archiveIntegrity !== npmReleasePolicy.npm.archiveIntegrity ||
    receipt.environment.npm.entrypoint !== "bin/npm-cli.js" ||
    typeof receipt.command !== "string" ||
    !receipt.command.includes("--release") ||
    receipt.command.includes("--allow-dirty") ||
    receipt.command.length > 4_096
  ) {
    throw new Error("npm qualification command or environment evidence is invalid.");
  }
  for (const [field, name] of [
    ["entrypointSha256", "npm CLI entrypoint digest"],
    ["packageJsonSha256", "npm CLI package manifest digest"],
    ["packageTreeSha256", "npm CLI package tree digest"],
  ]) {
    requireLowerHex(receipt.environment.npm[field], 64, name);
  }
  requireSafeInteger(receipt.environment.npm.fileCount, 1, 20_000, "npm CLI package file count");
  requireSafeInteger(receipt.environment.npm.bytes, 1, 2 * 1024 * 1024 * 1024, "npm CLI package byte count");
  if (JSON.stringify(receipt.environment.npm) !== JSON.stringify(npmCli.evidence)) {
    throw new Error("Promotion npm CLI bytes differ from the qualification toolchain.");
  }
  if (
    !Array.isArray(receipt.limitations) ||
    receipt.limitations.length !== 0 ||
    receipt.redaction !== "No environment values or package contents are embedded; dirty paths and workflow tooling are represented only by bounded classification evidence."
  ) {
    throw new Error("npm qualification receipt has limitations or invalid redaction evidence.");
  }
}

async function snapshotCandidateOutput(directory) {
  const resolvedRoot = path.resolve(directory);
  const realRoot = await realpath(resolvedRoot);
  const rootMetadata = await lstat(resolvedRoot);
  if (realRoot !== resolvedRoot || !rootMetadata.isDirectory() || rootMetadata.isSymbolicLink()) {
    throw new Error("Candidate artifacts root must be a real directory without symlinks.");
  }
  const entries = await readdir(resolvedRoot, { withFileTypes: true });
  const files = [];
  for (const entry of entries.sort((left, right) => left.name.localeCompare(right.name, "en"))) {
    if (!entry.isFile() || entry.isSymbolicLink() || !/^[^/\\\s:]+$/u.test(entry.name)) {
      throw new Error(`Candidate output contains a non-regular or unsafe path: ${entry.name}`);
    }
    const filePath = path.join(resolvedRoot, entry.name);
    if (await realpath(filePath) !== filePath) throw new Error(`Candidate output contains a symlinked path: ${entry.name}`);
    const [metadata, bytesBuffer] = await Promise.all([stat(filePath), readFile(filePath)]);
    files.push({
      name: entry.name,
      path: filePath,
      bytes: metadata.size,
      bytesBuffer,
      sha256: sha256(bytesBuffer),
    });
  }
  return { files, bytes: files.reduce((total, file) => total + file.bytes, 0) };
}

async function verifiedStandaloneFile(filePath, name) {
  const resolved = path.resolve(filePath);
  const [real, metadata] = await Promise.all([realpath(resolved), lstat(resolved)]);
  if (real !== resolved || !metadata.isFile() || metadata.isSymbolicLink()) {
    throw new Error(`${name} must be a regular non-symlink file.`);
  }
  return resolved;
}

function buildOutputDigest(root, files) {
  const material = {
    domain: npmReleasePolicy.buildOutputDigestDomain,
    root,
    files,
  };
  return `sha256:${sha256(Buffer.from(JSON.stringify(material)))}`;
}

async function readTarEntry(tarball, entry) {
  try {
    const result = await execute("tar", ["-xOf", tarball, entry], {
      encoding: "buffer",
      maxBuffer: 16 * 1024 * 1024,
    });
    if (result.stdout.length < 1) throw new Error("empty entry");
    return result.stdout;
  } catch (error) {
    throw new Error(`Candidate tarball is missing or has malformed ${entry}.`, { cause: error });
  }
}

async function publishCandidate(candidate, registry, token) {
  if (
    token === undefined ||
    token.length === 0 ||
    token.length > 4_096 ||
    !/^[\x21-\x7e]+$/u.test(token)
  ) {
    throw new Error("NPM_TOKEN is required to promote the verified candidate.");
  }

  const configDirectory = await mkdtemp(path.join(tmpdir(), "terrascale-npm-publish-"));
  const configPath = path.join(configDirectory, "npmrc");
  const globalConfigPath = path.join(configDirectory, "global-npmrc");
  try {
    await Promise.all([
      writeFile(configPath, `//registry.npmjs.org/:_authToken=${token}\nignore-scripts=true\n`, { mode: 0o600 }),
      writeFile(globalConfigPath, "", { mode: 0o600 }),
    ]);
    await execute(process.execPath, [candidate.npmCli.entrypoint, "publish", candidate.tarball, "--access", "public", "--ignore-scripts", "--registry", registry], {
      cwd: configDirectory,
      env: npmEnvironment({
        NPM_CONFIG_USERCONFIG: configPath,
        NPM_CONFIG_GLOBALCONFIG: globalConfigPath,
        NPM_CONFIG_IGNORE_SCRIPTS: "true",
      }),
    });
    console.log(`Published ${candidate.name}@${candidate.version} from the verified candidate tarball.`);
  } finally {
    await rm(configDirectory, { recursive: true, force: true });
  }
}

async function ensureVersionAvailable(name, version, registry, npmCli) {
  if (name !== npmReleasePolicy.packageName || typeof version !== "string" || version.length === 0) {
    throw new Error("Only a nonblank @terrascale/sdk version can be checked for promotion.");
  }
  requirePublicRegistry(registry);
  const configDirectory = await mkdtemp(path.join(tmpdir(), "terrascale-npm-view-"));
  const userConfigPath = path.join(configDirectory, "npmrc");
  const globalConfigPath = path.join(configDirectory, "global-npmrc");
  try {
    await Promise.all([
      writeFile(userConfigPath, "", { mode: 0o600 }),
      writeFile(globalConfigPath, "", { mode: 0o600 }),
    ]);
    try {
      await execute(process.execPath, [npmCli.entrypoint, "view", `${name}@${version}`, "version", "--json", "--ignore-scripts", "--registry", registry], {
        cwd: configDirectory,
        env: npmEnvironment({
          NPM_CONFIG_USERCONFIG: userConfigPath,
          NPM_CONFIG_GLOBALCONFIG: globalConfigPath,
          NPM_CONFIG_IGNORE_SCRIPTS: "true",
        }),
      });
    } catch (error) {
      if (error !== null && typeof error === "object" && "stderr" in error && /E404/u.test(String(error.stderr))) {
        console.log(`${name}@${version} is available on the public registry.`);
        return;
      }
      throw new Error(`Could not confirm public registry availability for ${name}@${version}.`, { cause: error });
    }
  } finally {
    await rm(configDirectory, { recursive: true, force: true });
  }
  throw new Error(`${name}@${version} already exists on the public registry.`);
}

async function inspectPromotionToolchain() {
  if (process.version !== npmReleasePolicy.nodeVersions[24]) {
    throw new Error(`Promotion requires Node ${npmReleasePolicy.nodeVersions[24]}.`);
  }
  if (process.env.TERRASCALE_NPM_ARCHIVE_INTEGRITY !== npmReleasePolicy.npm.archiveIntegrity) {
    throw new Error("Promotion requires the reviewed npm archive integrity environment binding.");
  }
  const [nodeExecutable, npmCli] = await Promise.all([
    realpath(process.execPath),
    inspectNpmCli({ archiveIntegrity: npmReleasePolicy.npm.archiveIntegrity }),
  ]);
  const nodeMetadata = await lstat(nodeExecutable);
  if (!nodeMetadata.isFile() || nodeMetadata.isSymbolicLink()) {
    throw new Error("Promotion Node executable must resolve to a regular file.");
  }
  if (npmCli.evidence.version !== npmReleasePolicy.npm.version) {
    throw new Error(`Promotion requires npm ${npmReleasePolicy.npm.version}.`);
  }
  return {
    node: {
      version: process.version,
      executableSha256: await sha256File(nodeExecutable),
    },
    npmCli,
  };
}

function requirePublicRegistry(registry) {
  if (registry !== "https://registry.npmjs.org/") {
    throw new Error("The SDK promotion registry must be https://registry.npmjs.org/.");
  }
}

function requireDigest(value, name) {
  if (typeof value !== "string" || !/^sha256:[0-9a-f]{64}$/u.test(value)) {
    throw new Error(`${name} must use sha256 and 64 lowercase hexadecimal characters.`);
  }
}

function requireLowerHex(value, length, name) {
  if (typeof value !== "string" || value.length !== length || !/^[0-9a-f]+$/u.test(value)) {
    throw new Error(`${name} must contain ${length} lowercase hexadecimal characters.`);
  }
}

function requireSafeInteger(value, minimum, maximum, name) {
  if (!Number.isSafeInteger(value) || value < minimum || value > maximum) {
    throw new Error(`${name} is outside its safe bound.`);
  }
}

function parseUtc(value, name) {
  if (
    typeof value !== "string" ||
    !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/u.test(value)
  ) {
    throw new Error(`${name} must be an exact UTC timestamp.`);
  }
  const parsed = Date.parse(value);
  if (!Number.isFinite(parsed)) throw new Error(`${name} is invalid.`);
  return parsed;
}

function npmEnvironment(overrides) {
  const permitted = ["COMSPEC", "HOME", "LANG", "LC_ALL", "PATH", "SystemRoot", "TEMP", "TMP", "TMPDIR", "WINDIR"];
  const environment = Object.fromEntries(
    permitted.flatMap((key) => process.env[key] === undefined ? [] : [[key, process.env[key]]]),
  );
  return { ...environment, ...overrides };
}

async function sha256File(filePath) {
  return sha256(await readFile(filePath));
}

function sha256(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}

function sha512(bytes) {
  return createHash("sha512").update(bytes).digest();
}
