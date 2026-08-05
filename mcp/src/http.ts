import { createHash, randomUUID, timingSafeEqual } from "node:crypto";
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import type { Transport } from "@modelcontextprotocol/sdk/shared/transport.js";
import { callerAccessToken, type McpHttpConfig } from "./config.js";
import type { McpLogger } from "./logger.js";
import { createMcpServer } from "./server.js";

const maxRequestBytes = 1_048_576;
type Limits = { bodyTimeoutMs: number; headersTimeoutMs: number; requestTimeoutMs: number; keepAliveTimeoutMs: number; initializeTimeoutMs: number; maxSessions: number; sessionIdleTimeoutMs: number; sessionTtlMs: number };
const defaults: Limits = { bodyTimeoutMs: 10_000, headersTimeoutMs: 10_000, requestTimeoutMs: 15_000, keepAliveTimeoutMs: 5_000, initializeTimeoutMs: 15_000, maxSessions: 100, sessionIdleTimeoutMs: 15 * 60_000, sessionTtlMs: 60 * 60_000 };

export type McpHttpServerOptions = Partial<Limits> & {
  readonly allowedHosts?: readonly string[];
  readonly allowedOrigins?: readonly string[];
};

type Session = { readonly transport: StreamableHTTPServerTransport; readonly credentialHash: Buffer; idleTimer: NodeJS.Timeout; readonly ttlTimer: NodeJS.Timeout; activeWork: number; closed: boolean };
type Pending = { transport?: StreamableHTTPServerTransport; timer: NodeJS.Timeout; settled: boolean };
type NewSession = { readonly transport: StreamableHTTPServerTransport; readonly pending: Pending };

export function createHttpMcpServer(config: McpHttpConfig, logger: McpLogger, options: McpHttpServerOptions = {}): Server {
  const limits = { ...defaults, ...options };
  const allowedHosts = options.allowedHosts ?? [];
  const allowedOrigins = options.allowedOrigins ?? [];
  const registry = new SessionRegistry(limits);
  const server = createServer(async (request, response) => {
    if (request.url === "/healthz") return send(response, 200, "ok");
    if (request.url === "/readyz") return send(response, 200, "ready");
    if (request.url !== "/mcp") return send(response, 404, "not found");
    if (!isAllowed(request, allowedHosts, allowedOrigins)) return send(response, 403, "request denied");
    if (request.method !== "GET" && request.method !== "POST" && request.method !== "DELETE") return send(response, 405, "method not allowed");

    let created: NewSession | undefined;
    try {
      const sessionId = header(request, "mcp-session-id");
      if (sessionId === undefined && request.method === "POST") {
        created = await createUninitializedSession(request, response, config, logger, registry, allowedHosts, allowedOrigins);
      }
      const session = created === undefined
        ? sessionId === undefined ? undefined : registry.find(request, sessionId)
        : { transport: created.transport };
      if (session === undefined) return send(response, sessionId === undefined ? 400 : 404, "session unavailable");

      if (sessionId !== undefined) {
        const releaseWork = registry.beginWork(sessionId);
        response.once("close", releaseWork);
      }

      const body = request.method === "POST" ? await readJson(request, response, limits.bodyTimeoutMs) : undefined;
      await session.transport.handleRequest(request, response, body);
      if (created !== undefined && created.transport.sessionId === undefined) {
        await registry.releasePending(created.pending);
      }
    } catch {
      await registry.releasePending(created?.pending);
      logger.warn("mcp.http.rejected", { method: request.method ?? "unknown" });
      if (!response.headersSent) send(response, 400, "invalid request");
      else response.end();
    }
  });
  server.headersTimeout = limits.headersTimeoutMs;
  server.requestTimeout = limits.requestTimeoutMs;
  server.keepAliveTimeout = limits.keepAliveTimeoutMs;
  server.timeout = 0;
  server.on("close", () => { void registry.closeAll(); });
  return server;
}

async function createUninitializedSession(
  request: IncomingMessage,
  response: ServerResponse,
  config: McpHttpConfig,
  logger: McpLogger,
  registry: SessionRegistry,
  allowedHosts: readonly string[],
  allowedOrigins: readonly string[],
): Promise<NewSession | undefined> {
  const accessToken = callerAccessToken(header(request, "authorization"));
  if (accessToken === undefined || !registry.canReserve()) return undefined;

  const pending = registry.reserve(request, response);
  const credentialHash = hash(accessToken);
  let transport: StreamableHTTPServerTransport | undefined;
  transport = new StreamableHTTPServerTransport({
    sessionIdGenerator: randomUUID,
    allowedHosts: [...allowedHosts],
    allowedOrigins: [...allowedOrigins],
    enableDnsRebindingProtection: true,
    onsessioninitialized: (sessionId): void => {
      if (transport !== undefined) registry.promote(pending, sessionId, transport, credentialHash);
    },
    onsessionclosed: (sessionId): void => { void registry.close(sessionId); },
  });
  pending.transport = transport;
  transport.onclose = () => {
    if (transport.sessionId !== undefined) void registry.close(transport.sessionId, false);
  };
  await createMcpServer(config, logger, (extra) => callerAccessToken(requestAuthorization(extra.requestInfo?.headers.authorization))).connect(transport as unknown as Transport);
  return { transport, pending };
}

class SessionRegistry {
  readonly #sessions = new Map<string, Session>();
  readonly #pending = new Set<Pending>();
  readonly #limits: Limits;

