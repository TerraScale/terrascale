/** Node-only PostgreSQL client for the signed INT8/TEXT point SQL listener. */
import { lookup } from "node:dns/promises";
import { isIP } from "node:net";
import { X509Certificate } from "node:crypto";
import { checkServerIdentity, createSecureContext, TLSSocket } from "node:tls";
/**
 * @import {
 *   Client as DriverClient,
 *   ClientConfig,
 *   DatabaseError,
 *   FieldDef,
 *   QueryArrayConfig,
 *   QueryArrayResult,
 * } from "pg"
 */

const MAX_BYTES = 16 * 1024;
const MIN_INT8 = -(1n << 63n);
const MAX_INT8 = (1n << 63n) - 1n;

/** @typedef {bigint | string} PostgresParameter */
/**
 * @typedef {{
 *   readonly statement: string;
 *   readonly parameters: readonly unknown[];
 * }} PostgresQuery
 */
/**
 * @typedef {{
 *   readonly statement: string;
 *   readonly parameters: readonly PostgresParameter[];
 * }} PostgresBoundQuery
 */
/**
 * @typedef {{
 *   readonly host: string;
 *   readonly port: number;
 *   readonly tlsServerName: string;
 *   readonly caCertificatePem: string;
 *   readonly database: string;
 *   readonly keyId: string;
 *   readonly password: string;
 *   readonly connectTimeoutMs?: number;
 * }} PostgresConfig
 */
/**
 * @typedef {{
 *   readonly name: string;
 *   readonly oid: 20 | 25;
 * }} PostgresColumn
 */
/**
 * @typedef {{
 *   readonly command: string;
 *   readonly affectedRows: number;
 *   readonly columns: readonly PostgresColumn[];
 *   readonly rows: readonly (readonly PostgresParameter[])[];
 * }} PostgresResult
 */

/** Diagnostics retain SQLSTATE, but never driver messages, SQL or passwords. */
export class PostgresError extends Error {
  /**
   * @override
   * @readonly
   */
  name = /** @type {const} */ ("PostgresError");
  /**
   * @readonly
   * @type {"configuration" | "query" | "connection" | "database" | "protocol"}
   */
  kind;
  /**
   * @readonly
   * @type {string | undefined}
   */
  sqlState;

  /**
   * @param {"configuration" | "query" | "connection" | "database" | "protocol"} kind
   * @param {string} [sqlState]
   */
  constructor(kind, sqlState) {
    super(
      `terrascale PostgreSQL ${kind} failure${sqlState === undefined ? "" : ` (${sqlState})`}`,
    );
    this.kind = kind;
    this.sqlState = sqlState;
  }
}

/**
 * @param {string} value
 * @returns {boolean}
 */
