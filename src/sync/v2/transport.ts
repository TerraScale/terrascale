import {
  syncV2Protocol,
  TerraScaleSyncV2Client,
  type SyncV2Envelope,
  type SyncV2Message,
} from "./client.js";

export type SyncV2Fetch = (
  input: RequestInfo | URL,
  init?: RequestInit,
) => Promise<Response>;

export type SyncV2WebSocket = {
  binaryType: BinaryType;
  readonly readyState: number;
  onclose: ((event: CloseEvent) => void) | null;
  onerror: ((event: Event) => void) | null;
  onmessage: ((event: MessageEvent<unknown>) => void) | null;
  onopen: ((event: Event) => void) | null;
  close(code?: number, reason?: string): void;
  send(data: ArrayBuffer | ArrayBufferView): void;
};

export type SyncV2WebSocketFactory = (
  url: string,
  protocols: string | string[],
) => SyncV2WebSocket;

export type SyncV2TransportOptions = {
  readonly client: TerraScaleSyncV2Client;
  readonly endpoint: string | URL;
};

export type SyncV2WebSocketTransportOptions = SyncV2TransportOptions & {
  readonly webSocketFactory?: SyncV2WebSocketFactory;
};

export type SyncV2HttpTransportOptions = SyncV2TransportOptions & {
  readonly fetch?: SyncV2Fetch;
};

export type SyncV2TransportMode = "websocket" | "http";

const OPEN = 1;
const messageContentType = "application/msgpack";

/** A browser-native WebSocket transport for the generated Sync v2 frames. */
export class TerraScaleSyncV2WebSocketTransport {
  readonly #client: TerraScaleSyncV2Client;
  readonly #endpoint: string;
  readonly #factory: SyncV2WebSocketFactory;
  #socket: SyncV2WebSocket | undefined;
  #opening: Promise<void> | undefined;
  #closed = false;

  public constructor(options: SyncV2WebSocketTransportOptions) {
    this.#client = options.client;
    this.#endpoint = String(options.endpoint);
    this.#factory = options.webSocketFactory ?? defaultWebSocketFactory;
  }

  public async connect(): Promise<void> {
    if (this.#closed) throw new Error("the Sync v2 transport is closed");
    if (this.#socket?.readyState === OPEN) return;
    if (this.#opening !== undefined) return this.#opening;

    this.#opening = new Promise<void>((resolve, reject) => {
      let settled = false;
      let socket: SyncV2WebSocket;
      try {
        socket = this.#factory(this.#endpoint, syncV2Protocol);
      } catch (error) {
        reject(error);
        return;
      }
      this.#socket = socket;
      socket.binaryType = "arraybuffer";
      socket.onopen = () => {
        settled = true;
        this.#client.setOnline(true);
        resolve();
      };
      socket.onmessage = event => {
        void this.#receive(event.data).catch(() => {
          socket.close(1002, "invalid Sync v2 frame");
        });
      };
      socket.onerror = () => {
        if (!settled) reject(new Error("the Sync v2 WebSocket failed to open"));
      };
      socket.onclose = event => {
        this.#client.setOnline(false);
        this.#socket = undefined;
        if (!settled) reject(new Error(`the Sync v2 WebSocket closed (${event.code})`));
      };
    }).finally(() => {
      this.#opening = undefined;
    });
    return this.#opening;
  }

  public send(message: SyncV2Message, correlation: bigint, causation = 0n): void {
    const socket = this.#socket;
    if (socket === undefined || socket.readyState !== OPEN) {
      throw new Error("the Sync v2 WebSocket is not open");
    }
    socket.send(this.#client.encode(message, correlation, causation));
  }

  public close(): void {
    this.#closed = true;
    this.#client.setOnline(false);
    this.#socket?.close(1000, "client closed");
    this.#socket = undefined;
  }

  async #receive(data: unknown): Promise<SyncV2Envelope> {
    return this.#client.receive(await bytesFromMessage(data));
  }
}

