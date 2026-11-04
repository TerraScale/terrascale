import { createHash } from "node:crypto";
import { createServer } from "node:http";

/**
 * @typedef {object} SqlSocketPeer
 * @property {(value: unknown) => void} send
 * @property {(text: string) => void} sendText
 * @property {(code: number, reason?: string) => void} close
 */
/** @typedef {(frame: any, peer: SqlSocketPeer) => void} SqlSocketHandler */

/** @param {number} opcode @param {Buffer} payload */
function encodeFrame(opcode, payload) {
  const length = payload.length;
  const header = length < 126 ? Buffer.from([0x80 | opcode, length])
    : length < 65_536 ? Buffer.from([0x80 | opcode, 126, length >> 8, length & 255])
      : Buffer.concat([Buffer.from([0x80 | opcode, 127]), (() => { const size = Buffer.alloc(8); size.writeBigUInt64BE(BigInt(length)); return size; })()]);
  return Buffer.concat([header, payload]);
}

/**
 * A loopback WebSocket server that speaks terrascale.sql.v1 frames, for Node and browser fixture tests.
 * @param {SqlSocketHandler} handle
 * @param {{ protocol?: string | null; status?: number }} [options]
 */
export async function startSqlSocketServer(handle, options = {}) {
  /** @type {import("node:stream").Duplex[]} */
  const sockets = [];
  /** @type {{ url: URL; headers: import("node:http").IncomingHttpHeaders }[]} */
  const upgrades = [];
  /** @type {any[]} */
  const frames = [];
  const closes = /** @type {number[]} */ ([]);
  const server = createServer((_request, response) => response.writeHead(400).end());
  server.on("upgrade", (request, socket) => {
    sockets.push(socket);
    upgrades.push({ url: new URL(request.url ?? "/", "http://localhost"), headers: request.headers });
    if (options.status !== undefined) {
      socket.end(`HTTP/1.1 ${options.status} Refused\r\ncontent-type: application/json\r\ncontent-length: 2\r\n\r\n{}`);
      return;
    }
    const accept = createHash("sha1").update(`${request.headers["sec-websocket-key"]}258EAFA5-E914-47DA-95CA-C5AB0DC85B11`).digest("base64");
    const protocol = options.protocol === undefined ? "terrascale.sql.v1" : options.protocol;
    socket.write(["HTTP/1.1 101 Switching Protocols", "Upgrade: websocket", "Connection: Upgrade", `Sec-WebSocket-Accept: ${accept}`,
      ...(protocol === null ? [] : [`Sec-WebSocket-Protocol: ${protocol}`]), "", ""].join("\r\n"));
    /** @type {SqlSocketPeer} */
    const peer = {
      send: value => socket.write(encodeFrame(1, Buffer.from(JSON.stringify(value)))),
      sendText: text => socket.write(encodeFrame(1, Buffer.from(text))),
      close: (code, reason = "") => {
        const payload = Buffer.alloc(2 + Buffer.byteLength(reason));
        payload.writeUInt16BE(code);
        payload.write(reason, 2);
        socket.end(encodeFrame(8, payload));
      },
    };
    let buffered = Buffer.alloc(0);
    socket.on("data", chunk => {
      buffered = Buffer.concat([buffered, chunk]);
      while (buffered.length >= 2) {
        const opcode = (buffered[0] ?? 0) & 15;
        let length = (buffered[1] ?? 0) & 127;
        let offset = 2;
        if (length === 126) { length = buffered.readUInt16BE(2); offset = 4; }
        else if (length === 127) { length = Number(buffered.readBigUInt64BE(2)); offset = 10; }
        if (buffered.length < offset + 4 + length) return;
        const mask = buffered.subarray(offset, offset + 4);
        const payload = Buffer.from(buffered.subarray(offset + 4, offset + 4 + length).map((byte, index) => byte ^ (mask[index % 4] ?? 0)));
        buffered = buffered.subarray(offset + 4 + length);
        if (opcode === 8) {
          closes.push(payload.length >= 2 ? payload.readUInt16BE(0) : 1005);
          socket.end(encodeFrame(8, payload.subarray(0, 2)));
          return;
        }
        if (opcode === 1) {
          const frame = JSON.parse(payload.toString("utf8"));
          frames.push(frame);
          handle(frame, peer);
        }
      }
    });
    socket.on("error", () => undefined);
  });
  await new Promise((resolve, reject) => { server.once("error", reject); server.listen(0, "127.0.0.1", () => resolve(undefined)); });
  const address = server.address();
  if (address === null || typeof address === "string") throw new Error("Expected loopback address.");
  const baseUrl = `http://127.0.0.1:${address.port}`;
  const stop = async () => {
    for (const socket of sockets.splice(0)) socket.destroy();
    server.closeAllConnections();
    await new Promise(resolve => server.close(() => resolve(undefined)));
  };
  return { baseUrl, upgrades, frames, closes, stop };
}
