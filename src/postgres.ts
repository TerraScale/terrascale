/** Node-only PostgreSQL client for the signed INT8/TEXT point SQL listener. */
import { lookup } from "node:dns/promises";
import { isIP } from "node:net";
import { X509Certificate } from "node:crypto";
import { checkServerIdentity, createSecureContext, TLSSocket } from "node:tls";
import type {
  Client as DriverClient,
  ClientConfig,
  DatabaseError,
  FieldDef,
  QueryArrayConfig,
} from "pg";

const MAX_BYTES = 16 * 1024;
const MIN_INT8 = -(1n << 63n);
const MAX_INT8 = (1n << 63n) - 1n;

export type PostgresParameter = bigint | string;
export interface PostgresQuery {
  readonly statement: string;
  readonly parameters: readonly unknown[];
}
export interface PostgresBoundQuery {
  readonly statement: string;
  readonly parameters: readonly PostgresParameter[];
}
export interface PostgresConfig {
  readonly host: string;
  readonly port: number;
  readonly tlsServerName: string;
  readonly caCertificatePem: string;
  readonly database: string;
  readonly keyId: string;
  readonly password: string;
  readonly connectTimeoutMs?: number;
}
export interface PostgresColumn {
  readonly name: string;
  readonly oid: 20 | 25;
}
export interface PostgresResult {
  readonly command: string;
  readonly affectedRows: number;
  readonly columns: readonly PostgresColumn[];
  readonly rows: readonly (readonly PostgresParameter[])[];
}

/** Diagnostics retain SQLSTATE, but never driver messages, SQL or passwords. */
export class PostgresError extends Error {
  public override readonly name = "PostgresError";
  public constructor(
    public readonly kind:
      | "configuration"
      | "query"
      | "connection"
      | "database"
      | "protocol",
    public readonly sqlState?: string,
  ) {
    super(
      `terrascale PostgreSQL ${kind} failure${sqlState === undefined ? "" : ` (${sqlState})`}`,
    );
  }
}

function validText(value: string): boolean {
  if (value.includes("\0") || Buffer.byteLength(value, "utf8") > MAX_BYTES)
    return false;
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(++index);
      if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
    } else if (code >= 0xdc00 && code <= 0xdfff) return false;
  }
  return true;
}

/** Validate and clone a builder descriptor without converting or interpolating values. */
export function postgresQuery(
  statement: string,
  parameters: readonly unknown[] = [],
): PostgresBoundQuery {
  if (
    typeof statement !== "string" ||
    statement.trim() === "" ||
    !validText(statement) ||
    !Array.isArray(parameters) ||
    parameters.length > 4
  )
    throw new PostgresError("query");
  const values: PostgresParameter[] = parameters.map((value: unknown) => {
    if (typeof value === "bigint" && value >= MIN_INT8 && value <= MAX_INT8)
      return value;
    if (typeof value === "string" && validText(value)) return value;
    throw new PostgresError("query");
  });
  return Object.freeze({ statement, parameters: Object.freeze(values) });
}

function canonicalBase64(value: string, bytes: number): boolean {
  return (
    /^[A-Za-z0-9_-]+$/.test(value) &&
    Buffer.from(value, "base64url").length === bytes &&
    Buffer.from(value, "base64url").toString("base64url") === value
  );
}

function configuration(
  config: PostgresConfig,
): PostgresConfig & { readonly connectTimeoutMs: number } {
  const timeout = config.connectTimeoutMs ?? 5000;
  const parts =
    typeof config.password === "string" ? config.password.split(".") : [];
  if (
    typeof config.host !== "string" ||
    config.host === "" ||
    config.host.length > 253 ||
    /[\s,\/\0]/.test(config.host) ||
    !Number.isInteger(config.port) ||
    config.port < 1 ||
    config.port > 65535 ||
    typeof config.tlsServerName !== "string" ||
    config.tlsServerName === "" ||
    /[\s,\/\0]/.test(config.tlsServerName) ||
    !Number.isInteger(timeout) ||
    timeout < 1 ||
    timeout > 10000 ||
    !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(config.database) ||
    !canonicalBase64(config.keyId, 16) ||
    parts.length !== 3 ||
    parts[0] !== "tbk1" ||
    parts[1] !== config.keyId ||
    !canonicalBase64(parts[2] ?? "", 32) ||
    typeof config.caCertificatePem !== "string" ||
    config.caCertificatePem.length > 64 * 1024
  ) {
    throw new PostgresError("configuration");
  }
  try {
    const certificates = config.caCertificatePem.match(
      /-----BEGIN CERTIFICATE-----[\s\S]+?-----END CERTIFICATE-----/g,
    );
    if (certificates === null || certificates.length === 0)
      throw new PostgresError("configuration");
    for (const certificate of certificates) new X509Certificate(certificate);
    createSecureContext({
      ca: config.caCertificatePem,
      minVersion: "TLSv1.3",
      maxVersion: "TLSv1.3",
    });
  } catch {
    throw new PostgresError("configuration");
  }
  return Object.freeze({ ...config, connectTimeoutMs: timeout });
}

