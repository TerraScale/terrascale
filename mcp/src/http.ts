import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import { randomUUID } from "node:crypto";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import type { Transport } from "@modelcontextprotocol/sdk/shared/transport.js";
import type { McpHttpConfig, McpRuntimeConfig } from "./config.js";
import { callerAccessToken } from "./config.js";
import type { McpLogger } from "./logger.js";
import { createMcpServer } from "./server.js";

const maxRequestBytes = 1_048_576;

export function createHttpMcpServer(config: McpHttpConfig, logger: McpLogger): Server {
  const sessions = new Map<string, StreamableHTTPServerTransport>();
  return createServer(async (request, response) => {
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

    try {
      const sessionId = header(request, "mcp-session-id");
      const transport = sessionId === undefined
        ? await createSession(request, config, logger, sessions)
        : sessions.get(sessionId);
      if (transport === undefined) {
        return send(response, 404, "unknown session");
      }
      const body = request.method === "POST" ? await readJson(request) : undefined;
      await transport.handleRequest(request, response, body);
    } catch {
      logger.warn("mcp.http.rejected", { method: request.method ?? "unknown" });
      if (!response.headersSent) {
        send(response, 400, "invalid request");
      } else {
        response.end();
      }
    }
  });
}

async function createSession(
  request: IncomingMessage,
  config: McpHttpConfig,
  logger: McpLogger,
  sessions: Map<string, StreamableHTTPServerTransport>,
): Promise<StreamableHTTPServerTransport | undefined> {
  if (request.method !== "POST") {
    return undefined;
  }

  const accessToken = callerAccessToken(header(request, "authorization"));
  if (accessToken === undefined) {
    return undefined;
  }

  const transport = new StreamableHTTPServerTransport({ sessionIdGenerator: randomUUID });
  transport.onclose = () => {
    if (transport.sessionId !== undefined) {
      sessions.delete(transport.sessionId);
    }
  };
  const runtimeConfig: McpRuntimeConfig = { ...config, accessToken };
  await createMcpServer(runtimeConfig, logger).connect(transport as unknown as Transport);
  if (transport.sessionId !== undefined) {
    sessions.set(transport.sessionId, transport);
  }
  return transport;
}

function header(request: IncomingMessage, name: string): string | undefined {
  const value = request.headers[name];
  return typeof value === "string" ? value : undefined;
}

async function readJson(request: IncomingMessage): Promise<unknown> {
  const chunks: Buffer[] = [];
  let size = 0;
  for await (const chunk of request) {
    const bytes = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
    size += bytes.length;
    if (size > maxRequestBytes) {
      throw new Error("request_too_large");
    }
    chunks.push(bytes);
  }
  const source = Buffer.concat(chunks).toString("utf8");
  return source.length === 0 ? undefined : JSON.parse(source) as unknown;
}

function send(response: ServerResponse, statusCode: number, text: string): void {
  response.writeHead(statusCode, { "content-type": "text/plain; charset=utf-8", "cache-control": "no-store" });
  response.end(text);
}
