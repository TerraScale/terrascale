import { createSecureContext, rootCertificates, TLSSocket } from "node:tls";
import { createServer } from "node:net";
import { once } from "node:events";
import pg from "pg";
import { afterEach, describe, expect, it, vi } from "vitest";
import { PostgresClient, PostgresError, postgresQuery } from "./postgres.js";
/** @import { Socket } from "node:net" */
/** @import { PostgresConfig } from "./postgres.js" */

const keyId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const config = {
  host: "127.0.0.1",
  port: 5432,
  tlsServerName: "postgres.example.test",
  caCertificatePem: rootCertificates[0] ?? "",
  database: "00000000000000000000000000",
  keyId,
  password: `tbk1.${keyId}.${Buffer.alloc(32).toString("base64url")}`,
};

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllEnvs();
});

describe("PostgreSQL typed point parameters", () => {
  it("retains hostile text and full INT8 bounds as separate values", () => {
    const payload = "quote ' ; DROP TABLE items; -- \\ Unicode α 🌍";
    const parameters = [-(1n << 63n), payload, (1n << 63n) - 1n];
    const query = postgresQuery(
      "INSERT INTO items (id, body) VALUES ($1, $2)",
      parameters,
    );
    parameters[1] = "changed";
    expect(query.parameters).toEqual([-(1n << 63n), payload, (1n << 63n) - 1n]);
    expect(query.statement).not.toContain(payload);
    expect(Object.isFrozen(query.parameters)).toBe(true);
  });
  it.each([
    0,
    Number.MAX_SAFE_INTEGER,
    true,
    null,
    new Uint8Array([1]),
    1n << 63n,
    -(1n << 63n) - 1n,
    "nul\0",
    "\ud800",
    "\udc00",
    "a".repeat(16385),
  ])("rejects unsupported or lossy parameters before dispatch", (value) => {
    expect(() =>
      postgresQuery("SELECT id FROM items WHERE id=$1", [value]),
    ).toThrow(PostgresError);
  });
  it("rejects oversized statements and parameter counts", () => {
    expect(() => postgresQuery("x".repeat(16385))).toThrow(PostgresError);
    expect(() =>
      postgresQuery("SELECT id FROM items WHERE id=$1", [1n, 2n, 3n, 4n, 5n]),
    ).toThrow(PostgresError);
  });
  it("configuration diagnostics never contain supplied credentials", async () => {
    const password = "tbk1.secret-do-not-report";
    /** @type {unknown} */
    let failure;
    try {
      await PostgresClient.connect({
        host: "",
        port: 0,
        tlsServerName: "",
        caCertificatePem: "",
        database: "invalid",
        keyId: "invalid",
        password,
      });
    } catch (error) {
      failure = error;
    }
    expect(failure).toBeInstanceOf(PostgresError);
    expect(String(failure)).not.toContain(password);
  });
  it.each(["PGOPTIONS", "PGREPLICATION", "NODE_PG_FORCE_NATIVE"])(
    "rejects ambient %s before driver connection",
    async (name) => {
      vi.stubEnv(name, "hostile-setting");
      const connect = vi.spyOn(pg.Client.prototype, "connect");
      await expect(PostgresClient.connect(config)).rejects.toMatchObject({
        kind: "configuration",
      });
      expect(connect).not.toHaveBeenCalled();
    },
  );
  it("uses the driver DatabaseError class to retain only real SQLSTATE diagnostics", async () => {
    vi.spyOn(pg.Client.prototype, "connect").mockResolvedValue(undefined);
    vi.spyOn(pg.Client.prototype, "end").mockResolvedValue(undefined);
    const query = vi.spyOn(pg.Client.prototype, "query");
    const client = await PostgresClient.connect(config);
    query.mockRejectedValueOnce(
      Object.assign(new Error(config.password), { code: "EPERM" }),
    );
    await expect(client.query(postgresQuery("BEGIN"))).rejects.toMatchObject({
      kind: "connection",
      sqlState: undefined,
    });
    const databaseError = new pg.DatabaseError(config.password, 0, "error");
    databaseError.code = "42501";
    query.mockRejectedValueOnce(databaseError);
    /** @type {unknown} */
    let failure;
    try {
      await client.query(postgresQuery("BEGIN"));
    } catch (error) {
      failure = error;
    }
    expect(failure).toMatchObject({ kind: "database", sqlState: "42501" });
    expect(String(failure)).not.toContain(config.password);
    await client.close();
  });
});

