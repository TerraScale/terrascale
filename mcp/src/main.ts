#!/usr/bin/env node
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { readHttpConfig, readHttpListenConfig, readHttpSessionLimits, readStdioConfig } from "./config.js";
import { createHttpMcpServer } from "./http.js";
import { listen } from "./listen.js";
import { createStructuredLogger } from "./logger.js";
import { createMcpServer } from "./server.js";

const logger = createStructuredLogger();
const transport = process.env.TERRASCALE_MCP_TRANSPORT ?? "stdio";

try {
  if (transport === "stdio") {
    await createMcpServer(readStdioConfig(process.env), logger).connect(new StdioServerTransport(undefined, undefined, { maxBufferSize: 1_048_576 }));
  } else if (transport === "http") {
    const listenConfig = readHttpListenConfig(process.env);
    const server = createHttpMcpServer(readHttpConfig(process.env), logger, { ...readHttpSessionLimits(process.env), allowedHosts: listenConfig.allowedHosts, allowedOrigins: listenConfig.allowedOrigins });
    const port = readPort(process.env.TERRASCALE_MCP_PORT);
    await listen(server, port, listenConfig.host, logger);
    logger.info("mcp.http.listening", { port });
  } else {
    throw new Error("transport_invalid");
  }
} catch (error) {
  logger.error("mcp.startup.failed", { code: error instanceof Error ? error.message : "startup_failed" });
  process.exitCode = 1;
}

function readPort(value: string | undefined): number {
  const port = value === undefined ? 3000 : Number(value);
  if (!Number.isInteger(port) || port < 1 || port > 65_535) {
    throw new Error("port_invalid");
  }
  return port;
}
