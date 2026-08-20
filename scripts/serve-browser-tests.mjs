import { createReadStream, statSync } from "node:fs";
import { createServer } from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const contentTypes = new Map([
  [".html", "text/html; charset=utf-8"],
  [".js", "text/javascript; charset=utf-8"],
  [".json", "application/json; charset=utf-8"],
]);

createServer((request, response) => {
  const requestUrl = new URL(request.url ?? "/", "http://127.0.0.1:4173");
  const relativePath = decodeURIComponent(requestUrl.pathname).replace(/^\/+/, "");
  const candidate = path.resolve(root, relativePath);
  if (!candidate.startsWith(`${root}${path.sep}`) || !statIsFile(candidate)) {
    response.writeHead(404).end();
    return;
  }

  response.writeHead(200, {
    "content-type": contentTypes.get(path.extname(candidate)) ?? "application/octet-stream",
    "cache-control": "no-store",
  });
  createReadStream(candidate).pipe(response);
}).listen(4173, "127.0.0.1");

function statIsFile(filePath) {
  try {
    return statSync(filePath).isFile();
  } catch {
    return false;
  }
}