/**
 * A request/response HTTP fallback. Each call carries exactly one generated
 * MessagePack frame and consumes one binary response frame, if present.
 */
export class TerraScaleSyncV2HttpTransport {
  readonly #client: TerraScaleSyncV2Client;
  readonly #endpoint: string;
  readonly #fetch: SyncV2Fetch;
  #closed = false;

  public constructor(options: SyncV2HttpTransportOptions) {
    this.#client = options.client;
    this.#endpoint = String(options.endpoint);
    this.#fetch = options.fetch ?? defaultFetch;
  }

  public connect(): void {
    if (this.#closed) throw new Error("the Sync v2 transport is closed");
    this.#client.setOnline(true);
  }

  public async send(message: SyncV2Message, correlation: bigint, causation = 0n): Promise<SyncV2Envelope | undefined> {
    if (this.#closed) throw new Error("the Sync v2 transport is closed");
    const frame = this.#client.encode(message, correlation, causation);
    const response = await this.#fetch(this.#endpoint, {
      method: "POST",
      headers: {
        Accept: messageContentType,
        "Content-Type": messageContentType,
      },
      body: frame as unknown as BodyInit,
    });
    if (!response.ok) {
      this.#client.setOnline(false);
      throw new Error(`the Sync v2 HTTP fallback failed (${response.status})`);
    }
    const body = new Uint8Array(await response.arrayBuffer());
    return body.byteLength === 0 ? undefined : this.#client.receive(body);
  }

  public close(): void {
    this.#closed = true;
    this.#client.setOnline(false);
  }
}

/** Uses WebSocket when available and falls back to HTTP+MessagePack on setup/send failure. */
export class TerraScaleSyncV2Transport {
  readonly #webSocket: TerraScaleSyncV2WebSocketTransport;
  readonly #http: TerraScaleSyncV2HttpTransport;
  #active: SyncV2TransportMode | undefined;

  public constructor(options: SyncV2WebSocketTransportOptions & SyncV2HttpTransportOptions) {
    this.#webSocket = new TerraScaleSyncV2WebSocketTransport(options);
    this.#http = new TerraScaleSyncV2HttpTransport(options);
  }

  public get mode(): SyncV2TransportMode | undefined {
    return this.#active;
  }

  public async connect(): Promise<void> {
    try {
      await this.#webSocket.connect();
      this.#active = "websocket";
    } catch {
      this.#webSocket.close();
      this.#http.connect();
      this.#active = "http";
    }
  }

  public async send(message: SyncV2Message, correlation: bigint, causation = 0n): Promise<SyncV2Envelope | undefined> {
    if (this.#active === "http") return this.#http.send(message, correlation, causation);
    if (this.#active !== "websocket") throw new Error("the Sync v2 transport is not connected");
    try {
      this.#webSocket.send(message, correlation, causation);
      return undefined;
    } catch {
      this.#webSocket.close();
      this.#http.connect();
      this.#active = "http";
      return this.#http.send(message, correlation, causation);
    }
  }

  public close(): void {
    this.#webSocket.close();
    this.#http.close();
    this.#active = undefined;
  }
}

function defaultWebSocketFactory(url: string, protocols: string | string[]): SyncV2WebSocket {
  if (typeof globalThis.WebSocket !== "function") {
    throw new Error("WebSocket is unavailable; use the HTTP+MessagePack fallback");
  }
  return new globalThis.WebSocket(url, protocols) as unknown as SyncV2WebSocket;
}

function defaultFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  if (typeof globalThis.fetch !== "function") {
    return Promise.reject(new Error("fetch is unavailable; use a fetch implementation"));
  }
  return globalThis.fetch(input, init);
}

async function bytesFromMessage(data: unknown): Promise<Uint8Array> {
  if (data instanceof Uint8Array) return new Uint8Array(data);
  if (data instanceof ArrayBuffer) return new Uint8Array(data);
  if (typeof Blob !== "undefined" && data instanceof Blob) return new Uint8Array(await data.arrayBuffer());
  throw new TypeError("Sync v2 WebSocket messages must be binary");
}
