#!/usr/bin/env node

import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { promisify } from "node:util";

const run = promisify(execFile);
const options = parseOptions(process.argv.slice(2));

if (options.manifest === undefined || options.artifacts === undefined || options.registry === undefined) {
  console.error("Usage: node scripts/promote-npm-candidate.mjs --manifest <path> --artifacts <path> --registry <url> [--verify-only]");
  process.exitCode = 2;
} else {
  const candidate = await verifyCandidate(options);

  if (options.verifyOnly) {
    console.log(`Verified ${candidate.name}@${candidate.version} candidate ${candidate.tarball}.`);
  } else {
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
  const tarball = path.resolve(options.artifacts, artifact.name);
  const digest = await sha256(tarball);
  if (digest !== artifact.sha256) {
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
  try {
    await writeFile(configPath, `//registry.npmjs.org/:_authToken=${token}\n`);
    await run("npm", ["publish", candidate.tarball, "--access", "public", "--registry", registry], {
      env: { ...process.env, NPM_CONFIG_USERCONFIG: configPath },
    });
    console.log(`Published ${candidate.name}@${candidate.version} from the verified candidate tarball.`);
  } finally {
    await rm(configDirectory, { recursive: true, force: true });
  }
}

async function sha256(filePath) {
  return createHash("sha256").update(await readFile(filePath)).digest("hex");
}
