import selectors
import socket
import sys
import time

port, rate = map(int, sys.argv[1:])
with socket.socket() as listener, socket.socket() as upstream:
    upstream.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4096)
    upstream.connect(("127.0.0.1", port))
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)
    print(listener.getsockname()[1], flush=True)
    client, _ = listener.accept()
    with client, selectors.DefaultSelector() as ready:
        ready.register(client, selectors.EVENT_READ)
        ready.register(upstream, selectors.EVENT_READ)
        ready.register(sys.stdin, selectors.EVENT_READ)
        paced = False
        registered = True
        next_read = time.monotonic()
        while True:
            now = time.monotonic()
            if not registered and rate > 0 and now >= next_read:
                ready.register(upstream, selectors.EVENT_READ)
                registered = True
            timeout = max(0, next_read - now) if not registered and rate > 0 else None
            for event, _ in ready.select(timeout):
                source = event.fileobj
                if source is sys.stdin:
                    if not sys.stdin.readline():
                        sys.exit(0)
                    paced = True
                    next_read = time.monotonic()
                    if rate == 0 and registered:
                        ready.unregister(upstream)
                        registered = False
                    continue
                if source is upstream and paced and rate == 0:
                    continue
                payload = source.recv(4096)
                if not payload:
                    sys.exit(0)
                destination = upstream if source is client else client
                destination.sendall(payload)
                if source is upstream and paced:
                    next_read = time.monotonic() + len(payload) / rate
                    ready.unregister(upstream)
                    registered = False
