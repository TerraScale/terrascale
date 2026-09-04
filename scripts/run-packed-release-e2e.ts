#!/usr/bin/env node

import { execFile } from "node:child_process";
import { copyFile, lstat, mkdtemp, readFile, realpath, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import "./require-release-e2e-config.ts";

const execute = promisify(execFile);
const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const tarballArgument = parseArguments(process.argv.slice(2));
const tarball = path.resolve(tarballArgument);
const [tarballRealPath, metadata] = await Promise.all([realpath(tarball), lstat(tarball)]);
if (
  tarballRealPath !== tarball ||
  !metadata.isFile() ||
  metadata.isSymbolicLink() ||
  !tarball.endsWith(".tgz")
) {
  throw new Error("Packed release E2E requires a regular non-symlink npm tarball.");
}

const consumer = await mkdtemp(path.join(tmpdir(), "terrascale-packed-release-e2e-"));
try {
  await writeFile(path.join(consumer, "package.json"), `${JSON.stringify({ private: true, type: "module" })}\n`);
  await execute("npm", [
    "install",
    "--ignore-scripts",
    "--no-audit",
    "--no-fund",
    tarball,
  ], {
    cwd: consumer,
    env: npmEnvironment(),
    maxBuffer: 16 * 1024 * 1024,
  });
  const installedManifest = JSON.parse(await readFile(
    path.join(consumer, "node_modules", "@terrascale", "sdk", "package.json"),
    "utf8",
  ));
  if (installedManifest.name !== "@terrascale/sdk") {
    throw new Error("Packed release E2E did not install @terrascale/sdk.");
  }
  const journey = path.join(consumer, "release-e2e.ts");
  await copyFile(path.join(scriptDirectory, "packed-release-e2e-journey.ts"), journey);
  await execute(process.execPath, [journey], {
    cwd: consumer,
    env: process.env,
    maxBuffer: 16 * 1024 * 1024,
  });
  console.log(`Packed live E2E passed for @terrascale/sdk@${installedManifest.version}.`);
} finally {
  await rm(consumer, { recursive: true, force: true });
}

function parseArguments(arguments_) {
  if (arguments_.length !== 2 || arguments_[0] !== "--tarball" || arguments_[1].startsWith("--")) {
    throw new Error("Usage: node scripts/run-packed-release-e2e.ts --tarball <candidate.tgz>");
  }
  return arguments_[1];
}

function npmEnvironment() {
  const permitted = ["COMSPEC", "HOME", "LANG", "LC_ALL", "PATH", "SystemRoot", "TEMP", "TMP", "TMPDIR", "WINDIR"];
  return Object.fromEntries(
    permitted.flatMap((key) => process.env[key] === undefined ? [] : [[key, process.env[key]]]),
  );
}