function failure(
  error: unknown,
  databaseError?: typeof DatabaseError,
): PostgresError {
  if (error instanceof PostgresError) return error;
  if (
    databaseError !== undefined &&
    error instanceof databaseError &&
    typeof error.code === "string" &&
    /^[0-9A-Z]{5}$/.test(error.code)
  ) {
    return new PostgresError("database", error.code);
  }
  return new PostgresError("connection");
}

function columns(fields: readonly FieldDef[]): readonly PostgresColumn[] {
  return Object.freeze(
    fields.map((field): PostgresColumn => {
      if (field.dataTypeID !== 20 && field.dataTypeID !== 25)
        throw new PostgresError("protocol");
      return Object.freeze({ name: field.name, oid: field.dataTypeID });
    }),
  );
}

/** One driver connection; no automatic reconnect, mutation retry or callback replay. */
export class PostgresClient {
  readonly #driver: DriverClient;
  readonly #databaseError: typeof DatabaseError;
  #closed = false;
  private constructor(
    driver: DriverClient,
    databaseError: typeof DatabaseError,
  ) {
    this.#driver = driver;
    this.#databaseError = databaseError;
    driver.on("error", () => {
      this.#closed = true;
    });
  }

  public static async connect(input: PostgresConfig): Promise<PostgresClient> {
    const config = configuration(input);
    // pg treats empty options/replication as absent and reads these variables.
    // Reject them before loading the driver or opening a socket.
    if (
      process.env["NODE_PG_FORCE_NATIVE"] ||
      process.env["PGOPTIONS"] ||
      process.env["PGREPLICATION"]
    ) {
      throw new PostgresError("configuration");
    }
    const started = Date.now();
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      // pg overwrites SNI for DNS hosts. Resolve only the configured address,
      // then send the separately pinned TLS name through its SSL options.
      const address = isIP(config.host)
        ? config.host
        : await Promise.race([
            lookup(config.host).then((result) => result.address),
            new Promise<never>((_resolve, reject) => {
              timer = setTimeout(() => {
                reject(new PostgresError("connection"));
              }, config.connectTimeoutMs);
            }),
          ]);
      if (timer !== undefined) clearTimeout(timer);
      const remaining = config.connectTimeoutMs - (Date.now() - started);
      if (remaining <= 0) throw new PostgresError("connection");
      const { default: pg } = await import("pg");
      // Pinned pg 8.23 emits sslconnect immediately after creating TLSSocket.
      // Its Client sends startup on that event, so fence the event itself:
      // waiting for Client.connect() would disclose credentials too early.
      class VerifiedTlsConnection extends pg.Connection {
        #tlsStarted = false;

        public override emit(
          event: string | symbol,
          ...args: unknown[]
        ): boolean {
          if (event !== "sslconnect") return super.emit(event, ...args);
          const hasListeners = this.listenerCount(event) > 0;
          if (this.#tlsStarted) return hasListeners;
          this.#tlsStarted = true;
          const socket = this.stream;
          if (!(socket instanceof TLSSocket)) {
            socket.destroy(new PostgresError("protocol"));
            return hasListeners;
          }
          const cleanup = (): void => {
            socket.off("secureConnect", verified);
            socket.off("error", cleanup);
            socket.off("close", cleanup);
          };
          const verified = (): void => {
            cleanup();
            if (socket.destroyed) return;
            if (!socket.authorized || socket.alpnProtocol !== "postgresql") {
              socket.destroy(new PostgresError("protocol"));
              return;
            }
            super.emit(event, ...args);
          };
          socket.once("secureConnect", verified);
          socket.once("error", cleanup);
          socket.once("close", cleanup);
          return hasListeners;
        }
      }
      const driverConfig: ClientConfig & {
        binary: false;
        client_encoding: string;
        sslnegotiation: "postgres";
      } = {
        host: address,
        port: config.port,
        database: config.database,
        user: config.keyId,
        password: config.password,
        connectionTimeoutMillis: remaining,
        application_name: "terrascale-postgres",
        options: "",
        binary: false,
        client_encoding: "UTF8",
        sslnegotiation: "postgres",
        ssl: {
          ca: config.caCertificatePem,
          minVersion: "TLSv1.3",
          maxVersion: "TLSv1.3",
          rejectUnauthorized: true,
          ALPNProtocols: ["postgresql"],
          servername: config.tlsServerName,
          checkServerIdentity: (_host, certificate) =>
            checkServerIdentity(config.tlsServerName, certificate),
        },
        types: {
          getTypeParser: (oid: number, format?: string) => {
            if (format === "binary")
              return () => {
                throw new PostgresError("protocol");
              };
            if (oid === 20) return (value: string) => BigInt(value);
            return (value: string) => value;
          },
        },
      };
      // Connection's pinned runtime option uses camelCase; the driver's public
      // ClientConfig type does not yet declare the supported connection seam.
      const connectionConfig: ClientConfig & { sslNegotiation: "postgres" } = {
        ssl: driverConfig.ssl,
        sslNegotiation: "postgres",
      };
      const clientConfig: ClientConfig & {
        connection: InstanceType<typeof VerifiedTlsConnection>;
      } = {
        ...driverConfig,
        connection: new VerifiedTlsConnection(connectionConfig),
      };
      const driver = new pg.Client(clientConfig);
      const client = new PostgresClient(driver, pg.DatabaseError);
      try {
        await driver.connect();
        return client;
      } catch (error) {
        await driver.end().catch(() => undefined);
        throw failure(error, pg.DatabaseError);
      }
    } catch (error) {
      throw failure(error);
    } finally {
      if (timer !== undefined) clearTimeout(timer);
    }
  }

