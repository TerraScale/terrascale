import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { isIP } from "node:net";

const MAX_REQUEST_BODY_BYTES = 2 * 1024 * 1024;
const DEFAULT_HOST = "127.0.0.1";
const DEFAULT_PORT = 0;

/** The local HTTP endpoint exposed by a running server. */
export type LocalServerAddress = Readonly<{
  readonly origin: string;
  readonly apiBaseUrl: string;
  readonly host: string;
  readonly port: number;
}>;

type LocalRequestHandler = (request: Request) => Promise<Response>;
type StartOptions = Readonly<{ host?: string; port?: number }>;
type ConstructorOptions = boolean | Readonly<{ allowRemote?: boolean }>;
type ServerLike = ReturnType<typeof createServer>;

/**
 * A small Node-only bridge for handlers that speak the Fetch API.
 *
 * The handler is deliberately the sole routing authority. In particular, this
 * adapter does not reserve a health route (or any other private route).
 */
export class TerraScaleLocalHttpServer {
  readonly #handler: LocalRequestHandler;
  readonly #allowRemote: boolean;
  #server: unknown;
  #address: LocalServerAddress | undefined;
  #startPromise: Promise<LocalServerAddress> | undefined;
  #closePromise: Promise<void> | undefined;

  constructor(handler: LocalRequestHandler, options: ConstructorOptions = false) {
    if (typeof handler !== "function") {
      throw new TypeError("TerraScaleLocalHttpServer requires a request handler.");
    }

    const allowRemote = typeof options === "boolean"
      ? options
      : options !== null && typeof options === "object" &&
          (options.allowRemote === undefined || typeof options.allowRemote === "boolean")
        ? options.allowRemote ?? false
        : undefined;
    if (typeof allowRemote !== "boolean") {
      throw new TypeError("allowRemote must be a boolean.");
    }

    this.#handler = handler;
    this.#allowRemote = allowRemote;
  }

  get address(): LocalServerAddress | undefined {
    return this.#address;
  }

