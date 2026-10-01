#!/usr/bin/env node

import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import { appendFile, mkdir, mkdtemp, rm, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { promisify } from "node:util";
import { inspectNpmCli } from "./npm-cli-evidence.js";
import { npmReleasePolicy } from "./npm-release-policy.js";

const execute = promisify(execFile);
const runnerTemporary = process.env.RUNNER_TEMP?.trim() || tmpdir();
const workspace = await mkdtemp(path.join(runnerTemporary, "terrascale-reviewed-npm-"));
let provisioned = false;
try {
  const response = await fetch(npmReleasePolicy.npm.archiveUrl, {
    redirect: "error",
    signal: AbortSignal.timeout(60_000),
  });
  if (!response.ok) throw new Error(`Reviewed npm archive download failed with HTTP ${response.status}.`);
  const archive = Buffer.from(await response.arrayBuffer());
  if (archive.length < 1 || archive.length > 32 * 1024 * 1024) {
    throw new Error("Reviewed npm archive size is outside its safe bound.");
  }
  const digest = createHash("sha512").update(archive).digest();
  if (
    digest.toString("hex") !== npmReleasePolicy.npm.archiveSha512 ||
    `sha512-${digest.toString("base64")}` !== npmReleasePolicy.npm.archiveIntegrity
  ) {
    throw new Error("Reviewed npm archive SHA-512 does not match release policy.");
  }
  const archivePath = path.join(workspace, `npm-${npmReleasePolicy.npm.version}.tgz`);
  await writeFile(archivePath, archive, { mode: 0o600 });
  await execute("tar", ["-xzf", archivePath, "-C", workspace], { maxBuffer: 16 * 1024 * 1024 });

  const binaryDirectory = path.join(workspace, "path");
  await mkdir(binaryDirectory, { mode: 0o700 });
  await symlink(
    path.join(workspace, "package", "bin", "npm-cli.js"),
    path.join(binaryDirectory, process.platform === "win32" ? "npm.cmd" : "npm"),
  );

  if (process.env.GITHUB_ENV === undefined || process.env.GITHUB_ENV.trim() === "") {
    throw new Error("GITHUB_ENV is required to bind reviewed npm evidence across workflow steps.");
  }
  if (process.env.GITHUB_PATH === undefined || process.env.GITHUB_PATH.trim() === "") {
    throw new Error("GITHUB_PATH is required to select reviewed npm across workflow steps.");
  }

  process.env.PATH = `${binaryDirectory}${path.delimiter}${process.env.PATH ?? ""}`;
  const npmCli = await inspectNpmCli({ archiveIntegrity: npmReleasePolicy.npm.archiveIntegrity });
  if (npmCli.evidence.version !== npmReleasePolicy.npm.version) {
    throw new Error("Reviewed npm CLI extraction resolved the wrong version.");
  }
  await Promise.all([
    appendFile(
      process.env.GITHUB_ENV,
      `TERRASCALE_NPM_ARCHIVE_INTEGRITY=${npmReleasePolicy.npm.archiveIntegrity}\n`,
    ),
    appendFile(process.env.GITHUB_PATH, `${binaryDirectory}\n`),
  ]);
  provisioned = true;
  console.log(`Provisioned reviewed npm ${npmCli.evidence.version} from SHA-512-bound archive bytes.`);
} finally {
  if (!provisioned) await rm(workspace, { recursive: true, force: true });
}
