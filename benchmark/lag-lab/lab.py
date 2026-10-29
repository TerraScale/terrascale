import base64
import ctypes as C
import gzip
import hashlib
import http.server
import json
import os
from pathlib import Path
import queue
import signal
import socket
import socketserver
import subprocess as S
import sys
import threading
import time

ROOT = Path(__file__).resolve().parent
now = time.monotonic_ns
schema = 'CREATE TABLE items(id TEXT PRIMARY KEY, seq INTEGER, value TEXT)'
def dump(path, obj):
    temporary = Path(str(path) + '.tmp')
    temporary.write_text(json.dumps(obj, separators=(',', ':')))
    temporary.replace(path)
def row(i):
    alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'
    return [''.join(alphabet[((i + 1_000_000) >> (5 * j)) & 31] for j in reversed(range(26))), i, f'{i:08d}' + 'x' * 248]
def digest(rows):
    return hashlib.sha256(json.dumps(rows, separators=(',', ':')).encode()).hexdigest()

class Native:
    def __init__(self, directory, synchronous):
        directory.mkdir(exist_ok=True)
        S.run(['chattr', '+C', str(directory)], check=True, capture_output=True)
        self.lib = C.CDLL(str(ROOT / 'turso/target/lib-release/libts_sync_host.so'))
        self.lib.ts_sync_new.argtypes = [C.c_char_p, C.c_size_t, C.c_bool]
        self.lib.ts_sync_new.restype = C.c_void_p
        self.lib.ts_sync_request.argtypes = [C.c_void_p, C.c_char_p, C.c_bool, C.c_char_p, C.c_size_t, C.POINTER(C.c_void_p), C.POINTER(C.c_size_t)]
        self.lib.ts_sync_request.restype = C.c_uint16
        self.lib.ts_sync_free.argtypes = [C.c_void_p, C.c_size_t]
        self.host = self.lib.ts_sync_new(os.fsencode(directory), 4, False)
        assert self.host
        self.lock = threading.Condition(threading.RLock())
        self.generation = 0
        self.sql(schema)
        self.sql('PRAGMA synchronous=' + str(synchronous))
        self.settings = {p: self.sql('PRAGMA ' + p) for p in ('journal_mode', 'synchronous')}
    def request(self, body, pipeline):
        out, size = C.c_void_p(), C.c_size_t()
        status = self.lib.ts_sync_request(self.host, b'lab', pipeline, body, len(body), C.byref(out), C.byref(size))
        try:
            data = C.string_at(out, size.value)
        finally:
            self.lib.ts_sync_free(out, size.value)
        assert status == 200, (status, data)
        return data
    def sql(self, sql):
        with self.lock:
            result = json.loads(self.request(json.dumps({'requests': [{'type': 'execute', 'stmt': {'sql': sql}}]}).encode(), True))
            assert all(x['type'] != 'error' for x in result['results']), result
            return result['results'][0]['response']['result']
    def insert(self, r):
        with self.lock:
            self.sql(f"INSERT INTO items VALUES ('{r[0]}',{r[1]},'{r[2]}')")
            stamp = now()
            self.generation += 1
            self.lock.notify_all()
            return stamp
    def rows(self):
        return [[v['value'] if v['type'] != 'integer' else int(v['value']) for v in r] for r in self.sql('SELECT id,seq,value FROM items ORDER BY seq')['rows']]

