import { createServer } from "node:http";
import { isIP } from "node:net";
/** @import { IncomingMessage, ServerResponse } from "node:http" */

const MAX_REQUEST_BODY_BYTES = 4 * 1024 * 1024;
const DEFAULT_HOST = "127.0.0.1";
const DEFAULT_PORT = 0;

/**
 * The local HTTP endpoint exposed by a running server.
 *
 * @typedef {Readonly<{
 *   readonly origin: string;
 *   readonly host: string;
 *   readonly port: number;
 * }>} LocalServerAddress
 */

/** @typedef {(request: Request) => Promise<Response>} LocalRequestHandler */
/** @typedef {Readonly<{ host?: string; port?: number }>} StartOptions */
/** @typedef {boolean | Readonly<{ allowRemote?: boolean }>} ConstructorOptions */
/** @typedef {ReturnType<typeof createServer>} ServerLike */

/**
 * A small Node-only bridge for handlers that speak the Fetch API.
 *
 * The handler is deliberately the sole routing authority. In particular, this
 * adapter does not reserve a health route (or any other private route).
 */
export class TerraScaleLocalHttpServer {
  /** @type {LocalRequestHandler} */
  #handler;
  /** @type {boolean} */
  #allowRemote;
  /** @type {unknown} */
  #server;
  /** @type {LocalServerAddress | undefined} */
  #address;
  /** @type {Promise<LocalServerAddress> | undefined} */
  #startPromise;
  /** @type {Promise<void> | undefined} */
  #closePromise;

  /**
   * @param {LocalRequestHandler} handler
   * @param {ConstructorOptions} [options]
   */
  constructor(handler, options = false) {
    if (typeof handler !== "function") {
      throw new TypeError(
        "TerraScaleLocalHttpServer requires a request handler.",
      );
    }

    const allowRemote =
      typeof options === "boolean"
        ? options
        : options !== null &&
            typeof options === "object" &&
            (options.allowRemote === undefined ||
              typeof options.allowRemote === "boolean")
          ? (options.allowRemote ?? false)
          : undefined;
    if (typeof allowRemote !== "boolean") {
      throw new TypeError("allowRemote must be a boolean.");
    }

    this.#handler = handler;
    this.#allowRemote = allowRemote;
  }

  /** @returns {LocalServerAddress | undefined} */
  get address() {
    return this.#address;
  }

  /**
   * @param {StartOptions} [options]
   * @returns {Promise<LocalServerAddress>}
   */
  start(options = {}) {
    if (this.#address !== undefined) {
      return Promise.resolve(this.#address);
    }
    if (this.#startPromise !== undefined) {
      return this.#startPromise;
    }

    const host = normalizeHost(options.host ?? DEFAULT_HOST);
    const port = normalizePort(options.port ?? DEFAULT_PORT);
    if (!this.#allowRemote && !isLoopbackHost(host)) {
      return Promise.reject(
        new TypeError(
          `Refusing to bind local HTTP server to non-loopback host "${host}" without allowRemote.`,
        ),
      );
    }

    /** @type {Promise<LocalServerAddress>} */
    const startPromise = new Promise((resolve, reject) => {
      const server = createServer((request, response) => {
        void handleRequest(request, response, this.#handler, host).catch(() => {
          // handleRequest owns the response and turns handler failures into a
          // response where possible. This final guard prevents a rejected
          // callback promise from becoming an unhandled rejection if the peer
          // has already disconnected.
        });
      });
      this.#server = server;

      /** @param {Error} error */
      const onError = (error) => {
        server.off("listening", onListening);
        this.#server = undefined;
        reject(error);
      };
      const onListening = () => {
        server.off("error", onError);
        const bound = server.address();
        if (bound === null || typeof bound === "string") {
          this.#server = undefined;
          reject(new Error("Local HTTP server did not expose a TCP address."));
          return;
        }

        const address = makeAddress(host, bound.port);
        this.#address = address;
        resolve(address);
      };

      server.once("error", onError);
      server.once("listening", onListening);
      server.listen({ host, port });
    });

    this.#startPromise = startPromise;
    void startPromise.catch(() => {
      if (this.#startPromise === startPromise) {
        this.#startPromise = undefined;
      }
    });
    return startPromise;
  }

