import { readFile } from 'node:fs/promises';

const [resultsPath, receiptPath] = process.argv.slice(2);
const results = JSON.parse(await readFile(resultsPath, 'utf8'));
const receipt = JSON.parse(await readFile(receiptPath, 'utf8'));
const runs = results.durable;
const controls = results.controls;
const out = [];
const line = (s = '') => out.push(s);
const row = (cells) => line(`| ${cells.join(' | ')} |`);
const header = (cells, align) => {
  row(cells);
  row(align ?? cells.map((_, i) => (i === 0 ? '---' : '---:')));
};
const perRepeat = (label, f) => row([label, ...runs.map(f)]);
const yes = (b) => (b ? 'yes' : '**no**');
const pct = (v, p) => {
  const s = [...v].sort((a, b) => a - b);
  return s.length ? s[Math.min(s.length - 1, Math.ceil(s.length * p) - 1)] : null;
};
const ms = (v) => (v == null ? 'n/a' : v.toFixed(1));
const repeatCols = runs.map((r) => `Repeat ${r.repeat}`);

line(`# Durable sync revision prototype (W0-2), ${receipt.date}`);
line();
line(
  `**Verdict: ${results.pass ? 'PASS' : 'FAIL'}.** ${runs.filter((r) => r.pass).length}/${runs.length} repeats passed every gate with the official \`@tursodatabase/sync\` ${receipt.client} client. The unchanged upstream sync server reproduced the stale checkpoint pull in ${controls.filter((c) => c.bug_reproduced).length}/${controls.length} control repeats.`,
);
line();
line('Source: [`benchmark/turso-sync-host/`](../turso-sync-host/README.md). Regenerate this page with `qualify/report.mjs` from the JSON written by `qualify/run.sh`.');
line();
line('## Pins and environment');
line();
header(['Item', 'Value'], ['---', '---']);
row(['TerraScale commit', `\`${receipt.terrascale_commit}\``]);
row(['Turso source', `[\`${receipt.turso_commit}\`](https://github.com/tursodatabase/turso/tree/${receipt.turso_commit}) (0.8.2, tarball SHA-256 \`${receipt.turso_sha256}\`)`]);
row(['Rust toolchain', receipt.rust]);
row(['`ts-sync-revision-host` SHA-256', `\`${receipt.host_sha256}\``]);
row(['`tursodb` (control) SHA-256', `\`${receipt.cli_sha256}\``]);
row(['Official client', `\`@tursodatabase/sync\` ${receipt.client}, Node ${results.node}`]);
row(['Machine', `${receipt.machine} (${receipt.cpu}, ${receipt.cpus} CPUs), Linux ${receipt.kernel}`]);
row(['Network', 'loopback; client, host and control on one machine'] );
line();
line('## Gates');
line();
line('Every counter must be 0 in every repeat. "Acked" means a server write whose pipeline request returned success, or a client row whose `push()` returned. "Local" means a row the client committed offline and has not pushed yet.');
line();
header(['Gate', ...repeatCols]);
const totals = [
  ['Lost acked writes (server missing an acked row)', 'lost_acked_writes'],
  ['Lost local writes (client lost a pending offline row)', 'lost_local_writes'],
  ['Skipped commits (client missing an acked row after `pull()`)', 'skipped_commits'],
  ['Phantom rows (client keeps a row the server does not have)', 'phantom_rows'],
  ['`pull()` returned false while rows were missing', 'stale_false_pulls'],
  ['Unexpected server rows', 'unexpected_server_rows'],
  ['Plan mismatches (wrong incremental/full/expired decision)', 'plan_mismatches'],
];
for (const [label, key] of totals) perRepeat(label, (r) => String(r.totals[key]));
perRepeat('History bounded', (r) => yes(r.bounded.pass));
perRepeat('All clients equal to the server, `integrity_check` ok', (r) => yes(r.converged && Object.values(r.converged.clients).every((c) => c.equal_to_server && c.integrity === 'ok') && r.converged.server_integrity === 'ok'));
perRepeat('Repeat passed', (r) => yes(r.pass));
line();
line('## Lifecycle events with pending offline writes');
line();
line('Each row is the first `/pull-updates` response the official client received after the event, while it held offline writes. The host labels each pull response; the harness checks the label and then the rows.');
line();
const key = (p) => `${p.phase} / ${p.client} / ${p.expect}`;
const checked = new Map();
for (const r of runs) {
  for (const p of r.pulls) {
    if (!p.expect) continue;
    const k = key(p);
    if (!checked.has(k)) checked.set(k, { expect: p.expect, byRepeat: {} });
    const entry = checked.get(k);
    entry.byRepeat[r.repeat] ??= [];
    entry.byRepeat[r.repeat].push(p);
  }
}
header(['Event / client', 'Expected', ...repeatCols], ['---', '---', ...runs.map(() => '---')]);
for (const [k, entry] of checked) {
  row([
    k.split(' / ').slice(0, 2).join(' / '),
    `\`${entry.expect}\``,
    ...runs.map((r) => {
      const ps = entry.byRepeat[r.repeat] ?? [];
      const ok = ps.every((p) => (p.kinds[0] ?? '').startsWith(p.expect) && p.missing === 0 && p.lost_local === 0 && p.phantom === 0);
      const first = ps[0]?.kinds[0] ?? 'none';
      const kind = first.split(';pages=')[0];
      const pages = first.match(/pages=(\d+);db_size=(\d+)/);
      return `${ok ? 'ok' : '**FAIL**'}: \`${kind}\`${pages ? ` ${pages[1]}/${pages[2]} pages` : ''}${ps.length > 1 ? ` (x${ps.length})` : ''}`;
    }),
  ]);
}
line();
line('Explicit expiry: a chunked bootstrap (`pullBytesThreshold`) pins its first revision. The harness checkpoints between chunks; the next pinned chunk must fail loudly, and a retry on the same path must bootstrap.');
line();
header(['Check', ...repeatCols]);
perRepeat('Pinned chunk rejected with HTTP 409 `revision_expired`', (r) => yes(r.expiry?.explicit));
perRepeat('Retry on the same path converged', (r) => yes(r.converged?.clients?.d?.equal_to_server));
perRepeat('Chunked bootstrap requests (first + pinned)', (r) => String(r.chunked_bootstrap_kinds?.filter((k) => !k.startsWith('empty')).length ?? 0));
line();
line('## Bounded history');
line();
header(['Measure', ...repeatCols]);
perRepeat('Peak WAL bytes', (r) => String(r.bounded.max_wal_bytes));
perRepeat('WAL bound (32 + (1024 + 256) frames x 4120 B)', (r) => String(r.bounded.wal_bound_bytes));
perRepeat('Peak exact generations retained (limit 8)', (r) => String(r.bounded.max_sealed_generations));
perRepeat('Peak compacted buckets (limit 8 x 8 generations)', (r) => String(r.bounded.max_horizon_buckets));
perRepeat('Peak manifest bytes', (r) => String(r.bounded.max_manifest_bytes));
perRepeat('Automatic checkpoints during the 8,000-row burst', (r) => String(r.automatic_checkpoints));
perRepeat('Coordinated checkpoints, all host lifetimes', (r) => String(r.server_counters?.checkpoints ?? 'n/a'));
perRepeat('Checkpoints detected outside the coordinator', (r) => String(r.server_counters?.detected_checkpoints ?? 'n/a'));
perRepeat('Rewrite: database pages before / after', (r) => `${r.rewrite?.pages_before} / ${r.rewrite?.pages_after}`);
perRepeat('Acked rows rolled back by restoring an older backup', (r) => String(r.rolled_back_by_restore));
line();
line('The rolled-back rows were acked after backup `b2` was taken. Restoring `b2` removes them by definition (recovery point), and the gate is that every client drops them too (0 phantom rows). They are not counted as lost acked writes.');
line();
line('## Pull latency (same host, includes client apply)');
line();
const lat = (r, f) => r.pulls.filter((p) => f(p.kinds[0] ?? '')).map((p) => p.ms);
header(['Pull kind', ...repeatCols]);
for (const [label, f] of [
  ['Incremental, p50 / max ms', (k) => k.startsWith('incremental')],
  ['Full resend, p50 / max ms', (k) => k.startsWith('full')],
  ['Empty, p50 / max ms', (k) => k.startsWith('empty')],
]) {
  perRepeat(label, (r) => {
    const v = lat(r, f);
    return `${ms(pct(v, 0.5))} / ${ms(v.length ? Math.max(...v) : null)} (n=${v.length})`;
  });
}
line();
line('## Control: unchanged upstream sync server');
line();
line('`tursodb --sync-server` from the same pin, same official client. 100 rows, `PRAGMA wal_checkpoint(TRUNCATE)`, one more insert, then `pull()`.');
line();
header(['Measure', ...controls.map((c) => `Repeat ${c.repeat}`)]);
row(['Client rows before', ...controls.map((c) => String(c.rows_before))]);
row(['`pull()` returned', ...controls.map((c) => String(c.pull_returned))]);
row(['Client rows after', ...controls.map((c) => String(c.rows_after))]);
row(['Server rows', ...controls.map((c) => String(c.server_rows))]);
row(['Stale pull reproduced', ...controls.map((c) => yes(c.bug_reproduced))]);
line();
line('## Limits');
line();
for (const l of [
  'One machine over loopback. No network latency, packet loss or cross-region links.',
  'Sequential workload: one request at a time, so there are no concurrent writer races. Crash tests use SIGKILL of the host process, not power loss.',
  'Only the official JS client (page protocol, V1 revisions) was qualified. Go, Python and Rust clients, partial sync and MVCC were not tested.',
  "Client pushes use upstream's Hrana pipeline only to deliver the pending offline writes. Production never uses Turso push (D14); R6/R7 replace it.",
  'The prototype buffers every response in memory, keeps every database open, and binds 127.0.0.1 with fixture tokens. R1/R2 must add streaming, LRU, TLS and production auth.',
  'Upstream SQL can run `PRAGMA wal_checkpoint` through the pipeline. Here that is detected and served as a full resend. Production denies it in the D15 sandbox.',
]) line(`- ${l}`);
line();
process.stdout.write(out.join('\n'));