def worker(role, mode, directory, rate, seconds, peer, synchronous):
    directory = Path(directory)
    db = Native(directory / (role + '-db'), synchronous) if role == 'source' or mode != 'direct' else None
    received, overlay = [], []
    class HTTP(http.server.BaseHTTPRequestHandler):
        protocol_version = 'HTTP/1.1'
        last_generation = -1
        def log_message(self, *args):
            pass
        def do_POST(self):
            self.connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            body = self.rfile.read(int(self.headers['Content-Length']))
            with db.lock:
                if self.path.endswith('pull-updates'):
                    db.lock.wait_for(lambda: db.generation != HTTP.last_generation, timeout=1)
                    data = db.request(body, False)
                    HTTP.last_generation = db.generation
                else:
                    data = db.request(body, True)
            self.send_response(200)
            self.send_header('Content-Length', str(len(data)))
            self.end_headers()
            self.wfile.write(data)
    class Stream(socketserver.StreamRequestHandler):
        def handle(self):
            self.request.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            for line in self.rfile:
                batch = json.loads(line)
                arrival = now()
                if batch is None:
                    rows = overlay if mode == 'direct' else db.rows()
                    dump(directory / 'destination.json', {'samples': [s for s in received if s[0] > 0], 'warmup_samples': [s for s in received if s[0] < 0], 'rows': len(rows), 'hash': digest(rows), 'settings': db.settings if db else None})
                    return
                for r in batch:
                    stamp = arrival if mode == 'direct' else db.insert(r)
                    if mode == 'direct':
                        overlay.append(r)
                    received.append([r[1], arrival, stamp])
    if db:
        threading.Thread(target=http.server.ThreadingHTTPServer(('0.0.0.0', 18981), HTTP).serve_forever, daemon=True).start()
    if role == 'destination':
        socketserver.TCPServer.allow_reuse_address = True
        server = socketserver.TCPServer(('0.0.0.0', 18982), Stream)
        dump(directory / 'destination-ready.json', {'clock': now()})
        server.serve_forever()
    dump(directory / 'source-ready.json', {'clock': now()})
    while not (directory / 'start').exists():
        time.sleep(.01)
    work, outgoing = queue.Queue(4096), queue.Queue(4096)
    samples, batches, failures = [], [], []
    connected = threading.Event()
    metrics = {'writer_queue_max': 0, 'stream_queue_max': 0, 'writer_enqueue_ns': 0, 'stream_enqueue_ns': 0, 'writer_queue_full': 0, 'stream_queue_full': 0}
    def send():
        try:
            with socket.create_connection((peer, 18982), timeout=60) as sock:
                sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                connected.set()
                while True:
                    item = outgoing.get()
                    if item is None:
                        sock.sendall(b'null\n')
                        return
                    batch, finished = [item], False
                    while len(batch) < 64 and not outgoing.empty():
                        extra = outgoing.get_nowait()
                        if extra is None:
                            finished = True
                            break
                        batch.append(extra)
                    before = now()
                    sock.sendall(json.dumps(batch, separators=(',', ':')).encode() + b'\n')
                    batches.append([batch[0][1], batch[-1][1], before, now(), len(batch)])
                    if finished:
                        sock.sendall(b'null\n')
                        return
        except BaseException as error:
            failures.append(repr(error))
    sender = threading.Thread(target=send, daemon=True) if mode != 'page' else None
    if sender:
        sender.start()
        assert connected.wait(30), failures
    def write():
        try:
            while True:
                item = work.get()
                if item is None:
                    return
                i, due = item
                r, begin = row(i), now()
                committed = db.insert(r)
                enqueued = now()
                if sender:
                    metrics['stream_queue_full'] += int(outgoing.full())
                    outgoing.put(r, timeout=30)
                    metrics['stream_enqueue_ns'] += now() - enqueued
                    metrics['stream_queue_max'] = max(metrics['stream_queue_max'], outgoing.qsize())
                samples.append([i, due, begin, committed, now()])
        except BaseException as error:
            failures.append(repr(error))
    writer = threading.Thread(target=write, daemon=True)
    writer.start()
    start = now() + 50_000_000
    for offset in range(-rate * 2, rate * seconds):
        i = offset if offset < 0 else offset + 1
        due = start + (offset + rate * 2) * 1_000_000_000 // rate
        time.sleep(max(0, (due - now()) / 1e9))
        before = now()
        metrics['writer_queue_full'] += int(work.full())
        work.put((i, due), timeout=30)
        metrics['writer_enqueue_ns'] += now() - before
        metrics['writer_queue_max'] = max(metrics['writer_queue_max'], work.qsize())
    work.put(None, timeout=30)
    writer.join(60)
    assert not writer.is_alive() and not failures, failures
    if sender:
        outgoing.put(None)
        sender.join(60)
        assert not sender.is_alive() and not failures, failures
    rows = db.rows()
    dump(directory / 'source.json', {'samples': [s for s in samples if s[0] > 0], 'warmup_samples': [s for s in samples if s[0] < 0], 'batches': batches, 'metrics': metrics, 'rows': len(rows), 'hash': digest(rows), 'planned_start_ns': start + 2_000_000_000, 'settings': db.settings, 'nice': os.getpriority(os.PRIO_PROCESS, 0)})
    threading.Event().wait()

