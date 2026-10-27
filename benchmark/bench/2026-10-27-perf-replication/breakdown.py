import json,os,sys,statistics as st
d=sys.argv[1]
def pct(v,q): v=sorted(v); return v[min(len(v)-1,max(0,round(q*len(v))-1))]
rows={}
for n in sorted(os.listdir(d)):
    if not n.endswith('.jsonl'): continue
    dirn,mode,_=n[:-6].split('.')
    lines=[json.loads(l) for l in open(os.path.join(d,n))]
    summ=[l for l in lines if l.get('summary')]
    if not summ: continue
    fl=summ[0]['floor_ms']
    for l in lines:
        if l.get('summary'): continue
        server=l['ack_ms']-2*fl
        rows.setdefault((dirn,mode),[]).append((l['latency_ms'],server,l['latency_ms']-max(server,0)-fl))
print("| Direction | Mode | n | origin server p50/p99 | commit->visible minus one-way p50/p99 | slow probes (>200ms): share where origin server > half |")
print("|---|---|---:|---|---|---|")
for k,v in sorted(rows.items(),key=lambda x:(x[0][1],x[0][0])):
    s=[x[1] for x in v]; r=[x[2] for x in v]; slow=[x for x in v if x[0]>200]
    share=sum(1 for x in slow if x[1]>x[0]/2)/len(slow) if slow else 0
    print(f"| {k[0]} | {k[1]} | {len(v)} | {pct(s,.5):.0f}/{pct(s,.99):.0f} | {pct(r,.5):.0f}/{pct(r,.99):.0f} | {len(slow)} slow, {share*100:.0f}% origin-bound |")