  /** @returns {Promise<void>} */
  async close() {
    if (this.#closePromise !== undefined) {
      return this.#closePromise;
    }

    const closePromise = (async () => {
      const startPromise = this.#startPromise;
      if (startPromise !== undefined) {
        try {
          await startPromise;
        } catch {
          // A failed listen has already released its server reference.
        }
      }

      const server = /** @type {ServerLike | undefined} */ (this.#server);
      if (server === undefined) {
        this.#address = undefined;
        this.#startPromise = undefined;
        return;
      }

      await closeNodeServer(server);
      this.#server = undefined;
      this.#address = undefined;
      this.#startPromise = undefined;
    })();

    this.#closePromise = closePromise;
    try {
      await closePromise;
    } finally {
      if (this.#closePromise === closePromise) {
        this.#closePromise = undefined;
      }
    }
  }
}

/**
 * @param {string} host
 * @returns {string}
 */
function normalizeHost(host) {
  if (typeof host !== "string") {
    throw new TypeError("host must be a string.");
  }

  const trimmed = host.trim();
  if (trimmed.length === 0) {
    throw new TypeError("host cannot be empty.");
  }

  // URL notation commonly includes brackets around IPv6 literals, while
  // net.Server.listen expects the unbracketed form.
  if (trimmed.startsWith("[") && trimmed.endsWith("]")) {
    const unbracketed = trimmed.slice(1, -1);
    if (unbracketed.length === 0) {
      throw new TypeError("host cannot be empty.");
    }
    return unbracketed;
  }
  return trimmed;
}

/**
 * @param {number} port
 * @returns {number}
 */
function normalizePort(port) {
  if (!Number.isInteger(port) || port < 0 || port > 65_535) {
    throw new TypeError("port must be an integer between 0 and 65535.");
  }
  return port;
}

/**
 * @param {string} host
 * @returns {boolean}
 */
function isLoopbackHost(host) {
  const normalized = host.toLowerCase().replace(/\.$/u, "");
  if (normalized === "localhost") {
    return true;
  }

  const family = isIP(normalized);
  if (family === 4) {
    const octets = normalized.split(".").map(Number);
    return (
      octets.length === 4 &&
      octets[0] === 127 &&
      octets.every((octet) => octet >= 0 && octet <= 255)
    );
  }
  if (family === 6) {
    return normalized === "::1";
  }
  return false;
}

/**
 * @param {string} host
 * @param {number} port
 * @returns {LocalServerAddress}
 */
function makeAddress(host, port) {
  const urlHost =
    host.includes(":") && !host.startsWith("[") ? `[${host}]` : host;
  const origin = `http://${urlHost}:${port}`;
  return { origin, host, port };
}

/**
 * @param {IncomingMessage} incoming
 * @param {ServerResponse} outgoing
 * @param {LocalRequestHandler} handler
 * @param {string} fallbackHost
 * @returns {Promise<void>}
 */
async function handleRequest(incoming, outgoing, handler, fallbackHost) {
  const abortController = new AbortController();
  const abort = () => {
    if (!abortController.signal.aborted) {
      abortController.abort();
    }
    if (!outgoing.writableEnded && !outgoing.destroyed) {
      outgoing.destroy();
    }
  };
  const onIncomingAborted = () => abort();
  const onIncomingError = () => abort();
  const onIncomingClose = () => {
    if (!incoming.complete) {
      abort();
    }
  };
  const onOutgoingClose = () => {
    if (!outgoing.writableEnded) {
      abortController.abort();
    }
  };

  incoming.once("aborted", onIncomingAborted);
  incoming.once("error", onIncomingError);
  incoming.once("close", onIncomingClose);
  outgoing.once("close", onOutgoingClose);
  outgoing.once("error", onIncomingError);

  try {
    const body = await readRequestBody(incoming, abortController.signal);
    if (abortController.signal.aborted) {
      return;
    }

    const request = makeRequest(
      incoming,
      body,
      abortController.signal,
      fallbackHost,
    );
    const response = await awaitAbortable(
      Promise.resolve().then(() => handler(request)),
      abortController.signal,
    );
    if (abortController.signal.aborted || outgoing.destroyed) {
      return;
    }

    await writeResponse(
      outgoing,
      response,
      incoming.method ?? "GET",
      abortController.signal,
    );
  } catch (error) {
    if (abortController.signal.aborted || outgoing.destroyed) {
      return;
    }

    const status =
      error instanceof RequestBodyTooLargeError
        ? 413
        : error instanceof BadRequestError
          ? 400
          : 500;
    const message =
      status === 413
        ? "Request body exceeds the 4 MiB limit."
        : status === 400
          ? "Malformed request."
          : "Internal Server Error";
    sendErrorResponse(outgoing, status, message);
  } finally {
    incoming.off("aborted", onIncomingAborted);
    incoming.off("error", onIncomingError);
    incoming.off("close", onIncomingClose);
    outgoing.off("close", onOutgoingClose);
    outgoing.off("error", onIncomingError);
  }
}

/**
 * @param {IncomingMessage} incoming
 * @param {Uint8Array} body
 * @param {AbortSignal} signal
 * @param {string} fallbackHost
 * @returns {Request}
 */
function makeRequest(incoming, body, signal, fallbackHost) {
  const headers = new Headers();
  try {
    for (const [name, value] of Object.entries(incoming.headers)) {
      if (value === undefined) {
        continue;
      }
      if (Array.isArray(value)) {
        for (const item of value) {
          headers.append(name, item);
        }
      } else {
        headers.append(name, value);
      }
    }
  } catch {
    throw new BadRequestError();
  }

  const authority = incoming.headers.host ?? formatHostForUrl(fallbackHost);
  /** @type {URL} */
  let url;
  try {
    url = new URL(incoming.url ?? "/", `http://${authority}`);
  } catch {
    throw new BadRequestError();
  }

  const method = incoming.method ?? "GET";
  /** @type {RequestInit} */
  const init = { method, headers, signal };
  if (method !== "GET" && method !== "HEAD" && body.byteLength > 0) {
    // Node's Buffer/Uint8Array is a valid fetch body at runtime. The DOM
    // library's BodyInit declaration is narrower on some TypeScript versions.
    init.body = /** @type {BodyInit} */ (/** @type {unknown} */ (body));
  }
  try {
    return new Request(url, init);
  } catch {
    throw new BadRequestError();
  }
}

/**
 * @param {string} host
 * @returns {string}
 */
function formatHostForUrl(host) {
  return host.includes(":") && !host.startsWith("[") ? `[${host}]` : host;
}

/**
 * @param {IncomingMessage} incoming
 * @param {AbortSignal} signal
 * @returns {Promise<Uint8Array>}
 */
async function readRequestBody(incoming, signal) {
  const contentLength = incoming.headers["content-length"];
  const declaredLength =
    typeof contentLength === "string" ? Number(contentLength) : undefined;
  if (
    declaredLength !== undefined &&
    Number.isFinite(declaredLength) &&
    declaredLength > MAX_REQUEST_BODY_BYTES
  ) {
    incoming.resume();
    throw new RequestBodyTooLargeError();
  }

  return new Promise((resolve, reject) => {
    /** @type {Uint8Array[]} */
    const chunks = [];
    let size = 0;
    let settled = false;

    const cleanup = () => {
      incoming.off("data", onData);
      incoming.off("end", onEnd);
      incoming.off("error", onError);
      incoming.off("aborted", onAborted);
      signal.removeEventListener("abort", onSignalAbort);
    };
    /** @param {Error} [error] */
    const finish = (error) => {
      if (settled) {
        return;
      }
      settled = true;
      cleanup();
      if (error === undefined) {
        const output = new Uint8Array(size);
        let offset = 0;
        for (const chunk of chunks) {
          output.set(chunk, offset);
          offset += chunk.byteLength;
        }
        resolve(output);
      } else {
        reject(error);
      }
    };
    /** @param {Uint8Array | string} chunk */
    const onData = (chunk) => {
      const bytes =
        typeof chunk === "string" ? new TextEncoder().encode(chunk) : chunk;
      if (size + bytes.byteLength > MAX_REQUEST_BODY_BYTES) {
        incoming.resume();
        finish(new RequestBodyTooLargeError());
        return;
      }
      chunks.push(bytes);
      size += bytes.byteLength;
    };
    const onEnd = () => finish();
    /** @param {Error} error */
    const onError = (error) => finish(error);
    const onAborted = () => finish(new Error("Request aborted."));
    const onSignalAbort = () => finish(new Error("Request aborted."));

    incoming.on("data", onData);
    incoming.once("end", onEnd);
    incoming.once("error", onError);
    incoming.once("aborted", onAborted);
    signal.addEventListener("abort", onSignalAbort, { once: true });
    if (signal.aborted) {
      finish(new Error("Request aborted."));
    }
  });
}

/**
 * @param {ServerResponse} outgoing
 * @param {Response} response
 * @param {string} method
 * @param {AbortSignal} signal
 * @returns {Promise<void>}
 */
async function writeResponse(outgoing, response, method, signal) {
  for (const [name, value] of response.headers) {
    outgoing.setHeader(name, value);
  }

  const getSetCookie = /** @type {Headers & { getSetCookie?: () => string[] }} */ (
    response.headers
  ).getSetCookie;
  if (getSetCookie !== undefined) {
    const cookies = getSetCookie.call(response.headers);
    if (cookies.length > 0) {
      outgoing.setHeader("set-cookie", cookies);
    }
  }
  outgoing.writeHead(response.status);

  if (
    method === "HEAD" ||
    response.body === null ||
    response.status === 204 ||
    response.status === 205 ||
    response.status === 304
  ) {
    outgoing.end();
    return;
  }

  const reader = response.body.getReader();
  let completed = false;
  try {
    while (true) {
      const result = await awaitAbortable(reader.read(), signal);
      if (result.done) {
        break;
      }
      await writeChunk(outgoing, result.value, signal);
    }
    outgoing.end();
    completed = true;
  } finally {
    if (!completed) {
      try {
        await reader.cancel();
      } catch {
        // The body may already have observed the same disconnect.
      }
    }
    reader.releaseLock();
  }
}

/**
 * @param {ServerResponse} outgoing
 * @param {Uint8Array} chunk
 * @param {AbortSignal} signal
 * @returns {Promise<void>}
 */
function writeChunk(outgoing, chunk, signal) {
  if (signal.aborted || outgoing.destroyed) {
    return Promise.reject(new Error("Response aborted."));
  }
  if (outgoing.write(chunk)) {
    return Promise.resolve();
  }
  return new Promise((resolve, reject) => {
    const cleanup = () => {
      outgoing.off("drain", onDrain);
      outgoing.off("close", onClose);
      outgoing.off("error", onError);
      signal.removeEventListener("abort", onAbort);
    };
    const onDrain = () => {
      cleanup();
      resolve();
    };
    const onClose = () => {
      cleanup();
      reject(new Error("Response closed."));
    };
    /** @param {Error} error */
    const onError = (error) => {
      cleanup();
      reject(error);
    };
    const onAbort = () => {
      cleanup();
      reject(new Error("Response aborted."));
    };
    outgoing.once("drain", onDrain);
    outgoing.once("close", onClose);
    outgoing.once("error", onError);
    signal.addEventListener("abort", onAbort, { once: true });
    if (signal.aborted) {
      onAbort();
    }
  });
}

/**
 * @template T
 * @param {Promise<T>} promise
 * @param {AbortSignal} signal
 * @returns {Promise<T>}
 */
async function awaitAbortable(promise, signal) {
  if (signal.aborted) {
    throw new Error("Operation aborted.");
  }
  return new Promise((resolve, reject) => {
    const onAbort = () => {
      cleanup();
      reject(new Error("Operation aborted."));
    };
    const cleanup = () => signal.removeEventListener("abort", onAbort);
    signal.addEventListener("abort", onAbort, { once: true });
    promise.then(
      (value) => {
        cleanup();
        resolve(value);
      },
      /** @param {unknown} error */
      (error) => {
        cleanup();
        reject(error);
      },
    );
  });
}

/**
 * @param {ServerResponse} outgoing
 * @param {number} status
 * @param {string} message
 * @returns {void}
 */
function sendErrorResponse(outgoing, status, message) {
  if (outgoing.destroyed || outgoing.headersSent) {
    if (!outgoing.destroyed && !outgoing.writableEnded) {
      outgoing.destroy();
    }
    return;
  }
  try {
    outgoing.statusCode = status;
    outgoing.setHeader("content-type", "text/plain; charset=utf-8");
    outgoing.setHeader("content-length", Buffer.byteLength(message));
    outgoing.end(message);
  } catch {
    outgoing.destroy();
  }
}

/**
 * @param {ServerLike} server
 * @returns {Promise<void>}
 */
function closeNodeServer(server) {
  return new Promise((resolve, reject) => {
    server.close((error) => {
      if (
        error !== undefined &&
        /** @type {NodeJS.ErrnoException} */ (error).code !== "ERR_SERVER_NOT_RUNNING"
      ) {
        reject(error);
        return;
      }
      resolve();
    });
  });
}

class RequestBodyTooLargeError extends Error {}
class BadRequestError extends Error {}
