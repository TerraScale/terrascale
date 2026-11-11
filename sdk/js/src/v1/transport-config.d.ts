export {};

declare module "./transport.js" {
  export interface TerraBaseV1ClientConfig {
    project: string;
    apiKey?: string | undefined;
    token?: string | (() => string | Promise<string>) | undefined;
    baseUrl?: string | undefined;
    authBaseUrl?: string | undefined;
    region?: string;
    endpointResolver?: ((context: {
      readonly project: string | undefined;
      readonly database?: string | undefined;
      readonly signal?: AbortSignal | undefined;
    }) => string | Promise<string>) | undefined;
    fetch?: import("../config.js").FetchLike | undefined;
    WebSocket?: (new (
      url: string,
      init: string[] | { protocols: string[]; headers: Record<string, string> },
    ) => import("./sql-socket.js").SqlSocketLike) | undefined;
    maxResponseBytes?: number | undefined;
  }
}