// Synthetic local TLS security peers only. No TerraBase server or successful
// PostgreSQL authentication is simulated; the matching peer explicitly denies startup.
const peerCertificate = `-----BEGIN CERTIFICATE-----
MIIDRTCCAi2gAwIBAgIUWrAyIttO9LCN+2pFMG1QKGchAjkwDQYJKoZIhvcNAQEL
BQAwIDEeMBwGA1UEAwwVcG9zdGdyZXMuZXhhbXBsZS50ZXN0MCAXDTI2MDkzMDA2
MTAxNVoYDzIxMjYwOTA2MDYxMDE1WjAgMR4wHAYDVQQDDBVwb3N0Z3Jlcy5leGFt
cGxlLnRlc3QwggEiMA0GCSqGSIb3DQEBAQUAA4IBDwAwggEKAoIBAQDF/R58RF1+
gL4HnUZVC/CkjP1HjQpL3iZ98tDcD8GtkaNEZ4lX6v/tG719Yk8kLsBPPdja+xOW
TsUfP48Y8eaWzJq9g15bBsK01jj/XhlQMjl/pYRiARGDGNGRozgtfb6OLHhhW9lZ
7ZN3+vQc/MuneOIuJJtCTi7EN+rLk76kuBhJRY96ulkX07BCj1yF5EyOhZIU4F8/
m8F6Yx3Q1KOpLdqGVAzMAOS2gDUPP41HZoWstRpTJJnk9saqZdylBNN7FxbHHadK
UfqpqMeJ3KcnayyyWym5RKiE9gxFU+l62rvezYIuovKsypD0DvSB9RCfOijHI6iR
vYw5jUKOZdQnAgMBAAGjdTBzMB0GA1UdDgQWBBSvEF5HrxY1pd5QCHXnccNYGEHI
wjAfBgNVHSMEGDAWgBSvEF5HrxY1pd5QCHXnccNYGEHIwjAgBgNVHREEGTAXghVw
b3N0Z3Jlcy5leGFtcGxlLnRlc3QwDwYDVR0TAQH/BAUwAwEB/zANBgkqhkiG9w0B
AQsFAAOCAQEAvWiRMPlt0wi8LaCFyFwA05upKJewIFyBXMMQlpHjiTy2FYl88VkK
wwMaiFYTMC/P18sHqy2hXAhpuDcgahq5sTxWam0R5/NAs6TfinYO+00FUdvQL80A
ATwxpUkRYSLDvrCgJNUffpbnW/BTtzJTmGZBPTSXfdpRNn+XnoN5F9r3POHCUQQR
piYSIvxKg5LogVTP6xbxb4dw1VYBZKqJmhJJBCgyheVz6DlgfsgTrOlgIbPBtJaJ
pzdlwE/JcW8jZVVfM1b8qaTVgSVTeT7Jm+LzkCjDVw1slqWpqxRN7mQHOW+cfRfi
IheD8/n1X9O89/twufx3b6/Sru6wS3h4cA==
-----END CERTIFICATE-----`;
const peerPrivateKey = `-----BEGIN PRIVATE KEY-----
MIIEvgIBADANBgkqhkiG9w0BAQEFAASCBKgwggSkAgEAAoIBAQDF/R58RF1+gL4H
nUZVC/CkjP1HjQpL3iZ98tDcD8GtkaNEZ4lX6v/tG719Yk8kLsBPPdja+xOWTsUf
P48Y8eaWzJq9g15bBsK01jj/XhlQMjl/pYRiARGDGNGRozgtfb6OLHhhW9lZ7ZN3
+vQc/MuneOIuJJtCTi7EN+rLk76kuBhJRY96ulkX07BCj1yF5EyOhZIU4F8/m8F6
Yx3Q1KOpLdqGVAzMAOS2gDUPP41HZoWstRpTJJnk9saqZdylBNN7FxbHHadKUfqp
qMeJ3KcnayyyWym5RKiE9gxFU+l62rvezYIuovKsypD0DvSB9RCfOijHI6iRvYw5
jUKOZdQnAgMBAAECggEAWdHMmDydm6vFSggIJcIOcGBD8Udjceq81KupJASD7X2i
L4C3FqsWYteQXgcQ9028UdaxgBqilrByjgwUG5F2dlgpJSupTStifo6m+fcTDcZT
Pnz0808fxjUmseb1r/GZVr+G90gncTwuCCLRxyrKK1ZUafGSfACjB8KEYsnKlcff
sTBzGORRri0JIO2KCJ7rRRDOhAY/wwfNfWMAEBaBy4ZVNkk/RM/ty/Z4Gdj4wGVv
vSg8QGd8bKKQgmfxRvdpZqK1iriECw8CqibKxaD5i0y2YScWrf8mo5p8S+jDDk27
2aIJuPFm7hu9/yh0UlFzWNEWd0D3jv7SbnzwJwRsQQKBgQD10Jyn1jj+o2+8jAzu
aYLAMv06AGh6ZM9RXd+dQkc+VrGRTjFOW8N5SrZR6MlL1u4LTk3ByaNt+6Kk9jY8
sncFBxaws7By0RXy4AAZUNX42zdiljHOKhVe4X5M/Gs2/EY8mVeplciSR/pVLqcr
VclrCPDQrkqM8GEkWwUNuuaHQQKBgQDOMTWhdethkjk955vce1qoFmP0sQzoRmBW
qoDl4QE/6xqJ68vaA0GleSiE8gqNN11mxUxTxAk3ntt/rqmydeq0hcOMKzvcXxEp
YTpkAZGXREzQZqwBMCuiu4CrHsK1XqipUA+J4YlgFW/ejBYgAHHOpdH5zCAvHsol
V7l5TPwpZwKBgBu8rWCW4JmDEaGh3/eq+jD7z+0SjLd61pXk1M2m4+kZ2TF/NDsW
SalPD+GQNd6T4wptjIKYYROQ+FpK2fXhYWK02Q+ZRwi9pQ9hzUEOCQbqnYZHUllk
EENDiH/hX6u1ras61lRxbU9sRcgE2qIeLPsh6CJz+D+NBDkY+CoDc+3BAoGBAKBM
dYtO5211hnbwBqYEaFhg/9iNhKFXhTIbYBZXNOeCWaF75YFaIsEITk389eMPdded
71b4Aspw/+hl25vYPF2X2IyyV9ldNzRaWFDDP3LAMI+cIoDR+DqNVtyyD2F9c9Yd
XdctteUzBUOY2OKMNe4X0ARJg4Pehf0+E/2PskJlAoGBAKR5x1FGp6yn7800A9hT
FeE3jMBla/F5vBxf76A+iU13Z7q3IpNGPFvBdW9JWhHeu+lv/ZHnUpyUlJCscGpR
Tn4hIBO/adMVAwGeEWKliX3DWjynb91Do3PDpiZOiV7G/n/uwSOoDBRnv9Ik/b7A
eAjsa7kEk5Y8oMx3dd7smsQc
-----END PRIVATE KEY-----`;

