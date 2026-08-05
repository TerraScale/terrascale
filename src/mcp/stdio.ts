import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { loadMcpConfig } from "./config.js";
import { createTerraScaleMcpServer } from "./server.js";

const server = createTerraScaleMcpServer(loadMcpConfig());
await server.connect(new StdioServerTransport());
