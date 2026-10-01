import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import { promisify } from "node:util";

const execute = promisify(execFile);

/**
 * @typedef {object} ReleaseSource
 * @property {string} revision
 * @property {string} tree
 * @property {boolean} clean
 * @property {string | null} dirtyStatusSha256
 * @property {{ root: string, classification: string, trackedFiles: number }} toolingExclusion
 */

/**
 * @param {string} repositoryRoot
 * @param {{ allowDirty?: boolean }} [options]
 * @returns {Promise<ReleaseSource>}
 */
export async function inspectReleaseSource(repositoryRoot, { allowDirty = false } = {}) {
  const [revision, tree, committedTree, status, trackedTooling, ignoredTooling] = await Promise.all([
    git(repositoryRoot, ["rev-parse", "HEAD"]),
    git(repositoryRoot, ["write-tree"]),
    git(repositoryRoot, ["rev-parse", "HEAD^{tree}"]),
    git(repositoryRoot, ["status", "--porcelain", "--untracked-files=all"]),
    git(repositoryRoot, ["ls-files", "--", ".tools"]),
    gitAllowFailure(repositoryRoot, ["check-ignore", "--no-index", ".tools/terrascale-deploy"]),
  ]);
  if (trackedTooling.trim() !== "") throw new Error(".tools must not contain tracked candidate source files.");
  if (ignoredTooling.exitCode !== 0) throw new Error(".tools must be an explicitly ignored workflow-tooling root.");

  const dirty = status !== "" || tree.trim() !== committedTree.trim();
  if (dirty && !allowDirty) {
    throw new Error("Artifact qualification requires a clean Git worktree; commit the candidate or pass --allow-dirty for diagnostic evidence.");
  }
  return {
    revision: revision.trim(),
    tree: tree.trim(),
    clean: !dirty,
    dirtyStatusSha256: dirty ? sha256(Buffer.from(`${tree.trim()}\n${committedTree.trim()}\n${status}`)) : null,
    toolingExclusion: {
      root: ".tools",
      classification: "ignored-workflow-tooling",
      trackedFiles: 0,
    },
  };
}

/**
 * @param {string} repositoryRoot
 * @param {string} revision
 * @returns {Promise<string>}
 */
export async function gitTreeDigest(repositoryRoot, revision) {
  const result = await execute("git", ["ls-tree", "-r", "-z", revision], {
    cwd: repositoryRoot,
    encoding: "buffer",
    maxBuffer: 64 * 1024 * 1024,
  });
  return `sha256:${sha256(result.stdout)}`;
}

/**
 * @param {string} repositoryRoot
 * @param {string[]} arguments_
 * @returns {Promise<string>}
 */
async function git(repositoryRoot, arguments_) {
  return (await execute("git", arguments_, { cwd: repositoryRoot, maxBuffer: 16 * 1024 * 1024 })).stdout;
}

/**
 * @param {string} repositoryRoot
 * @param {string[]} arguments_
 * @returns {Promise<{ exitCode: number, stdout: string }>}
 */
async function gitAllowFailure(repositoryRoot, arguments_) {
  try {
    const result = await git(repositoryRoot, arguments_);
    return { exitCode: 0, stdout: result };
  } catch (error) {
    if (typeof error === "object" && error !== null && "code" in error && "stdout" in error) {
      return { exitCode: Number(error.code), stdout: String(error.stdout) };
    }
    throw error;
  }
}

/**
 * @param {Uint8Array} bytes
 * @returns {string}
 */
function sha256(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}