/**
 * @param {string[]} protocols
 * @param {(
 *   input: PostgresConfig & { connectTimeoutMs: number },
 *   observations: {
 *     bytes: Buffer[];
 *     negotiated: (string | false | null)[];
 *     names: (string | false | null | undefined)[];
 *   },
 * ) => Promise<void>} action
 * @param {{ stall?: boolean; tlsServerName?: string; trustedCa?: string }} [options]
 * @returns {Promise<void>}
 */
async function tlsPeer(protocols, action, options = {}) {
  /** @type {Set<Socket>} */
  const sockets = new Set();
  const observations = {
    bytes: /** @type {Buffer[]} */ ([]),
    negotiated: /** @type {(string | false | null)[]} */ ([]),
    names: /** @type {(string | false | null | undefined)[]} */ ([]),
  };
  const secureContext = createSecureContext({
    key: peerPrivateKey,
    cert: peerCertificate,
    minVersion: "TLSv1.3",
    maxVersion: "TLSv1.3",
  });
  const server = createServer((socket) => {
    sockets.add(socket);
    socket.on("close", () => {
      sockets.delete(socket);
    });
    socket.on("error", () => undefined);
    let sslRequest = Buffer.alloc(0);
    /**
     * @param {Buffer} chunk
     * @returns {void}
     */
    const readSsl = (chunk) => {
      sslRequest = Buffer.concat([sslRequest, chunk]);
      if (sslRequest.length < 8) return;
      socket.off("data", readSsl);
      if (
        sslRequest.length !== 8 ||
        sslRequest.readInt32BE(0) !== 8 ||
        sslRequest.readInt32BE(4) !== 80877103
      ) {
        socket.destroy();
        return;
      }
      socket.write("S");
      if (options.stall) return;
      const secure = new TLSSocket(socket, {
        isServer: true,
        secureContext,
        ALPNProtocols: protocols,
        minVersion: "TLSv1.3",
        maxVersion: "TLSv1.3",
      });
      // Ownership of the same TCP handle transfers to the TLS wrapper; the
      // wrapped raw Socket does not independently emit close on every path.
      sockets.delete(socket);
      sockets.add(secure);
      secure.on("close", () => {
        sockets.delete(secure);
      });
      secure.on("error", () => undefined);
      secure.once("secure", () => {
        observations.negotiated.push(secure.alpnProtocol);
        observations.names.push(secure.servername);
      });
      let startup = Buffer.alloc(0);
      secure.on("data", (/** @type {Buffer} */ chunk) => {
        observations.bytes.push(chunk);
        startup = Buffer.concat([startup, chunk]);
        if (startup.length < 4 || startup.length < startup.readInt32BE(0))
          return;
        // A real driver startup was released only after the accepted handshake.
        // End the probe with a valid explicit denial, never a fake database success.
        const fields = Buffer.from(
          "SFATAL\0C28000\0MIntentional synthetic TLS peer denial\0\0",
        );
        const header = Buffer.alloc(5);
        header[0] = 0x45;
        header.writeInt32BE(fields.length + 4, 1);
        secure.end(Buffer.concat([header, fields]));
      });
    };
    socket.on("data", readSsl);
  });
  try {
    server.listen(0, "127.0.0.1");
    await once(server, "listening");
    const address = server.address();
    if (address === null || typeof address === "string")
      throw new Error("No local test address.");
    await action(
      {
        ...config,
        port: address.port,
        connectTimeoutMs: options.stall ? 50 : 2000,
        caCertificatePem: options.trustedCa ?? peerCertificate,
        tlsServerName: options.tlsServerName ?? config.tlsServerName,
      },
      observations,
    );
  } finally {
    for (const socket of sockets) socket.destroy();
    await /** @type {Promise<void>} */ (
      new Promise((resolve, reject) => {
        server.close((error) => {
          if (error) reject(error);
          else resolve(undefined);
        });
      })
    );
    await /** @type {Promise<void>} */ (
      new Promise((resolve) => {
        setImmediate(resolve);
      })
    );
    expect(server.listening).toBe(false);
    for (const socket of sockets) {
      expect(socket.destroyed).toBe(true);
      expect(socket.closed).toBe(true);
    }
  }
}