def orchestrate(seconds, smoke=False, synchronous=2):
    result = ROOT / ('run-' + str(now()))
    result.mkdir()
    names = [f'll{os.getpid()}{c}' for c in 'abc']
    def cmd(*args):
        return S.check_output(args, text=True, stderr=S.STDOUT)
    def ns(index, *args):
        return ['ip', 'netns', 'exec', names[index], *args]
    processes, created = [], []
    root_network = {c: cmd(*c.split()) for c in ('ip -j link show', 'tc qdisc show')}
    evidence = {'started_utc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()), 'root_network_before': root_network, 'sources_sha256': {f.name: hashlib.sha256(f.read_bytes()).hexdigest() for f in ROOT.iterdir() if f.is_file()}, 'host': socket.gethostname(), 'uname': cmd('uname', '-a'), 'clock': time.get_clock_info('monotonic').__dict__, 'seconds': seconds, 'synchronous': synchronous, 'cases': []}
    measured_sources = ('lab.py', 'pull.mjs', 'bridge.rs', 'prepare.py', 'shim.toml', 'Cargo.lock.gz', 'package-lock.json', 'package.json', 'build.sh')
    evidence['source_snapshots_base64'] = {name: base64.b64encode((ROOT / name).read_bytes()).decode() for name in measured_sources}
    try:
        for name in names:
            cmd('ip', 'netns', 'add', name)
            created.append(name)
            cmd('ip', '-n', name, 'link', 'set', 'lo', 'up')
        for a, b, subnet in [(0, 1, 1), (1, 2, 2), (0, 2, 3)]:
            x, y = f'v{a}{b}', f'v{b}{a}'
            cmd('ip', '-n', names[a], 'link', 'add', x, 'type', 'veth', 'peer', 'name', y, 'netns', names[b])
            for index, interface, end in [(a, x, 1), (b, y, 2)]:
                cmd('ip', '-n', names[index], 'addr', 'add', f'10.207.{subnet}.{end}/24', 'dev', interface)
                cmd('ip', '-n', names[index], 'link', 'set', interface, 'up')
        bracket = now()
        clock_sample = int(cmd('node', '-e', 'process.stdout.write(String(process.hrtime.bigint()))'))
        evidence['clock_bracket'] = [bracket, clock_sample, now()]
        assert bracket <= clock_sample <= evidence['clock_bracket'][2]
        evidence['versions'] = {c: cmd(c, '--version') for c in ('node', 'python3')}
        evidence['native_sha256'] = hashlib.sha256((ROOT / 'turso/target/lib-release/libts_sync_host.so').read_bytes()).hexdigest()
        for delay in ((70,) if smoke else (70, 90)):
            for index, interfaces in enumerate([['v01', 'v02'], ['v10', 'v12'], ['v21', 'v20']]):
                for interface in interfaces:
                    cmd(*ns(index, 'tc', 'qdisc', 'replace', 'dev', interface, 'root', 'netem', 'delay', f'{delay}ms', 'limit', '10000'))
            rtt = cmd(*ns(0, 'ping', '-n', '-c', '10', '-i', '.2', '10.207.1.2'))
            for rate in (100, 1000):
                for repeat in range(1, 2 if smoke else 4):
                    for mode in ('page', 'push', 'direct', 'combined'):
                        case = result / f'{delay}-{rate}-{repeat}-{mode}'
                        case.mkdir()
                        def launch(index, *args):
                            log = open(case / f'process-{index}.log', 'w')
                            p = S.Popen(ns(index, *args), stdout=log, stderr=S.STDOUT)
                            processes.append(p)
                            return p
                        def waitfile(filename, timeout=60):
                            end = time.monotonic() + timeout
                            while not (case / filename).exists():
                                assert all(p.poll() is None for p in processes), f'process failed: {case}'
                                assert time.monotonic() < end, f'timeout: {case}/{filename}'
                                time.sleep(.01)
                        launch(0, sys.executable, str(ROOT / 'lab.py'), 'source', mode, str(case), str(rate), str(seconds), '10.207.1.2', str(synchronous))
                        waitfile('source-ready.json')
                        if mode != 'page':
                            launch(1, sys.executable, str(ROOT / 'lab.py'), 'destination', mode, str(case), str(rate), str(seconds), '-', str(synchronous))
                            waitfile('destination-ready.json')
                        if mode in ('page', 'combined'):
                            launch(1 if mode == 'page' else 2, 'node', str(ROOT / 'pull.mjs'), str(case), str(rate * seconds), str(rate * 2), '10.207.1.1' if mode == 'page' else '10.207.2.1')
                            waitfile('pull-ready.json')
                        (case / 'start').touch()
                        waitfile('source.json', seconds + 90)
                        if mode != 'page':
                            waitfile('destination.json')
                        if mode in ('page', 'combined'):
                            waitfile('pull.json')
                        item = {'delay_ms': delay, 'rate': rate, 'repeat': repeat, 'mode': mode, 'ping': rtt, 'loadavg': os.getloadavg()}
                        for file in ('source', 'destination', 'pull'):
                            if (case / (file + '.json')).exists():
                                item[file] = json.loads((case / (file + '.json')).read_text())
                        sequence = list(range(-rate * 2, 0)) + list(range(1, rate * seconds + 1))
                        expected = digest([row(i) for i in sequence])
                        for file in ('source', 'destination', 'pull'):
                            if file in item:
                                assert item[file]['rows'] == rate * (seconds + 2) and item[file]['hash'] == expected, (case, file)
                                assert [x[0] for x in item[file]['warmup_samples'] + item[file]['samples']] == sequence, (case, file, 'missing or duplicate sequence')
                        item['qdisc'] = [cmd(*ns(i, 'tc', '-s', 'qdisc', 'show')) for i in range(3)]
                        evidence['cases'].append(item)
                        with gzip.open(result / 'raw.json.gz.tmp', 'wt') as stream:
                            json.dump(evidence, stream, separators=(',', ':'))
                        (result / 'raw.json.gz.tmp').replace(result / 'raw.json.gz')
                        print(f'PASS {case.name}', flush=True)
                        for p in processes:
                            if p.poll() is None:
                                p.terminate()
                        for p in processes:
                            p.wait(timeout=10)
                        processes.clear()
    finally:
        for p in processes:
            if p.poll() is None:
                p.kill()
                p.wait()
        for name in created:
            S.run(['ip', 'netns', 'del', name], check=False)
        evidence['root_network_after'] = {c: cmd(*c.split()) for c in root_network}
        assert evidence['root_network_after'] == root_network, 'root network changed'
        evidence['cleanup'] = {'namespaces': created, 'remaining': cmd('ip', 'netns', 'list')}
        assert not set(created) & {line.split()[0] for line in evidence['cleanup']['remaining'].splitlines()}
        with gzip.open(result / 'raw.json.gz.tmp', 'wt') as stream:
            json.dump(evidence, stream, separators=(',', ':'))
        (result / 'raw.json.gz.tmp').replace(result / 'raw.json.gz')
        print(result, flush=True)

if __name__ == '__main__':
    if sys.argv[1] == 'run':
        signal.signal(signal.SIGTERM, lambda *_: sys.exit(143))
        orchestrate(int(sys.argv[2]), 'smoke' in sys.argv[3:], 1 if 'normal' in sys.argv[3:] else 2)
    else:
        worker(*sys.argv[1:4], int(sys.argv[4]), int(sys.argv[5]), sys.argv[6], int(sys.argv[7]))
