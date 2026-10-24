import { readdir, readFile } from 'node:fs/promises'
import { createClient } from '@libsql/client'
import { libsqlUrl } from './src/url.js'
import { accessToken } from './src/token.js'

// Applies migrations/*.sql in order, each file in one write batch, and records it
// in prisma_migrations so a second run skips it.
const client = createClient({ url: libsqlUrl(), authToken: await accessToken() })
await client.execute('CREATE TABLE IF NOT EXISTS prisma_migrations (name TEXT PRIMARY KEY, applied_at TEXT NOT NULL)')
const applied = new Set((await client.execute('SELECT name FROM prisma_migrations')).rows.map((r) => r.name))
for (const name of (await readdir('migrations')).filter((f) => f.endsWith('.sql')).sort()) {
  if (applied.has(name)) continue
  const sql = await readFile(`migrations/${name}`, 'utf8')
  const statements = sql.replace(/^--.*$/gm, '').split(';').map((s) => s.trim()).filter(Boolean)
  await client.batch([
    ...statements,
    { sql: 'INSERT INTO prisma_migrations (name, applied_at) VALUES (?, ?)', args: [name, new Date().toISOString()] },
  ], 'write')
  console.log('applied', name)
}
client.close()