describe("pinned PostgreSQL driver verified TLS startup fence", () => {
  it.each([
    { name: "missing ALPN", protocols: [] },
    { name: "incompatible peer ALPN", protocols: ["http/1.1"] },
  ])(
    "rejects $name before any startup or password bytes",
    async ({ protocols }) => {
      await tlsPeer(protocols, async (input, observations) => {
        const started = Date.now();
        // A missing selection reaches the SDK's protocol fence. The incompatible
        // TLS peer instead sends no_application_protocol during negotiation.
        await expect(PostgresClient.connect(input)).rejects.toMatchObject({
          kind: protocols.length === 0 ? "protocol" : "connection",
        });
        expect(Date.now() - started).toBeLessThan(input.connectTimeoutMs);
        expect(Buffer.concat(observations.bytes).length).toBe(0);
        expect(observations.negotiated).not.toContain("postgresql");
      });
    },
  );
  it("offers and verifies postgresql ALPN before releasing the actual driver's startup", async () => {
    await tlsPeer(["postgresql"], async (input, observations) => {
      await expect(PostgresClient.connect(input)).rejects.toMatchObject({
        kind: "database",
        sqlState: "28000",
      });
      expect(observations.negotiated).toEqual(["postgresql"]);
      expect(observations.names).toEqual([config.tlsServerName]);
      const bytes = Buffer.concat(observations.bytes);
      expect(bytes.readInt32BE(4)).toBe(196608);
      expect(bytes.toString()).toContain(config.keyId);
      expect(bytes.toString()).toContain(config.database);
      expect(bytes.toString()).not.toContain(config.password);
    });
  });
  it.each([
    { name: "wrong TLS name", tlsServerName: "wrong.example.test" },
    { name: "untrusted certificate", trustedCa: config.caCertificatePem },
  ])(
    "retains $name rejection without startup/password",
    async ({ tlsServerName, trustedCa }) => {
      await tlsPeer(
        ["postgresql"],
        async (input, observations) => {
          await expect(PostgresClient.connect(input)).rejects.toMatchObject({
            kind: "connection",
          });
          expect(Buffer.concat(observations.bytes).length).toBe(0);
        },
        {
          ...(tlsServerName === undefined ? {} : { tlsServerName }),
          ...(trustedCa === undefined ? {} : { trustedCa }),
        },
      );
    },
  );
  it("closes a stalled handshake at the driver deadline without delayed startup", async () => {
    await tlsPeer(
      ["postgresql"],
      async (input, observations) => {
        await expect(PostgresClient.connect(input)).rejects.toMatchObject({
          kind: "connection",
        });
        expect(Buffer.concat(observations.bytes).length).toBe(0);
      },
      { stall: true },
    );
  });
});
