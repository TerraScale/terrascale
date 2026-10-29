import { createHash } from "node:crypto";
import { inspectNpmCli } from "./npm-cli-evidence.js";
import { npmReleasePolicy } from "./npm-release-policy.js";
import { execFile } from "node:child_process";
import { createReadStream, statSync } from "node:fs";
import { lstat, mkdtemp, readFile, realpath, rm } from "node:fs/promises";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const run = promisify(execFile);
const contentTypes = new Map([
  [".html", "text/html; charset=utf-8"],
  [".js", "text/javascript; charset=utf-8"],
  [".json", "application/json; charset=utf-8"],
]);

const packageWorkspace = await mkdtemp(
  path.join(tmpdir(), "terrascale-browser-package-"),
);
const packageTarball =
  process.env.TERRASCALE_BROWSER_CANDIDATE_TARBALL === undefined
    ? await packSdk(packageWorkspace)
    : await verifiedCandidateTarball(
        process.env.TERRASCALE_BROWSER_CANDIDATE_TARBALL,
      );
await run("tar", ["-xzf", packageTarball, "-C", packageWorkspace]);
const packageRoot = path.join(packageWorkspace, "package");

const server = createServer((request, response) => {
  const requestUrl = new URL(request.url ?? "/", "http://127.0.0.1:4173");
  const relativePath = decodeURIComponent(requestUrl.pathname).replace(
    /^\/+/,
    "",
  );
  const [contentRoot, contentPath] = relativePath.startsWith("candidate/")
    ? [packageRoot, relativePath.slice("candidate/".length)]
    : [root, relativePath];
  const candidate = path.resolve(contentRoot, contentPath);
  if (
    !candidate.startsWith(`${contentRoot}${path.sep}`) ||
    !statIsFile(candidate)
  ) {
    response.writeHead(404).end();
    return;
  }

  response.writeHead(200, {
    "content-type":
      contentTypes.get(path.extname(candidate)) ?? "application/octet-stream",
    "cache-control": "no-store",
  });
  createReadStream(candidate).pipe(response);
}).listen(4173, "127.0.0.1");

for (const signal of ["SIGINT", "SIGTERM"]) {
  process.once(signal, () => {
    server.close(() => {
      void rm(packageWorkspace, { recursive: true, force: true }).finally(() =>
        process.exit(0),
      );
    });
  });
}

/**
 * @param {string} destination
 * @returns {Promise<string>}
 */
async function packSdk(destination) {
  const npm = await inspectNpmCli();
  if (npm.evidence.version !== npmReleasePolicy.npm.version)
    throw new Error("Browser packaging requires the reviewed npm CLI.");
  const result = await run(
    process.execPath,
    [npm.entrypoint, "pack", "--json", "--pack-destination", destination],
    { cwd: root },
  );
  const metadata = JSON.parse(result.stdout);
  const filename = metadata[0]?.filename;
  if (
    typeof filename !== "string" ||
    path.basename(filename) !== filename ||
    !filename.endsWith(".tgz")
  ) {
    throw new Error("npm pack did not produce a safe SDK tarball name.");
  }

  const tarball = path.join(destination, filename);
  await readFile(tarball);
  return tarball;
}

/**
 * @param {string} candidate
 * @returns {Promise<string>}
 */
async function verifiedCandidateTarball(candidate) {
  const resolved = path.resolve(root, candidate);
  const [real, metadata] = await Promise.all([
    realpath(resolved),
    lstat(resolved),
  ]);
  if (
    real !== resolved ||
    !metadata.isFile() ||
    metadata.isSymbolicLink() ||
    !resolved.endsWith(".tgz")
  ) {
    throw new Error(
      "Browser qualification candidate must be a regular non-symlink npm tarball.",
    );
  }
  const expected = process.env.TERRASCALE_BROWSER_CANDIDATE_TARBALL_SHA256;
  if (
    expected === undefined ||
    !/^[0-9a-f]{64}$/u.test(expected) ||
    createHash("sha256")
      .update(await readFile(resolved))
      .digest("hex") !== expected
  ) {
    throw new Error(
      "Browser qualification candidate SHA-256 is missing or does not match.",
    );
  }
  return resolved;
}

/**
 * @param {string} filePath
 * @returns {boolean}
 */
function statIsFile(filePath) {
  try {
    return statSync(filePath).isFile();
  } catch {
    return false;
  }
}
