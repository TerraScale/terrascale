#!/usr/bin/env node

import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { lstat, open } from "node:fs/promises";
import path from "node:path";
import { TextDecoder } from "node:util";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const allowlistPath = "docs/rename/legacy-name-allowlist.tsv";
const maxFileBytes = 8 * 1024 * 1024;
const nameParts = ["terra", "base", "v2"];
const separator = String.raw`[\s._-]*`;
const legacyNameSource = nameParts.join(separator);
const legacyName = new RegExp(legacyNameSource, "iu");
const utf8 = new TextDecoder("utf-8", { fatal: true });
const scanMode = parseArguments(process.argv.slice(2));
const nestedRepositoryCache = new Map();

verifyPatternCoverage();

const trackedPaths = readGitPaths(["--cached"]);
const untrackedPaths = scanMode.includeUntracked
  ? new Set([...readGitPaths(["--others", "--exclude-standard"])].filter((entry) => !trackedPaths.has(entry)))
  : new Set();
const repositoryPaths = new Set([...trackedPaths, ...untrackedPaths]);
const allowlist = await readAllowlist();
const seenAllowances = new Set();
const skippedNestedRepositories = new Set();
const violations = [];

for (const relativePath of [...repositoryPaths].sort()) {
  const pathKind = trackedPaths.has(relativePath) ? "tracked" : "untracked";
  const pathMatches = collectMatches(relativePath, "path", 0);
  const fileScan = await scanTextFile(relativePath);
  if (fileScan.nestedRepository !== null) {
    skippedNestedRepositories.add(fileScan.nestedRepository);
  }

  const matches = [...pathMatches, ...fileScan.matches];
  const allowance = allowlist.get(relativePath);
  if (allowance !== undefined) {
    seenAllowances.add(relativePath);
    const actualFingerprint = fingerprintMatches(relativePath, matches);
    if (pathKind !== "tracked") {
      violations.push(`${allowlistPath} [allowance must name a tracked file: ${relativePath}]`);
    } else if (fileScan.issue !== null) {
      violations.push(`${relativePath} [allowance cannot hide ${fileScan.issue}]`);
    } else if (matches.length !== allowance.expectedMatches || actualFingerprint !== allowance.fingerprint) {
      violations.push(
        `${allowlistPath} [allowance mismatch for ${relativePath}: expected ${allowance.expectedMatches} matches ` +
        `and ${allowance.fingerprint}, found ${matches.length} and ${actualFingerprint}]`,
      );
    }
  } else {
    for (const match of matches) {
      violations.push(formatMatch(relativePath, pathKind, match));
    }
    if (fileScan.issue !== null) {
      violations.push(`${relativePath} [${pathKind} ${fileScan.issue}]`);
    }
  }
}

for (const relativePath of allowlist.keys()) {
  if (!trackedPaths.has(relativePath)) {
    violations.push(`${allowlistPath} [allowance names an untracked or missing path: ${relativePath}]`);
  } else if (!seenAllowances.has(relativePath)) {
    violations.push(`${allowlistPath} [allowance was not evaluated: ${relativePath}]`);
  }
}

for (const nestedRepository of [...skippedNestedRepositories].sort()) {
  console.log(`Skipped nested repository: ${nestedRepository}`);
}

if (violations.length > 0) {
  console.error("Retired product-name variants detected:");
  for (const violation of [...new Set(violations)].sort()) {
    console.error(`  - ${violation}`);
  }
  process.exitCode = 1;
} else {
  const scope = scanMode.includeUntracked ? "tracked and non-ignored files" : "tracked files only";
  console.log(`Product-name migration check passed (${scope}).`);
}

function parseArguments(arguments_) {
  if (arguments_.length === 0) return { includeUntracked: true };
  if (arguments_.length === 1 && arguments_[0] === "--tracked-only") {
    return { includeUntracked: false };
  }
  throw new Error("usage: node scripts/check-product-name.mjs [--tracked-only]");
}

function readGitPaths(arguments_) {
  const result = spawnSync("git", ["ls-files", "-z", ...arguments_], {
    cwd: repositoryRoot,
    encoding: "buffer",
    maxBuffer: 32 * 1024 * 1024,
  });
  if (result.status !== 0) {
    throw new Error(`git ls-files failed: ${result.stderr.toString("utf8")}`);
  }
  return new Set(result.stdout.toString("utf8").split("\0").filter(Boolean));
}

async function readAllowlist() {
  const result = await readBoundedFile(path.join(repositoryRoot, allowlistPath));
  if (result.status === "missing") {
    throw new Error(`${allowlistPath} is missing`);
  }
  if (result.status === "too-large") {
    throw new Error(`${allowlistPath} exceeds the ${maxFileBytes}-byte scan limit`);
  }

  let text;
  try {
    text = utf8.decode(result.bytes);
  } catch {
    throw new Error(`${allowlistPath} is not valid UTF-8`);
  }

  const entries = new Map();
  for (const [index, line] of text.split(/\r?\n/u).entries()) {
    if (line.length === 0 || line.startsWith("#")) continue;
    const fields = line.split("\t");
    if (fields.length !== 4 || fields.some((field) => field.trim().length === 0)) {
      throw new Error(
        `${allowlistPath}:${index + 1} must contain path, expected match count, match fingerprint, and reason`,
      );
    }

    const expectedMatches = Number(fields[1]);
    if (!Number.isSafeInteger(expectedMatches) || expectedMatches < 1) {
      throw new Error(`${allowlistPath}:${index + 1} has an invalid expected match count`);
    }
    if (!/^[0-9a-f]{64}$/u.test(fields[2])) {
      throw new Error(`${allowlistPath}:${index + 1} has an invalid SHA-256 match fingerprint`);
    }
    if (entries.has(fields[0])) {
      throw new Error(`${allowlistPath}:${index + 1} repeats ${fields[0]}`);
    }
    entries.set(fields[0], {
      expectedMatches,
      fingerprint: fields[2],
      reason: fields[3],
    });
  }
  return entries;
}

