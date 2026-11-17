import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { expect, test } from '@playwright/test';
import { keyId } from '../support/api-key';
import { auth } from '../support/stack';
import { journey, saveSecret } from '../support/state';

const source = `WITH input(data, key) AS (VALUES ('{"name":"Ada","b":{"c":"nested"},"items":["zero","one"]}', 'name')) SELECT `;
const cases: { name: string; sql: string; args?: string[]; pgSql?: string; expected: string }[] = [
  { name: 'single quotes', sql: source + `data->>'name' FROM input`, expected: 'Ada' },
  { name: 'identifier', sql: source + 'data->>key FROM input', expected: 'Ada' },
  { name: 'quoted identifiers', sql: source + '"data"->>"key" FROM input', expected: 'Ada' },
  { name: 'numbered parameter', sql: source + 'data->>$1 FROM input', args: ['name'], expected: 'Ada' },
  { name: 'question parameter', sql: source + 'data->>? FROM input', pgSql: source + 'data->>$1 FROM input', args: ['name'], expected: 'Ada' },
  { name: 'nested path', sql: source + `data->'b'->>'c' FROM input`, expected: 'nested' },
  { name: 'array index', sql: source + `data->'items'->>1 FROM input`, expected: 'one' },
  { name: 'JSON literal', sql: `SELECT '{"name":"Ada"}'->>'name'`, expected: 'Ada' },
  { name: 'spaced operator', sql: source + `data ->> 'name' FROM input`, expected: 'Ada' },
];

test('JSON text arrows preserve adjacent SQL tokens over REST, WebSocket and PostgreSQL', async ({ request }) => {
  const current = journey();
  const exchanged = await request.post(`${auth}/terrascale/oauth/token`, {
    form: {
      grant_type: 'client_credentials', client_id: keyId(current.apiKey),
      client_secret: current.apiKey, project: current.projectId, scope: 'sql:read',
    },
  });
  expect(exchanged.status()).toBe(200);
  const token = (await exchanged.json()).access_token as string;
  saveSecret('json-arrow-token', token);
  const path = `/v1/projects/${current.projectId}/databases/${current.databaseId}/sql`;

  for (const entry of cases) {
    const response = await request.post(`https://api.terrascale.tech${path}/execute`, {
      headers: { authorization: 'Bearer ' + token },
      data: { statements: [{ sql: entry.sql, args: entry.args }] },
    });
    const body = await response.json();
    expect(response.status(), `REST ${entry.name}: ${JSON.stringify(body)}`).toBe(200);
    expect(body.results[0].rows, `REST ${entry.name}`).toEqual([[entry.expected]]);
  }

  const socket = new WebSocket(`wss://api.terrascale.tech${path}/ws`, 'terrascale.sql.v1');
  try {
    await once(socket, 'open');
    const authenticated = once(socket, 'message');
    socket.send(JSON.stringify({ id: 1, type: 'auth', token }));
    expect(JSON.parse(((await authenticated)[0] as MessageEvent<string>).data)).toMatchObject({ id: 1, type: 'result' });
    for (const [index, entry] of cases.entries()) {
      const received = once(socket, 'message');
      socket.send(JSON.stringify({ id: index + 2, type: 'execute', statements: [{ sql: entry.sql, args: entry.args }] }));
      const body = JSON.parse(((await received)[0] as MessageEvent<string>).data);
      expect(body, `WebSocket ${entry.name}`).toMatchObject({
        id: index + 2, type: 'result', results: [{ rows: [[entry.expected]] }],
      });
    }
  } finally {
    const closed = once(socket, 'close');
    socket.close();
    await closed;
  }

  const url = `postgresql://any@pg.terrascale.tech:5432/${current.projectId}/${current.databaseId}?sslmode=verify-full&sslrootcert=system`;
  const pg = spawn('psql', ['-X', '-v', 'ON_ERROR_STOP=1', '-At', url], {
    env: { ...process.env, PGPASSWORD: current.apiKey }, stdio: ['pipe', 'pipe', 'pipe'],
  });
  let stdout = '';
  let stderr = '';
  pg.stdout.on('data', (chunk: Buffer) => { stdout += chunk.toString(); });
  pg.stderr.on('data', (chunk: Buffer) => { stderr += chunk.toString(); });
  const completed = once(pg, 'close');
  pg.stdin.end(cases.map(entry => entry.args
    ? `${entry.pgSql ?? entry.sql}\n\\bind ${entry.args.join(' ')}\n\\g\n`
    : `${entry.sql};\n`).join(''));
  expect((await completed)[0], `PostgreSQL: ${stderr}`).toBe(0);
  expect(stdout.trim().split('\n')).toEqual(cases.map(entry => entry.expected));
});
