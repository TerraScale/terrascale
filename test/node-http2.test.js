import { execFile } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { createSecureServer } from "node:http2";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { expect, test } from "vitest";

const run = promisify(execFile);
const runner = fileURLToPath(new URL("./support/node-http2-client.js", import.meta.url));
const project = "01ARZ3NDEKTSV4RRFFQ69G5FA1";
// Isolated synthetic TLS transport fixtures; not TerraBase/ts-auth qualification.
for (const surface of ["documents", "auth"]) {
  for (const protocol of ["h2", "http/1.1"]) {
    test(`${surface}: ${protocol} negotiation, credentials and shared connection reuse`, async () => {
      const directory = await mkdtemp(path.join(tmpdir(), "ts-js-http2-"));
      const certPath = path.join(directory, "cert.pem");
      const keyPath = path.join(directory, "key.pem");
      /** @type {Set<import("node:tls").TLSSocket>} */
      const sockets = new Set();
      /** @type {import("node:http2").Http2SecureServer | undefined} */
      let server;
      try {
        await run("openssl", ["req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
          "-keyout", keyPath, "-out", certPath, "-subj", "/CN=localhost",
          "-addext", "subjectAltName=DNS:localhost,IP:127.0.0.1", "-addext", "basicConstraints=critical,CA:TRUE"]);
        server = createSecureServer({ key: await readFile(keyPath), cert: await readFile(certPath),
          allowHTTP1: true, ALPNCallback: ({ protocols }) => protocols.includes(protocol) ? protocol : undefined });
        let connections = 0;
        /** @type {{ version: string; authorization: string | undefined; path: string | undefined }[]} */
        const requests = [];
        server.on("secureConnection", socket => {
          connections++;
          sockets.add(socket);
          socket.on("close", () => sockets.delete(socket));
        });
        server.on("request", (request, response) => {
          requests.push({ version: request.httpVersion, authorization: request.headers.authorization, path: request.url });
          response.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify(surface === "documents"
            ? { id: project, version: "opaque", state: "value", document: { value: 1 } }
            : { data: [] }));
        });
        await new Promise((resolve, reject) => {
          server?.once("error", reject);
          server?.listen(0, "127.0.0.1", () => resolve(undefined));
        });
        const address = server.address();
        if (address === null || typeof address === "string") throw new Error("Missing fixture address.");
        await run(process.execPath, [runner, `https://127.0.0.1:${address.port}`, surface, protocol], {
          env: { ...process.env, NODE_EXTRA_CA_CERTS: certPath }, timeout: 20_000,
        });
        expect(requests).toHaveLength(17);
        expect(connections).toBe(1);
        for (const request of requests) {
          expect(request.version).toBe(protocol === "h2" ? "2.0" : "1.1");
          expect(request.authorization).toBe("Bearer fixture.payload.signature");
          expect(request.path).toBe(surface === "documents"
            ? `/v1/projects/${project}/databases/app/collections/users/documents/by-key?id=${project}`
            : "/v1/acme/users");
        }
      } finally {
        for (const socket of sockets) socket.destroy();
        if (server !== undefined) await new Promise((resolve, reject) => server?.close(error => error ? reject(error) : resolve(undefined)));
        await rm(directory, { recursive: true, force: true });
      }
    }, 30_000);
  }
}