async function scanTextFile(relativePath) {
  const nestedRepository = await findNestedRepository(path.dirname(relativePath));
  if (nestedRepository !== null) {
    return { matches: [], issue: null, nestedRepository };
  }

  const absolutePath = path.join(repositoryRoot, relativePath);
  let metadata;
  try {
    metadata = await lstat(absolutePath);
  } catch (error) {
    if (error?.code === "ENOENT") {
      return { matches: [], issue: null, nestedRepository: null };
    }
    throw error;
  }

  if (metadata.isDirectory()) {
    return { matches: [], issue: null, nestedRepository: null };
  }
  if (metadata.isSymbolicLink()) {
    return { matches: [], issue: "symbolic link requires explicit review", nestedRepository: null };
  }
  if (!metadata.isFile()) {
    return { matches: [], issue: "special file requires explicit review", nestedRepository: null };
  }

  const result = await readBoundedFile(absolutePath);
  if (result.status === "missing") {
    return { matches: [], issue: null, nestedRepository: null };
  }
  if (result.status === "too-large") {
    return {
      matches: [],
      issue: `file exceeds the ${maxFileBytes}-byte scan limit`,
      nestedRepository: null,
    };
  }
  if (result.bytes.includes(0)) {
    return { matches: [], issue: null, nestedRepository: null };
  }

  let text;
  try {
    text = utf8.decode(result.bytes);
  } catch {
    return { matches: [], issue: null, nestedRepository: null };
  }

  const matches = [];
  for (const [index, line] of text.split(/\r?\n/u).entries()) {
    matches.push(...collectMatches(line, "text", index + 1));
  }
  return { matches, issue: null, nestedRepository: null };
}

async function readBoundedFile(absolutePath) {
  let handle;
  try {
    handle = await open(absolutePath, "r");
  } catch (error) {
    if (error?.code === "ENOENT") return { status: "missing" };
    throw error;
  }

  try {
    const buffer = Buffer.allocUnsafe(maxFileBytes + 1);
    let offset = 0;
    while (offset < buffer.length) {
      const { bytesRead } = await handle.read(buffer, offset, buffer.length - offset, null);
      if (bytesRead === 0) break;
      offset += bytesRead;
    }
    if (offset > maxFileBytes) return { status: "too-large" };
    return { status: "ok", bytes: buffer.subarray(0, offset) };
  } finally {
    await handle.close();
  }
}

async function findNestedRepository(directory) {
  if (directory === "." || directory.length === 0) return null;
  if (nestedRepositoryCache.has(directory)) return nestedRepositoryCache.get(directory);

  const marker = path.join(repositoryRoot, directory, ".git");
  let markerExists = false;
  try {
    await lstat(marker);
    markerExists = true;
  } catch (error) {
    if (error?.code !== "ENOENT") throw error;
  }

  const result = markerExists
    ? directory
    : await findNestedRepository(path.dirname(directory));
  nestedRepositoryCache.set(directory, result);
  return result;
}

function collectMatches(value, kind, line) {
  const matches = [];
  for (const match of value.matchAll(new RegExp(legacyNameSource, "giu"))) {
    matches.push({
      kind,
      line,
      column: (match.index ?? 0) + 1,
      value: match[0],
    });
  }
  return matches;
}

function fingerprintMatches(relativePath, matches) {
  const digest = createHash("sha256");
  digest.update("terrascale.product-name-allowance/v1\0", "utf8");
  digest.update(relativePath, "utf8");
  digest.update("\0", "utf8");
  for (const match of matches) {
    digest.update(match.kind, "utf8");
    digest.update("\0", "utf8");
    digest.update(String(match.line), "utf8");
    digest.update("\0", "utf8");
    digest.update(String(match.column), "utf8");
    digest.update("\0", "utf8");
    digest.update(match.value, "utf8");
    digest.update("\0", "utf8");
  }
  return digest.digest("hex");
}

function formatMatch(relativePath, pathKind, match) {
  if (match.kind === "path") {
    return `${relativePath} [${pathKind} path at column ${match.column}]`;
  }
  return `${relativePath}:${match.line}:${match.column} [${pathKind} UTF-8 text]`;
}

function verifyPatternCoverage() {
  for (const joiner of ["", "-", "_", " ", "."]) {
    const candidate = nameParts.join(joiner);
    if (!legacyName.test(candidate) || !legacyName.test(candidate.toUpperCase())) {
      throw new Error(`retired-name detector does not cover separator ${JSON.stringify(joiner)}`);
    }
  }
  if (legacyName.test(nameParts.slice(0, 2).join(""))) {
    throw new Error("retired-name detector rejects the canonical product name");
  }
}
