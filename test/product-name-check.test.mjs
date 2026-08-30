import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { copyFile, mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { afterEach, describe, test } from "node:test";

const checkerSource = new URL("../scripts/check-product-name.mjs", import.meta.url);
const nameParts = ["terra", "base", "v2"];
const maxFileBytes = 8 * 1024 * 1024;
const temporaryRepositories = new Set();

afterEach(async () => {
  await Promise.all([...temporaryRepositories].map((repository) => rm(repository, { recursive: true, force: true })));
  temporaryRepositories.clear();
});

describe("product-name migration check", () => {
  test("accepts the canonical product name in both scan modes", async () => {
    const repository = await createRepository({
      "README.md": nameParts.slice(0, 2).join(""),
    });
    assert.equal(runChecker(repository).status, 0);
    assert.equal(runChecker(repository, "--tracked-only").status, 0);
  });

  test("rejects every supported separator and mixed case", async () => {
    for (const joiner of ["", "-", "_", " ", "."]) {
      const retiredName = nameParts.join(joiner).toUpperCase();
      const repository = await createRepository({ "README.md": retiredName });
      const result = runChecker(repository);
      assert.notEqual(result.status, 0, `accepted separator ${JSON.stringify(joiner)}`);
      assert.match(result.stderr, /README\.md:1:1 \[tracked UTF-8 text\]/u);
    }
  });

  test("reports tracked and untracked paths and text accurately", async () => {
    const retiredPath = path.join("docs", `${nameParts.join("-")}.md`);
    const repository = await createRepository(
      { [retiredPath]: "archived" },
      { "scratch.txt": nameParts.join("_") },
    );
    const result = runChecker(repository);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /\[tracked path at column/u);
    assert.match(result.stderr, /scratch\.txt:1:1 \[untracked UTF-8 text\]/u);
  });

  test("tracked-only mode ignores ordinary untracked files", async () => {
    const repository = await createRepository(
      { "README.md": nameParts.slice(0, 2).join("") },
      { "scratch.txt": nameParts.join("_") },
    );
    assert.notEqual(runChecker(repository).status, 0);
    assert.equal(runChecker(repository, "--tracked-only").status, 0);
  });

  test("skips a nested repository instead of scanning its files or directory", async () => {
    const repository = await createRepository({ "README.md": nameParts.slice(0, 2).join("") });
    const nestedRepository = path.join(repository, "Dashboard");
    await mkdir(nestedRepository, { recursive: true });
    runGit(nestedRepository, ["init", "--quiet"]);
    await writeFile(path.join(nestedRepository, "README.md"), nameParts.join(""), "utf8");

    const result = runChecker(repository);
    assert.equal(result.status, 0, result.stderr);
  });

  test("binds an allowance to the exact count, locations, and matched tokens", async () => {
    const evidencePath = "evidence/record.md";
    const retiredName = nameParts.join("");
    const fingerprint = allowanceFingerprint(evidencePath, [
      { kind: "text", line: 1, column: 1, value: retiredName },
    ]);
    const allowance = allowanceLine(evidencePath, 1, fingerprint);
    const repository = await createRepository({ [evidencePath]: retiredName }, {}, allowance);
    assert.equal(runChecker(repository).status, 0);

    await writeFile(path.join(repository, evidencePath), retiredName.toUpperCase(), "utf8");
    const changedToken = runChecker(repository);
    assert.notEqual(changedToken.status, 0);
    assert.match(changedToken.stderr, /allowance mismatch/u);

    await writeFile(path.join(repository, evidencePath), `${retiredName}\n${retiredName}`, "utf8");
    const changedCount = runChecker(repository);
    assert.notEqual(changedCount.status, 0);
    assert.match(changedCount.stderr, /found 2/u);

    await writeFile(path.join(repository, evidencePath), nameParts.slice(0, 2).join(""), "utf8");
    const stale = runChecker(repository);
    assert.notEqual(stale.status, 0);
    assert.match(stale.stderr, /found 0/u);
  });

  test("fails closed before reading a file beyond the size limit", async () => {
    const repository = await createRepository({
      "oversized.txt": Buffer.alloc(maxFileBytes + 1, 0x61),
    });
    const result = runChecker(repository);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /file exceeds the 8388608-byte scan limit/u);
  });
});

async function createRepository(trackedFiles, untrackedFiles = {}, allowlist = "") {
  const repository = await mkdtemp(path.join(tmpdir(), "terrascale-product-name-"));
  temporaryRepositories.add(repository);
  await mkdir(path.join(repository, "scripts"), { recursive: true });
  await mkdir(path.join(repository, "docs", "rename"), { recursive: true });
  await copyFile(checkerSource, path.join(repository, "scripts", "check-product-name.mjs"));
  await writeFile(
    path.join(repository, "docs", "rename", "legacy-name-allowlist.tsv"),
    `# path<TAB>expected-match-count<TAB>match-fingerprint-sha256<TAB>reason\n${allowlist}`,
    "utf8",
  );
  for (const [relativePath, content] of Object.entries(trackedFiles)) {
    await writeFixture(repository, relativePath, content);
  }

  runGit(repository, ["init", "--quiet"]);
  runGit(repository, ["add", "."]);

  for (const [relativePath, content] of Object.entries(untrackedFiles)) {
    await writeFixture(repository, relativePath, content);
  }
  return repository;
}

async function writeFixture(repository, relativePath, content) {
  const absolutePath = path.join(repository, relativePath);
  await mkdir(path.dirname(absolutePath), { recursive: true });
  await writeFile(absolutePath, content);
}

function runChecker(repository, ...arguments_) {
  return spawnSync(process.execPath, ["scripts/check-product-name.mjs", ...arguments_], {
    cwd: repository,
    encoding: "utf8",
  });
}

function runGit(repository, arguments_) {
  const result = spawnSync("git", arguments_, { cwd: repository, encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr);
}

function allowanceLine(relativePath, expectedMatches, fingerprint) {
  return `${relativePath}\t${expectedMatches}\t${fingerprint}\tImmutable evidence.\n`;
}

function allowanceFingerprint(relativePath, matches) {
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