function validText(value) {
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

/**
 * Validate and clone a builder descriptor without converting or interpolating values.
 *
 * @param {string} statement
 * @param {readonly unknown[]} [parameters]
 * @returns {PostgresBoundQuery}
 */
export function postgresQuery(statement, parameters = []) {
  if (
    typeof statement !== "string" ||
    statement.trim() === "" ||
    !validText(statement) ||
    !Array.isArray(parameters) ||
    parameters.length > 4
  )
    throw new PostgresError("query");
  /** @type {PostgresParameter[]} */
  const values = parameters.map((value) => {
    if (typeof value === "bigint" && value >= MIN_INT8 && value <= MAX_INT8)
      return value;
    if (typeof value === "string" && validText(value)) return value;
    throw new PostgresError("query");
  });
  return Object.freeze({ statement, parameters: Object.freeze(values) });
}

/**
 * @param {string} value
 * @param {number} bytes
 * @returns {boolean}
 */
function canonicalBase64(value, bytes) {
  return (
    /^[A-Za-z0-9_-]+$/.test(value) &&
    Buffer.from(value, "base64url").length === bytes &&
    Buffer.from(value, "base64url").toString("base64url") === value
  );
}

/**
 * @param {PostgresConfig} config
 * @returns {PostgresConfig & { readonly connectTimeoutMs: number }}
 */
function configuration(config) {
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

/**
 * @param {unknown} error
 * @param {typeof DatabaseError} [databaseError]
 * @returns {PostgresError}
 */
function failure(error, databaseError) {
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

/**
 * @param {readonly FieldDef[]} fields
 * @returns {readonly PostgresColumn[]}
 */
function columns(fields) {
  return Object.freeze(
    fields.map((field) => {
      if (field.dataTypeID !== 20 && field.dataTypeID !== 25)
        throw new PostgresError("protocol");
      return Object.freeze({ name: field.name, oid: field.dataTypeID });
    }),
  );
}

/** One driver connection; no automatic reconnect, mutation retry or callback replay. */
export class PostgresClient {
  /** @type {DriverClient} */
  #driver;
  /** @type {typeof DatabaseError} */
  #databaseError;
  #closed = false;
  /**
   * @private
   * @param {DriverClient} driver
   * @param {typeof DatabaseError} databaseError
   */
  constructor(driver, databaseError) {
    this.#driver = driver;
    this.#databaseError = databaseError;
    driver.on("error", () => {
      this.#closed = true;
    });
  }

  /**
   * @param {PostgresConfig} input
   * @returns {Promise<PostgresClient>}
   */
  static async connect(input) {
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
    /** @type {ReturnType<typeof setTimeout> | undefined} */
    let timer;
    try {
      // pg overwrites SNI for DNS hosts. Resolve only the configured address,
      // then send the separately pinned TLS name through its SSL options.
      const address = isIP(config.host)
        ? config.host
        : await Promise.race([
            lookup(config.host).then((result) => result.address),
            /** @type {Promise<never>} */ (
              new Promise((_resolve, reject) => {
                timer = setTimeout(() => {
                  reject(new PostgresError("connection"));
                }, config.connectTimeoutMs);
              })
            ),
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

        /**
         * @override
         * @param {string | symbol} event
         * @param {...unknown} args
         * @returns {boolean}
         */
        emit(event, ...args) {
          if (event !== "sslconnect") return super.emit(event, ...args);
          const hasListeners = this.listenerCount(event) > 0;
          if (this.#tlsStarted) return hasListeners;
          this.#tlsStarted = true;
          const socket = this.stream;
          if (!(socket instanceof TLSSocket)) {
            socket.destroy(new PostgresError("protocol"));
            return hasListeners;
          }
          /** @returns {void} */
          const cleanup = () => {
            socket.off("secureConnect", verified);
            socket.off("error", cleanup);
            socket.off("close", cleanup);
          };
          /** @returns {void} */
          const verified = () => {
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
      /**
       * @type {ClientConfig & {
       *   binary: false;
       *   client_encoding: string;
       *   sslnegotiation: "postgres";
       * }}
       */
      const driverConfig = {
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
          getTypeParser: (
            /** @type {number} */ oid,
            /** @type {string | undefined} */ format,
          ) => {
            if (format === "binary")
              return () => {
                throw new PostgresError("protocol");
              };
            if (oid === 20)
              return (/** @type {string} */ value) => BigInt(value);
            return (/** @type {string} */ value) => value;
          },
        },
      };
      // Connection's pinned runtime option uses camelCase; the driver's public
      // ClientConfig type does not yet declare the supported connection seam.
      /** @type {ClientConfig & { sslNegotiation: "postgres" }} */
      const connectionConfig = {
        ssl: driverConfig.ssl,
        sslNegotiation: "postgres",
      };
      /**
       * @type {ClientConfig & {
       *   connection: InstanceType<typeof VerifiedTlsConnection>;
       * }}
       */
      const clientConfig = {
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

  /**
   * @param {PostgresQuery} input
   * @returns {Promise<PostgresResult>}
   */
  async query(input) {
    const query = postgresQuery(input.statement, input.parameters);
    if (this.#closed) throw new PostgresError("connection");
    try {
      /** @type {QueryArrayConfig<string[]> & { binary: false }} */
      const queryConfig = {
        text: query.statement,
        values: query.parameters.map((value) =>
          typeof value === "bigint" ? value.toString() : value,
        ),
        rowMode: "array",
        binary: false,
      };
      const result = /** @type {QueryArrayResult<unknown[]>} */ (
        await this.#driver.query(queryConfig)
      );
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
      const rows = result.rows.map((row) => {
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

  /**
   * @returns {Promise<void>}
   */
  async close() {
    this.#closed = true;
    try {
      await this.#driver.end();
    } catch (error) {
      throw failure(error, this.#databaseError);
    }
  }
}
