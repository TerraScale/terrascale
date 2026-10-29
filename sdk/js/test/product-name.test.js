// Retired product-name gate (Vitest port of the retired check-product-name gate,
// D-47). Tracked files must be free of retired TerraBase-style name
// variants except for the fingerprinted allowlist entries in
// docs/rename/legacy-name-allowlist.tsv. The fingerprint binding means an
// edited allowlisted file fails until its allowance is re-reviewed.

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { lstat, open, readFile } from "node:fs/promises";
import path from "node:path";
import { TextDecoder } from "node:util";
import { fileURLToPath } from "node:url";
import { describe, test } from "vitest";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const allowlistPath = "docs/rename/legacy-name-allowlist.tsv";
const maxFileBytes = 8 * 1024 * 1024;
const nameParts = ["terra", "base", "v2"];
const separator = String.raw`[\s._-]*`;
const legacyNameSource = nameParts.join(separator);
const utf8 = new TextDecoder("utf-8", { fatal: true });

/**
 * @typedef {object} NameMatch
 * @property {"path" | "text"} kind
 * @property {number} line
 * @property {number} column
 * @property {string} value
 */

/**
 * @typedef {object} Allowance
 * @property {number} expectedMatches
 * @property {string} fingerprint
 * @property {string} reason
 */

describe("retired product name", () => {
  test("canonical name is accepted and every separator variant is rejected", () => {
    const legacyName = new RegExp(legacyNameSource, "iu");
    for (const joiner of ["", "-", "_", " ", "."]) {
      const candidate = nameParts.join(joiner);
      assert.equal(legacyName.test(candidate), true, `separator ${JSON.stringify(joiner)} not covered`);
      assert.equal(legacyName.test(candidate.toUpperCase()), true);
    }
    assert.equal(legacyName.test(nameParts.slice(0, 2).join("")), false);
  });

  test("tracked files contain no retired variants outside the allowlist", async () => {
    const trackedPaths = readGitPaths();
    const allowlist = await readAllowlist();
    /** @type {string[]} */
    const violations = [];
    /** @type {Set<string>} */
    const seenAllowances = new Set();

    for (const relativePath of [...trackedPaths].sort()) {
      const textMatches = await collectTextMatches(relativePath);
      const matches = [
        ...collectMatches(relativePath, "path", 0),
        ...textMatches,
      ];
      const allowance = allowlist.get(relativePath);
      if (allowance !== undefined) {
        seenAllowances.add(relativePath);
        const actualFingerprint = fingerprintMatches(relativePath, matches);
        if (matches.length !== allowance.expectedMatches || actualFingerprint !== allowance.fingerprint) {
          violations.push(
            `${allowlistPath} [allowance mismatch for ${relativePath}: expected ${allowance.expectedMatches} matches ` +
            `and ${allowance.fingerprint}, found ${matches.length} and ${actualFingerprint}]`,
          );
        }
      } else {
        for (const match of matches) {
          violations.push(
            match.kind === "path"
              ? `${relativePath} [tracked path at column ${match.column}]`
              : `${relativePath}:${match.line}:${match.column} [tracked UTF-8 text]`,
          );
        }
      }
    }

    for (const relativePath of allowlist.keys()) {
      if (!trackedPaths.has(relativePath) || !seenAllowances.has(relativePath)) {
        violations.push(`${allowlistPath} [allowance names an untracked, missing, or unevaluated path: ${relativePath}]`);
      }
    }

    assert.deepEqual(violations, []);
  });
});

/** @returns {Set<string>} */
function readGitPaths() {
  const result = spawnSync("git", ["ls-files", "-z"], {
    cwd: repositoryRoot,
    encoding: "buffer",
    maxBuffer: 32 * 1024 * 1024,
  });
  assert.equal(result.status, 0, `git ls-files failed: ${result.stderr.toString("utf8")}`);
  return new Set(result.stdout.toString("utf8").split("\0").filter(Boolean));
}

/** @returns {Promise<Map<string, Allowance>>} */
async function readAllowlist() {
  const text = await readFile(path.join(repositoryRoot, allowlistPath), "utf8");
  /** @type {Map<string, Allowance>} */
  const entries = new Map();
  for (const [index, line] of text.split(/\r?\n/u).entries()) {
    if (line.length === 0 || line.startsWith("#")) continue;
    // The length assertion below guarantees all four columns are present.
    const fields = /** @type {[string, string, string, string]} */ (line.split("\t"));
    assert.equal(
      fields.length,
      4,
      `${allowlistPath}:${index + 1} must contain path, expected match count, match fingerprint, and reason`,
    );
    entries.set(fields[0], { expectedMatches: Number(fields[1]), fingerprint: fields[2], reason: fields[3] });
  }
  return entries;
}

/**
 * @param {string} relativePath
 * @returns {Promise<NameMatch[]>}
 */
async function collectTextMatches(relativePath) {
  const absolutePath = path.join(repositoryRoot, relativePath);
  let metadata;
  try {
    metadata = await lstat(absolutePath);
  } catch {
    return [];
  }
  if (!metadata.isFile() || metadata.isSymbolicLink()) return [];

  let bytes;
  try {
    const handle = await open(absolutePath, "r");
    try {
      const buffer = Buffer.allocUnsafe(maxFileBytes + 1);
      let offset = 0;
      while (offset < buffer.length) {
        const { bytesRead } = await handle.read(buffer, offset, buffer.length - offset, null);
        if (bytesRead === 0) break;
        offset += bytesRead;
      }
      if (offset > maxFileBytes) return [];
      bytes = buffer.subarray(0, offset);
    } finally {
      await handle.close();
    }
  } catch {
    return [];
  }
  if (bytes.includes(0)) return [];

  let text;
  try {
    text = utf8.decode(bytes);
  } catch {
    return [];
  }

  /** @type {NameMatch[]} */
  const matches = [];
  for (const [index, line] of text.split(/\r?\n/u).entries()) {
    matches.push(...collectMatches(line, "text", index + 1));
  }
  return matches;
}

/**
 * @param {string} value
 * @param {NameMatch["kind"]} kind
 * @param {number} line
 * @returns {NameMatch[]}
 */
function collectMatches(value, kind, line) {
  /** @type {NameMatch[]} */
  const matches = [];
  for (const match of value.matchAll(new RegExp(legacyNameSource, "giu"))) {
    matches.push({ kind, line, column: (match.index ?? 0) + 1, value: match[0] });
  }
  return matches;
}

/**
 * @param {string} relativePath
 * @param {readonly NameMatch[]} matches
 * @returns {string}
 */
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
