#!/usr/bin/env node

import { execFile } from "node:child_process";
import { copyFile, lstat, mkdtemp, readFile, realpath, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { npmReleasePolicy } from "./npm-release-policy.js";
import "./require-release-e2e-config.js";

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
    path.join(consumer, "node_modules", ...npmReleasePolicy.packageName.split("/"), "package.json"),
    "utf8",
  ));
  if (installedManifest.name !== npmReleasePolicy.packageName) {
    throw new Error(`Packed release E2E did not install ${npmReleasePolicy.packageName}.`);
  }
  const journey = path.join(consumer, "release-e2e.js");
  await copyFile(path.join(scriptDirectory, "packed-release-e2e-journey.js"), journey);
  await execute(process.execPath, [journey], {
    cwd: consumer,
    env: process.env,
    maxBuffer: 16 * 1024 * 1024,
  });
  console.log(`Packed live E2E passed for ${npmReleasePolicy.packageName}@${installedManifest.version}.`);
} finally {
  await rm(consumer, { recursive: true, force: true });
}

/**
 * @param {string[]} arguments_
 * @returns {string}
 */
function parseArguments(arguments_) {
  const tarball = arguments_[1];
  if (arguments_.length !== 2 || arguments_[0] !== "--tarball" || tarball === undefined || tarball.startsWith("--")) {
    throw new Error("Usage: node scripts/run-packed-release-e2e.js --tarball <candidate.tgz>");
  }
  return tarball;
}

/** @returns {NodeJS.ProcessEnv} */
function npmEnvironment() {
  const permitted = ["COMSPEC", "HOME", "LANG", "LC_ALL", "PATH", "SystemRoot", "TEMP", "TMP", "TMPDIR", "WINDIR"];
  return Object.fromEntries(
    permitted.flatMap((key) => process.env[key] === undefined ? [] : [[key, process.env[key]]]),
  );
}
