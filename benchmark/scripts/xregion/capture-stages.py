# Usage: capan.py <tcpdump -tt text> <probe jsonl> <origin_ip> <dest_ip>
# Times on B's clock (ms after t0): put_out, batch_in (first origin->B:28081 payload > 60 B), ack_out (B:28081->origin payload), visible.
import sys, json, statistics as st
cap, probes, origin, dest = sys.argv[1:5]
pk = []
for line in open(cap):
    p = line.split()
    if len(p) < 5 or p[1] != 'IP': continue
    t = float(p[0]); src = p[2]; dst = p[4].rstrip(':')
    ln = int(line.rsplit('length', 1)[1].split()[0].strip(':')) if 'length' in line else 0
    pk.append((t, src, dst, ln))
rows = []
for l in open(probes):
    s = json.loads(l)
    if 'summary' in s: summ = s; continue
    t0 = s['t0_unix_ns'] / 1e9
    def first(after, pred):
        for t, a, b, n in pk:
            if t >= after and pred(a, b, n): return t
    put = first(t0, lambda a, b, n: a.startswith(dest + '.') and b == origin + '.28080' and n > 0)
    batch = first(put or t0, lambda a, b, n: a.startswith(origin + '.') and b == dest + '.28081' and n > 60)
    ack = first(batch or t0, lambda a, b, n: a == dest + '.28081' and b.startswith(origin + '.') and n > 0)
    if None in (put, batch, ack): continue
    ms = lambda x: (x - t0) * 1000
    rows.append(dict(put=ms(put), batch=ms(batch), ack=ms(ack), vis=s['visible_ms']))
rtt = summ['rtt_tcp_ms']
# Prefer the capture's own SYN -> SYN-ACK times (B's connects to A's client port) over the probe's: same clock, same minute.
syn = {}
hs = []
for line in open(cap):
    p = line.split()
    if len(p) < 7 or p[1] != 'IP': continue
    flags = line.split('Flags [', 1)[1].split(']', 1)[0] if 'Flags [' in line else ''
    a, b = p[2], p[4].rstrip(':')
    if flags == 'S' and b == origin + '.28080': syn[a] = float(p[0])
    elif flags == 'S.' and a == origin + '.28080' and b in syn: hs.append((float(p[0]) - syn.pop(b)) * 1000)
if hs:
    rtt = st.median(hs)
print(f"n={len(rows)} rtt_tcp={rtt:.1f} ms")
def med(k): return st.median(r[k] for r in rows)
seg = {
 'client send (t0 -> PUT on wire)': [r['put'] for r in rows],
 'origin: request in -> batch out (= batch_in - put - RTT)': [r['batch'] - r['put'] - rtt for r in rows],
 'dest: batch in -> ack out (apply + fsync + ack)': [r['ack'] - r['batch'] for r in rows],
 'dest: batch in -> visible to GET': [r['vis'] - r['batch'] for r in rows],
}
for k, v in seg.items():
    v = sorted(v); print(f"{k}: p50 {st.median(v):.1f}  p10 {v[len(v)//10]:.1f}  p90 {v[9*len(v)//10]:.1f}")
