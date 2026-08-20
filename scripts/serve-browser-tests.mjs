import { execFile } from "node:child_process";
import { createReadStream, statSync } from "node:fs";
import { mkdtemp, readFile, rm } from "node:fs/promises";
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

const packageWorkspace = await mkdtemp(path.join(tmpdir(), "terrascale-sdk-browser-package-"));
const packageTarball = await packSdk(packageWorkspace);
await run("tar", ["-xzf", packageTarball, "-C", packageWorkspace]);
const packageRoot = path.join(packageWorkspace, "package");

const server = createServer((request, response) => {
  const requestUrl = new URL(request.url ?? "/", "http://127.0.0.1:4173");
  const relativePath = decodeURIComponent(requestUrl.pathname).replace(/^\/+/, "");
  const [contentRoot, contentPath] = relativePath.startsWith("candidate/")
    ? [packageRoot, relativePath.slice("candidate/".length)]
    : [root, relativePath];
  const candidate = path.resolve(contentRoot, contentPath);
  if (!candidate.startsWith(`${contentRoot}${path.sep}`) || !statIsFile(candidate)) {
    response.writeHead(404).end();
    return;
  }

  response.writeHead(200, {
    "content-type": contentTypes.get(path.extname(candidate)) ?? "application/octet-stream",
    "cache-control": "no-store",
  });
  createReadStream(candidate).pipe(response);
}).listen(4173, "127.0.0.1");

for (const signal of ["SIGINT", "SIGTERM"]) {
  process.once(signal, () => {
    server.close(() => {
      void rm(packageWorkspace, { recursive: true, force: true }).finally(() => process.exit(0));
    });
  });
}

async function packSdk(destination) {
  const result = await run("npm", ["pack", "--json", "--pack-destination", destination], { cwd: root });
  const metadata = JSON.parse(result.stdout);
  const filename = metadata[0]?.filename;
  if (typeof filename !== "string" || path.basename(filename) !== filename || !filename.endsWith(".tgz")) {
    throw new Error("npm pack did not produce a safe SDK tarball name.");
  }

  const tarball = path.join(destination, filename);
  await readFile(tarball);
  return tarball;
}

function statIsFile(filePath) {
  try {
    return statSync(filePath).isFile();
  } catch {
    return false;
  }
}
