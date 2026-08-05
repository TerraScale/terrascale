import type { Server } from "node:http";
import type { McpLogger } from "./logger.js";

export async function listen(server: Server, port: number, host: string, logger: McpLogger): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    const failed = (error: Error & { readonly code?: string }) => {
      cleanup();
      logger.error("mcp.http.listen_failed", { code: error.code ?? "listen_failed" });
      reject(new McpListenError(error.code ?? "listen_failed"));
    };
    const listening = () => {
      cleanup();
      resolve();
    };
    const cleanup = () => {
      server.off("error", failed);
      server.off("listening", listening);
    };
    server.once("error", failed);
    server.once("listening", listening);
    try {
      server.listen(port, host);
    } catch (error) {
      failed(error instanceof Error ? error : new Error("listen_failed"));
    }
  });
}

export class McpListenError extends Error {
  readonly code: string;

  constructor(code: string) {
    super(code);
    this.code = code;
  }
}