  constructor(limits: Limits) {
    this.#limits = limits;
  }

  canReserve(): boolean {
    return this.#sessions.size + this.#pending.size < this.#limits.maxSessions;
  }

  reserve(request: IncomingMessage, response: ServerResponse): Pending {
    const pending: Pending = { timer: undefined as unknown as NodeJS.Timeout, settled: false };
    pending.timer = setTimeout(() => {
      if (!this.#pending.delete(pending) || pending.settled) return;
      pending.settled = true;
      if (!response.headersSent) send(response, 408, "request timeout");
      request.socket.destroy();
      void pending.transport?.close();
    }, this.#limits.initializeTimeoutMs);
    this.#pending.add(pending);
    return pending;
  }

  promote(pending: Pending, sessionId: string, transport: StreamableHTTPServerTransport, credentialHash: Buffer): void {
    this.#settle(pending);
    if (this.#sessions.size >= this.#limits.maxSessions) {
      void transport.close();
      return;
    }
    const session = { transport, credentialHash, idleTimer: undefined as unknown as NodeJS.Timeout, ttlTimer: undefined as unknown as NodeJS.Timeout, activeWork: 0, closed: false };
    session.idleTimer = this.#idleTimer(sessionId);
    session.ttlTimer = setTimeout(() => { void this.close(sessionId); }, this.#limits.sessionTtlMs);
    this.#sessions.set(sessionId, session);
  }

  find(request: IncomingMessage, sessionId: string): Session | undefined {
    const session = this.#sessions.get(sessionId);
    const accessToken = callerAccessToken(header(request, "authorization"));
    if (session === undefined || accessToken === undefined || !sameHash(session.credentialHash, hash(accessToken))) return undefined;
    this.#touch(sessionId, session);
    return session;
  }

  beginWork(sessionId: string): () => void {
    const session = this.#sessions.get(sessionId);
    if (session === undefined || session.closed) return () => undefined;
    session.activeWork += 1;
    clearTimeout(session.idleTimer);
    let released = false;
    return () => {
      if (released) return;
      released = true;
      session.activeWork -= 1;
      this.#touch(sessionId, session);
    };
  }

  async releasePending(pending: Pending | undefined): Promise<void> {
    if (pending === undefined) return;
    this.#settle(pending);
    await pending.transport?.close();
  }

  async close(sessionId: string, closeTransport = true): Promise<void> {
    const session = this.#sessions.get(sessionId);
    if (session === undefined || session.closed) return;
    session.closed = true;
    this.#sessions.delete(sessionId);
    clearTimeout(session.idleTimer);
    clearTimeout(session.ttlTimer);
    if (closeTransport) await session.transport.close();
  }

  async closeAll(): Promise<void> {
    await Promise.all([...this.#sessions.keys()].map((sessionId) => this.close(sessionId)));
    await Promise.all([...this.#pending].map((pending) => this.releasePending(pending)));
  }

  #settle(pending: Pending): void {
    if (!pending.settled) {
      pending.settled = true;
      this.#pending.delete(pending);
      clearTimeout(pending.timer);
    }
  }

  #touch(sessionId: string, session: Session): void {
    clearTimeout(session.idleTimer);
    if (session.activeWork === 0 && !session.closed) session.idleTimer = this.#idleTimer(sessionId);
  }

  #idleTimer(sessionId: string): NodeJS.Timeout {
    return setTimeout(() => { void this.close(sessionId); }, this.#limits.sessionIdleTimeoutMs);
  }
}

async function readJson(request: IncomingMessage, response: ServerResponse, timeoutMs: number): Promise<unknown> {
  let timedOut = false;
  const timer = setTimeout(() => {
    timedOut = true;
    if (!response.headersSent) send(response, 408, "request timeout");
    request.socket.destroy();
  }, timeoutMs);
  const chunks: Buffer[] = [];
  let size = 0;
  try {
    for await (const chunk of request) {
      const bytes = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
      size += bytes.length;
      if (size > maxRequestBytes) throw new Error("request_too_large");
      chunks.push(bytes);
    }
  } finally {
    clearTimeout(timer);
  }
  if (timedOut) throw new Error("request_timeout");
  const source = Buffer.concat(chunks).toString("utf8");
  return source.length === 0 ? undefined : JSON.parse(source) as unknown;
}

function isAllowed(request: IncomingMessage, allowedHosts: readonly string[], allowedOrigins: readonly string[]): boolean {
  const host = header(request, "host");
  const origin = header(request, "origin");
  return (allowedHosts.length === 0 || (host !== undefined && allowedHosts.includes(host))) &&
    (allowedOrigins.length === 0 || origin === undefined || allowedOrigins.includes(origin));
}

function header(request: IncomingMessage, name: string): string | undefined {
  const value = request.headers[name];
  return typeof value === "string" ? value : undefined;
}

function requestAuthorization(value: string | readonly string[] | undefined): string | undefined {
  return typeof value === "string" ? value : undefined;
}

function hash(value: string): Buffer {
  return createHash("sha256").update(value).digest();
}

function sameHash(left: Buffer, right: Buffer): boolean {
  return left.length === right.length && timingSafeEqual(left, right);
}

function send(response: ServerResponse, statusCode: number, text: string): void {
  response.writeHead(statusCode, { "content-type": "text/plain; charset=utf-8", "cache-control": "no-store", connection: "close" });
  response.end(text);
}
