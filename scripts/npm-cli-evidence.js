import { execFile } from "node:child_process";
import { constants } from "node:fs";
import { access, lstat, readFile, readdir, realpath } from "node:fs/promises";
import path from "node:path";
import { promisify } from "node:util";
import { createHash } from "node:crypto";
import { npmReleasePolicy } from "./npm-release-policy.js";
import { parseStrictJson } from "./strict-json.js";

const execute = promisify(execFile);

/**
 * @typedef {object} NpmCliEvidence
 * @property {string} version
 * @property {string | null} archiveIntegrity
 * @property {string} entrypoint
 * @property {string} entrypointSha256
 * @property {string} packageJsonSha256
 * @property {string} packageTreeSha256
 * @property {number} fileCount
 * @property {number} bytes
 */

/**
 * @typedef {object} NpmCli
 * @property {string} entrypoint Absolute path of the npm-cli.js executable on PATH.
 * @property {NpmCliEvidence} evidence
 */

/**
 * @param {{ archiveIntegrity?: string | null }} [options]
 * @returns {Promise<NpmCli>}
 */
export async function inspectNpmCli({ archiveIntegrity = null } = {}) {
  const entrypoint = await npmEntrypointOnPath();
  const packageRoot = path.resolve(path.dirname(entrypoint), "..");
  const packageJsonPath = path.join(packageRoot, "package.json");
  const packageJsonBytes = await readFile(packageJsonPath);
  const manifest = parseStrictJson(packageJsonBytes, "npm CLI package manifest");
  if (manifest.name !== "npm" || manifest.bin?.npm !== "bin/npm-cli.js") {
    throw new Error("npm CLI package identity does not match the executable on PATH.");
  }
  const expectedEntrypoint = path.join(packageRoot, manifest.bin.npm);
  if (entrypoint !== expectedEntrypoint) {
    throw new Error("npm on PATH does not resolve to its declared npm-cli.js entrypoint.");
  }
  const reportedVersion = (await execute(process.execPath, [entrypoint, "--version"], {
    maxBuffer: 1024 * 1024,
  })).stdout.trim();
  if (manifest.version !== reportedVersion) {
    throw new Error("npm CLI package version does not match its executable.");
  }

  /** @type {{ path: string, sha256: string, bytes: number }[]} */
  const files = [];
  let bytes = 0;
  for (const relative of await listRegularFiles(packageRoot)) {
    const contents = await readFile(path.join(packageRoot, relative));
    bytes += contents.length;
    files.push({ path: relative, sha256: sha256(contents), bytes: contents.length });
  }
  return {
    entrypoint,
    evidence: {
      version: reportedVersion,
      archiveIntegrity,
      entrypoint: manifest.bin.npm,
      entrypointSha256: sha256(await readFile(entrypoint)),
      packageJsonSha256: sha256(packageJsonBytes),
      packageTreeSha256: sha256(Buffer.from(JSON.stringify({
        domain: npmReleasePolicy.npmTreeDigestDomain,
        files,
      }))),
      fileCount: files.length,
      bytes,
    },
  };
}

/** @returns {Promise<string>} */
async function npmEntrypointOnPath() {
  for (const directory of (process.env.PATH ?? "").split(path.delimiter)) {
    if (directory === "") continue;
    const candidate = path.resolve(directory, process.platform === "win32" ? "npm.cmd" : "npm");
    try {
      await access(candidate, constants.X_OK);
      const resolved = await realpath(candidate);
      const metadata = await lstat(resolved);
      if (metadata.isFile() && !metadata.isSymbolicLink()) return resolved;
    } catch (error) {
      if (error !== null && typeof error === "object" && "code" in error &&
          (error.code === "ENOENT" || error.code === "EACCES")) continue;
      throw error;
    }
  }
  throw new Error("npm CLI executable is absent from PATH.");
}

/**
 * @param {string} directory
 * @param {string} [prefix]
 * @returns {Promise<string[]>}
 */
async function listRegularFiles(directory, prefix = "") {
  /** @type {string[]} */
  const result = [];
  for (const entry of await readdir(path.join(directory, prefix), { withFileTypes: true })) {
    const relative = path.posix.join(prefix, entry.name);
    if (entry.isDirectory()) result.push(...await listRegularFiles(directory, relative));
    else if (entry.isFile()) result.push(relative);
    else throw new Error(`npm CLI package contains a non-regular path: ${relative}`);
  }
  return result.sort();
}

/**
 * @param {Uint8Array} bytes
 * @returns {string}
 */
function sha256(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}
