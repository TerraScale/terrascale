import { createHash, randomUUID, timingSafeEqual } from "node:crypto";
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import type { Transport } from "@modelcontextprotocol/sdk/shared/transport.js";
import { callerAccessToken, type McpHttpConfig, type McpRuntimeConfig } from "./config.js";
import type { McpLogger } from "./logger.js";
import { createMcpServer } from "./server.js";

const maxRequestBytes = 1_048_576;
type McpHttpTimeouts = { bodyTimeoutMs: number; headersTimeoutMs: number; requestTimeoutMs: number; keepAliveTimeoutMs: number };
const defaultTimeouts: McpHttpTimeouts = { bodyTimeoutMs: 10_000, headersTimeoutMs: 10_000, requestTimeoutMs: 15_000, keepAliveTimeoutMs: 5_000 };

export type McpHttpServerOptions = Partial<McpHttpTimeouts>;

type Session = { readonly transport: StreamableHTTPServerTransport; readonly credentialHash: Buffer };

export function createHttpMcpServer(config: McpHttpConfig, logger: McpLogger, options: McpHttpServerOptions = {}): Server {
  const timeouts = { ...defaultTimeouts, ...options };
  const sessions = new Map<string, Session>();
  const server = createServer(async (request, response) => {
    if (request.url === "/healthz") {
      return send(response, 200, "ok");
    }
    if (request.url === "/readyz") {
      return send(response, 200, "ready");
    }
    if (request.url !== "/mcp") {
      return send(response, 404, "not found");
    }
    if (request.method !== "GET" && request.method !== "POST" && request.method !== "DELETE") {
      return send(response, 405, "method not allowed");
    }

    let transient: Session | undefined;
    try {
      const sessionId = header(request, "mcp-session-id");
      transient = sessionId === undefined ? await createUninitializedSession(request, config, logger, sessions) : undefined;
      const session = transient ?? (sessionId === undefined ? undefined : findSession(request, sessionId, sessions));
      if (session === undefined) {
        return send(response, sessionId === undefined ? 400 : 404, "session unavailable");
      }
      const body = request.method === "POST" ? await readJson(request, response, timeouts.bodyTimeoutMs) : undefined;
      await session.transport.handleRequest(request, response, body);
      if (sessionId === undefined && session.transport.sessionId === undefined) {
        await session.transport.close();
      }
    } catch {
      await transient?.transport.close();
      logger.warn("mcp.http.rejected", { method: request.method ?? "unknown" });
      if (!response.headersSent) {
        send(response, 400, "invalid request");
      } else {
        response.end();
      }
    }
  });
  server.headersTimeout = timeouts.headersTimeoutMs;
  server.requestTimeout = timeouts.requestTimeoutMs;
  server.keepAliveTimeout = timeouts.keepAliveTimeoutMs;
  server.timeout = timeouts.requestTimeoutMs;
  return server;
}

async function createUninitializedSession(
  request: IncomingMessage,
  config: McpHttpConfig,
  logger: McpLogger,
  sessions: Map<string, Session>,
): Promise<Session | undefined> {
  if (request.method !== "POST") {
    return undefined;
  }
  const accessToken = callerAccessToken(header(request, "authorization"));
  if (accessToken === undefined) {
    return undefined;
  }

  const credentialHash = hash(accessToken);
  let transport: StreamableHTTPServerTransport | undefined;
  transport = new StreamableHTTPServerTransport({
    sessionIdGenerator: randomUUID,
    onsessioninitialized: (sessionId): void => {
      if (transport !== undefined) {
        sessions.set(sessionId, { transport, credentialHash });
      }
    },
    onsessionclosed: (sessionId): void => { sessions.delete(sessionId); },
  });
  transport.onclose = () => {
    if (transport.sessionId !== undefined) {
      sessions.delete(transport.sessionId);
    }
  };
  const runtimeConfig: McpRuntimeConfig = { ...config, accessToken };
  await createMcpServer(runtimeConfig, logger).connect(transport as unknown as Transport);
  return { transport, credentialHash };
}

function findSession(request: IncomingMessage, sessionId: string, sessions: ReadonlyMap<string, Session>): Session | undefined {
  const session = sessions.get(sessionId);
  const accessToken = callerAccessToken(header(request, "authorization"));
  return session !== undefined && accessToken !== undefined && sameHash(session.credentialHash, hash(accessToken)) ? session : undefined;
}

async function readJson(request: IncomingMessage, response: ServerResponse, timeoutMs: number): Promise<unknown> {
  let timedOut = false;
  const timer = setTimeout(() => {
    timedOut = true;
    if (!response.headersSent) {
      send(response, 408, "request timeout");
    }
    request.socket.destroy();
  }, timeoutMs);
  const chunks: Buffer[] = [];
  let size = 0;
  try {
    for await (const chunk of request) {
      const bytes = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
      size += bytes.length;
      if (size > maxRequestBytes) {
        throw new Error("request_too_large");
      }
      chunks.push(bytes);
    }
  } finally {
    clearTimeout(timer);
  }
  if (timedOut) {
    throw new Error("request_timeout");
  }
  const source = Buffer.concat(chunks).toString("utf8");
  return source.length === 0 ? undefined : JSON.parse(source) as unknown;
}

function header(request: IncomingMessage, name: string): string | undefined {
  const value = request.headers[name];
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
