import asyncio
import resource
import sys

soft, hard = resource.getrlimit(resource.RLIMIT_NOFILE)
resource.setrlimit(resource.RLIMIT_NOFILE, (min(8192, hard), hard))


async def pipe(reader, writer):
    try:
        while data := await reader.read(65536):
            writer.write(data)
            await writer.drain()
    finally:
        writer.close()


async def connect(reader, writer):
    upstream = None
    try:
        header = await asyncio.wait_for(reader.readuntil(b"\r\n\r\n"), 10)
        method, target, protocol = header.split(b"\r\n", 1)[0].decode("ascii").split()
        if method != "CONNECT" or target not in (
            "api.terrascale.tech:443",
            "shapes-ts-terrabase-shapes-prod.terrascale.tech:443",
        ):
            writer.write(b"HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n")
            await writer.drain()
            return
        host, port = target.rsplit(":", 1)
        remote, upstream = await asyncio.wait_for(asyncio.open_connection(host, int(port)), 10)
        writer.write(b"HTTP/1.1 200 Connection Established\r\n\r\n")
        await writer.drain()
        await asyncio.gather(pipe(reader, upstream), pipe(remote, writer))
    except (OSError, ValueError, asyncio.TimeoutError, asyncio.IncompleteReadError):
        pass
    finally:
        writer.close()
        if upstream is not None:
            upstream.close()


async def main():
    server = await asyncio.start_server(connect, "127.0.0.1", 0, backlog=2048)
    print(server.sockets[0].getsockname()[1], flush=True)
    async with server:
        await asyncio.get_running_loop().run_in_executor(None, sys.stdin.buffer.read)


asyncio.run(main())