  public async query(input: PostgresQuery): Promise<PostgresResult> {
    const query = postgresQuery(input.statement, input.parameters);
    if (this.#closed) throw new PostgresError("connection");
    try {
      const queryConfig: QueryArrayConfig<string[]> & { binary: false } = {
        text: query.statement,
        values: query.parameters.map((value) =>
          typeof value === "bigint" ? value.toString() : value,
        ),
        rowMode: "array",
        binary: false,
      };
      const result = await this.#driver.query<unknown[], string[]>(queryConfig);
      const schema = columns(result.fields);
      if (
        result.rows.length > 1 ||
        ![
          "SELECT",
          "INSERT",
          "UPDATE",
          "DELETE",
          "BEGIN",
          "COMMIT",
          "ROLLBACK",
          "SET",
        ].includes(result.command)
      ) {
        throw new PostgresError("protocol");
      }
      const rows = result.rows.map((row): readonly PostgresParameter[] => {
        if (row.length !== schema.length) throw new PostgresError("protocol");
        return Object.freeze(
          row.map((value, index) => {
            if (
              schema[index]?.oid === 20 &&
              typeof value === "bigint" &&
              value >= MIN_INT8 &&
              value <= MAX_INT8
            )
              return value;
            if (
              schema[index]?.oid === 25 &&
              typeof value === "string" &&
              validText(value)
            )
              return value;
            throw new PostgresError("protocol");
          }),
        );
      });
      const affectedRows = result.rowCount ?? 0;
      if (
        !Number.isSafeInteger(affectedRows) ||
        affectedRows < 0 ||
        affectedRows > 2
      )
        throw new PostgresError("protocol");
      return Object.freeze({
        command: result.command,
        affectedRows,
        columns: schema,
        rows: Object.freeze(rows),
      });
    } catch (error) {
      throw failure(error, this.#databaseError);
    }
  }

  public async close(): Promise<void> {
    this.#closed = true;
    try {
      await this.#driver.end();
    } catch (error) {
      throw failure(error, this.#databaseError);
    }
  }
}
