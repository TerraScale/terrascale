import { createTerraBaseClient } from '../../../sdk/js/src/index.js';

export async function sessionFault() {
  const origin = process.env.E2E_SESSION_PROXY;
  const client = createTerraBaseClient({ apiKey: process.env.TERRASCALE_API_KEY, project: process.env.TERRASCALE_PROJECT_ID,
    baseUrl: origin, authBaseUrl: 'https://auth.terrascale.tech' });
  const db = client.database(process.env.TERRASCALE_DATABASE_NAME);
  const table = process.env.E2E_SESSION_TABLE;
  const mode = process.env.E2E_SESSION_FAULT;
  const failure = async work => {
    try { await work(); return 'accepted'; }
    catch (error) { return error.code ?? error.name; }
  };
  await db.execute(`CREATE TABLE ${table} (n INTEGER)`);
  try {
    const session = await db.connect();
    try {
      if (!['autocommit', 'batch'].includes(mode)) {
        await session.begin();
        await session.execute(`INSERT INTO ${table} VALUES (1)`);
      }
      if (mode === 'before') {
        await fetch(`${origin}/cut`, { method: 'POST' });
        await failure(() => session.query('SELECT 1'));
      }
      const first = await failure(() => mode === 'autocommit' ? session.execute(`INSERT INTO ${table} VALUES (1)`) : mode === 'batch' ? session.batch([`INSERT INTO ${table} VALUES (1)`, `INSERT INTO ${table} VALUES (2)`]) : mode === 'read' ? session.query('SELECT 1') : session.commit());
      const next = await failure(() => session.begin());
      const rows = await db.query(`SELECT count(*) AS n FROM ${table}`);
      console.log(JSON.stringify({ first, next, rows: String(rows[0].n) }));
    } finally { session.close(); }
  } finally { await db.execute(`DROP TABLE ${table}`); }
}
