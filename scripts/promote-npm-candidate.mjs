#!/usr/bin/env node

import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import { lstat, mkdtemp, readFile, realpath, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { promisify } from "node:util";

const run = promisify(execFile);
const options = parseOptions(process.argv.slice(2));

if (options.checkAvailability) {
  if (options.package === undefined || options.version === undefined || options.registry === undefined) {
    console.error("Usage: node scripts/promote-npm-candidate.mjs --check-availability --package <name> --version <version> --registry <url>");
    process.exitCode = 2;
  } else {
    await ensureVersionAvailable(options.package, options.version, options.registry);
  }
} else if (options.manifest === undefined || options.artifacts === undefined || options.registry === undefined) {
  console.error("Usage: node scripts/promote-npm-candidate.mjs --manifest <path> --artifacts <path> --registry <url> [--verify-only]");
  process.exitCode = 2;
} else {
  const candidate = await verifyCandidate(options);

  if (options.verifyOnly) {
    console.log(`Verified ${candidate.name}@${candidate.version} candidate ${candidate.tarball}.`);
  } else {
    await ensureVersionAvailable(candidate.name, candidate.version, options.registry);
    await publishCandidate(candidate, options.registry);
  }
}

function parseOptions(arguments_) {
  const options = {};
  for (let index = 0; index < arguments_.length; index += 1) {
    const argument = arguments_[index];
    if (argument === "--verify-only") {
      options.verifyOnly = true;
      continue;
    }
    if (argument === "--check-availability") {
      options.checkAvailability = true;
      continue;
    }
    if (!argument.startsWith("--") || arguments_[index + 1] === undefined) {
      throw new Error(`Invalid argument: ${argument}`);
    }
    options[argument.slice(2).replace(/-([a-z])/gu, (_, letter) => letter.toUpperCase())] = arguments_[index + 1];
    index += 1;
  }
  return options;
}

async function verifyCandidate(options) {
  if (options.registry !== "https://registry.npmjs.org/") {
    throw new Error("The SDK promotion registry must be https://registry.npmjs.org/.");
  }

  const manifest = JSON.parse(await readFile(options.manifest, "utf8"));
  if (typeof manifest.reservedVersion !== "string" || manifest.reservedVersion.length === 0) {
    throw new Error("Candidate manifest has no reserved package version.");
  }
  if (!Array.isArray(manifest.artifacts)) {
    throw new Error("Candidate manifest has no artifacts.");
  }
  const packageArtifacts = manifest.artifacts.filter((artifact) =>
    typeof artifact?.name === "string" && artifact.name.endsWith(".tgz"),
  );
  if (packageArtifacts.length !== 1) {
    throw new Error("Candidate manifest must contain exactly one npm tarball.");
  }

  const artifact = packageArtifacts[0];
  const tarball = await verifiedArtifactPath(options.artifacts, artifact.name);
  const digest = await sha256(tarball);
  if (digest !== manifestSha256(artifact.sha256)) {
    throw new Error("Candidate tarball digest does not match the tested manifest.");
  }
  const packageJson = JSON.parse((await run("tar", ["-xOf", tarball, "package/package.json"])).stdout);
  if (packageJson.name !== "@terrascale/sdk" || packageJson.version !== manifest.reservedVersion) {
    throw new Error("Candidate tarball name or version does not match the tested manifest.");
  }

  return { name: packageJson.name, version: packageJson.version, tarball };
}

async function publishCandidate(candidate, registry) {
  const token = process.env.NPM_TOKEN;
  if (token === undefined || token.length === 0) {
    throw new Error("NPM_TOKEN is required to promote the verified candidate.");
  }

  const configDirectory = await mkdtemp(path.join(tmpdir(), "terrascale-npm-publish-"));
  const configPath = path.join(configDirectory, "npmrc");
  const globalConfigPath = path.join(configDirectory, "global-npmrc");
  try {
    await Promise.all([
      writeFile(configPath, `//registry.npmjs.org/:_authToken=${token}\nignore-scripts=true\n`),
      writeFile(globalConfigPath, ""),
    ]);
    await run("npm", ["publish", candidate.tarball, "--access", "public", "--ignore-scripts", "--registry", registry], {
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

async function ensureVersionAvailable(name, version, registry) {
  if (name !== "@terrascale/sdk" || typeof version !== "string" || version.length === 0) {
    throw new Error("Only a nonblank @terrascale/sdk version can be checked for promotion.");
  }
  if (registry !== "https://registry.npmjs.org/") {
    throw new Error("The SDK promotion registry must be https://registry.npmjs.org/.");
  }
  const configDirectory = await mkdtemp(path.join(tmpdir(), "terrascale-npm-view-"));
  const userConfigPath = path.join(configDirectory, "npmrc");
  const globalConfigPath = path.join(configDirectory, "global-npmrc");
  try {
    await Promise.all([writeFile(userConfigPath, ""), writeFile(globalConfigPath, "")]);
    try {
      await run("npm", ["view", `${name}@${version}`, "version", "--json", "--ignore-scripts", "--registry", registry], {
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

async function verifiedArtifactPath(artifactsDirectory, artifactName) {
  if (typeof artifactName !== "string" || !/^[^/\\]+\.tgz$/u.test(artifactName)) {
    throw new Error("Candidate tarball name must be a simple .tgz basename.");
  }
  const root = await realpath(artifactsDirectory);
  const tarball = path.resolve(root, artifactName);
  if (path.dirname(tarball) !== root) {
    throw new Error("Candidate tarball resolves outside the artifacts directory.");
  }
  const metadata = await lstat(tarball);
  if (!metadata.isFile() || metadata.isSymbolicLink()) {
    throw new Error("Candidate tarball must be a regular non-symlink file.");
  }
  return tarball;
}

function manifestSha256(value) {
  const match = typeof value === "string" ? /^sha256:([0-9a-f]{64})$/iu.exec(value) : null;
  if (match === null) {
    throw new Error("Candidate manifest digest must use sha256:<64 hexadecimal characters>.");
  }
  return match[1].toLowerCase();
}

function npmEnvironment(overrides) {
  const permitted = ["COMSPEC", "HOME", "LANG", "LC_ALL", "PATH", "SystemRoot", "TEMP", "TMP", "TMPDIR", "WINDIR"];
  const environment = Object.fromEntries(
    permitted.flatMap((key) => process.env[key] === undefined ? [] : [[key, process.env[key]]]),
  );
  return { ...environment, ...overrides };
}

async function sha256(filePath) {
  return createHash("sha256").update(await readFile(filePath)).digest("hex");
}
