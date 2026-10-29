import { connect } from '@tursodatabase/sync';
import { createHash } from 'node:crypto';
import { writeFileSync } from 'node:fs';
const [directory, count, warmup, host] = process.argv.slice(2);
const clock = () => Number(process.hrtime.bigint());
const save = (name, value) => writeFileSync(`${directory}/${name}.json`, JSON.stringify(value));
const requests = [];
const instrumentedFetch = async (url, options) => {
    const start = clock();
    const response = await fetch(url, { ...options, signal: AbortSignal.timeout(30000) });
    if (!response.ok) throw Error(`${response.status}: ${await response.text()}`);
    const data = new Uint8Array(await response.arrayBuffer());
    requests.push([start, clock(), data.length, String(url).endsWith('pull-updates')]);
    return new Response(data, { status: response.status });
};
const db = await connect({ path: `${directory}/replica.db`, url: `http://${host}:18981/db/lab`, clientName: 'lag-lab', fetch: instrumentedFetch });
const samples = [], rows = [];
save('pull-ready', { clock: clock() });
let watermark = -Number(warmup) - 1;
while (watermark < Number(count)) {
    await db.pull();
    const batch = await (await db.prepare(`SELECT id,seq,value FROM items WHERE seq>${watermark} ORDER BY seq`)).all();
    const stamp = clock();
    for (const r of batch) {
        rows.push([r.id, Number(r.seq), r.value]);
        samples.push([Number(r.seq), stamp]);
        watermark = Number(r.seq);
    }
}
const persisted = await (await db.prepare('SELECT id,seq,value FROM items ORDER BY seq')).all();
if (persisted.length !== rows.length) throw Error('replica row count changed');
const hash = createHash('sha256').update(JSON.stringify(persisted.map(r => [r.id, Number(r.seq), r.value]))).digest('hex');
save('pull', { samples: samples.filter(r => r[0] > 0), warmup_samples: samples.filter(r => r[0] < 0), rows: persisted.length, hash, requests });
await db.close();
setInterval(() => {}, 1000);