  start(options: StartOptions = {}): Promise<LocalServerAddress> {
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

    const startPromise = new Promise<LocalServerAddress>((resolve, reject) => {
      const server = createServer((request, response) => {
        void handleRequest(request, response, this.#handler, host).catch(() => {
          // handleRequest owns the response and turns handler failures into a
          // response where possible. This final guard prevents a rejected
          // callback promise from becoming an unhandled rejection if the peer
          // has already disconnected.
        });
      });
      this.#server = server;

      const onError = (error: Error): void => {
        server.off("listening", onListening);
        this.#server = undefined;
        reject(error);
      };
      const onListening = (): void => {
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

  async close(): Promise<void> {
    if (this.#closePromise !== undefined) {
      return this.#closePromise;
    }

    const closePromise = (async (): Promise<void> => {
      const startPromise = this.#startPromise;
      if (startPromise !== undefined) {
        try {
          await startPromise;
        } catch {
          // A failed listen has already released its server reference.
        }
      }

      const server = this.#server as ServerLike | undefined;
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

function normalizeHost(host: string): string {
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

function normalizePort(port: number): number {
  if (!Number.isInteger(port) || port < 0 || port > 65_535) {
    throw new TypeError("port must be an integer between 0 and 65535.");
  }
  return port;
}

function isLoopbackHost(host: string): boolean {
  const normalized = host.toLowerCase().replace(/\.$/u, "");
  if (normalized === "localhost") {
    return true;
  }

  const family = isIP(normalized);
  if (family === 4) {
    const octets = normalized.split(".").map(Number);
    return octets.length === 4 && octets[0] === 127 && octets.every((octet) => octet >= 0 && octet <= 255);
  }
  if (family === 6) {
    return normalized === "::1";
  }
  return false;
}

function makeAddress(host: string, port: number): LocalServerAddress {
  const urlHost = host.includes(":") && !host.startsWith("[") ? `[${host}]` : host;
  const origin = `http://${urlHost}:${port}`;
  return { origin, apiBaseUrl: origin, host, port };
}

async function handleRequest(
  incoming: IncomingMessage,
  outgoing: ServerResponse,
  handler: LocalRequestHandler,
  fallbackHost: string,
): Promise<void> {
  const abortController = new AbortController();
  const abort = (): void => {
    if (!abortController.signal.aborted) {
      abortController.abort();
    }
    if (!outgoing.writableEnded && !outgoing.destroyed) {
      outgoing.destroy();
    }
  };
  const onIncomingAborted = (): void => abort();
  const onIncomingError = (): void => abort();
  const onIncomingClose = (): void => {
    if (!incoming.complete) {
      abort();
    }
  };
  const onOutgoingClose = (): void => {
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

    const request = makeRequest(incoming, body, abortController.signal, fallbackHost);
    const response = await awaitAbortable(
      Promise.resolve().then(() => handler(request)),
      abortController.signal,
    );
    if (abortController.signal.aborted || outgoing.destroyed) {
      return;
    }

    await writeResponse(outgoing, response, incoming.method ?? "GET", abortController.signal);
  } catch (error) {
    if (abortController.signal.aborted || outgoing.destroyed) {
      return;
    }

    const status = error instanceof RequestBodyTooLargeError ? 413 : error instanceof BadRequestError ? 400 : 500;
    const message = status === 413 ? "Request body exceeds the 2 MiB limit." : status === 400 ? "Malformed request." : "Internal Server Error";
    sendErrorResponse(outgoing, status, message);
  } finally {
    incoming.off("aborted", onIncomingAborted);
    incoming.off("error", onIncomingError);
    incoming.off("close", onIncomingClose);
    outgoing.off("close", onOutgoingClose);
    outgoing.off("error", onIncomingError);
  }
}

function makeRequest(
  incoming: IncomingMessage,
  body: Uint8Array,
  signal: AbortSignal,
  fallbackHost: string,
): Request {
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
  let url: URL;
  try {
    url = new URL(incoming.url ?? "/", `http://${authority}`);
  } catch {
    throw new BadRequestError();
  }

  const method = incoming.method ?? "GET";
  const init: RequestInit = { method, headers, signal };
  if (method !== "GET" && method !== "HEAD" && body.byteLength > 0) {
    // Node's Buffer/Uint8Array is a valid fetch body at runtime. The DOM
    // library's BodyInit declaration is narrower on some TypeScript versions.
    init.body = body as unknown as BodyInit;
  }
  try {
    return new Request(url, init);
  } catch {
    throw new BadRequestError();
  }
}

function formatHostForUrl(host: string): string {
  return host.includes(":") && !host.startsWith("[") ? `[${host}]` : host;
}

async function readRequestBody(incoming: IncomingMessage, signal: AbortSignal): Promise<Uint8Array> {
  const contentLength = incoming.headers["content-length"];
  const declaredLength = typeof contentLength === "string" ? Number(contentLength) : undefined;
  if (declaredLength !== undefined && Number.isFinite(declaredLength) && declaredLength > MAX_REQUEST_BODY_BYTES) {
    incoming.resume();
    throw new RequestBodyTooLargeError();
  }

  return new Promise<Uint8Array>((resolve, reject) => {
    const chunks: Uint8Array[] = [];
    let size = 0;
    let settled = false;

    const cleanup = (): void => {
      incoming.off("data", onData);
      incoming.off("end", onEnd);
      incoming.off("error", onError);
      incoming.off("aborted", onAborted);
      signal.removeEventListener("abort", onSignalAbort);
    };
    const finish = (error?: Error): void => {
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
    const onData = (chunk: Uint8Array | string): void => {
      const bytes = typeof chunk === "string" ? new TextEncoder().encode(chunk) : chunk;
      if (size + bytes.byteLength > MAX_REQUEST_BODY_BYTES) {
        incoming.resume();
        finish(new RequestBodyTooLargeError());
        return;
      }
      chunks.push(bytes);
      size += bytes.byteLength;
    };
    const onEnd = (): void => finish();
    const onError = (error: Error): void => finish(error);
    const onAborted = (): void => finish(new Error("Request aborted."));
    const onSignalAbort = (): void => finish(new Error("Request aborted."));

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

async function writeResponse(
  outgoing: ServerResponse,
  response: Response,
  method: string,
  signal: AbortSignal,
): Promise<void> {
  for (const [name, value] of response.headers) {
    outgoing.setHeader(name, value);
  }

  const getSetCookie = (response.headers as Headers & { getSetCookie?: () => string[] }).getSetCookie;
  if (getSetCookie !== undefined) {
    const cookies = getSetCookie.call(response.headers);
    if (cookies.length > 0) {
      outgoing.setHeader("set-cookie", cookies);
    }
  }
  outgoing.writeHead(response.status);

  if (method === "HEAD" || response.body === null || response.status === 204 || response.status === 205 || response.status === 304) {
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

function writeChunk(outgoing: ServerResponse, chunk: Uint8Array, signal: AbortSignal): Promise<void> {
  if (signal.aborted || outgoing.destroyed) {
    return Promise.reject(new Error("Response aborted."));
  }
  if (outgoing.write(chunk)) {
    return Promise.resolve();
  }
  return new Promise<void>((resolve, reject) => {
    const cleanup = (): void => {
      outgoing.off("drain", onDrain);
      outgoing.off("close", onClose);
      outgoing.off("error", onError);
      signal.removeEventListener("abort", onAbort);
    };
    const onDrain = (): void => {
      cleanup();
      resolve();
    };
    const onClose = (): void => {
      cleanup();
      reject(new Error("Response closed."));
    };
    const onError = (error: Error): void => {
      cleanup();
      reject(error);
    };
    const onAbort = (): void => {
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

async function awaitAbortable<T>(promise: Promise<T>, signal: AbortSignal): Promise<T> {
  if (signal.aborted) {
    throw new Error("Operation aborted.");
  }
  return new Promise<T>((resolve, reject) => {
    const onAbort = (): void => {
      cleanup();
      reject(new Error("Operation aborted."));
    };
    const cleanup = (): void => signal.removeEventListener("abort", onAbort);
    signal.addEventListener("abort", onAbort, { once: true });
    promise.then(
      (value) => {
        cleanup();
        resolve(value);
      },
      (error: unknown) => {
        cleanup();
        reject(error);
      },
    );
  });
}

function sendErrorResponse(outgoing: ServerResponse, status: number, message: string): void {
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

function closeNodeServer(server: ServerLike): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    server.close((error) => {
      if (error !== undefined && (error as NodeJS.ErrnoException).code !== "ERR_SERVER_NOT_RUNNING") {
        reject(error);
        return;
      }
      resolve();
    });
  });
}

class RequestBodyTooLargeError extends Error {}
class BadRequestError extends Error {}
