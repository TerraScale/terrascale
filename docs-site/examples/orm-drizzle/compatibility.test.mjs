import assert from 'node:assert/strict'
import test from 'node:test'
import { runInNewContext } from 'node:vm'
import { createClient } from '@libsql/client'
import { eq } from 'drizzle-orm'
import { drizzle } from 'drizzle-orm/libsql'
import { migrate } from 'drizzle-orm/libsql/migrator'
import { transform, transformSync } from '@esbuild-kit/core-utils'
import * as schema from './src/schema.js'

test('legacy loader transforms TypeScript with the patched esbuild API', async () => {
  const source = 'export const answer: number = 42'
  const sync = transformSync(source, '/example.ts')
  const module = { exports: {} }
  runInNewContext(sync.code, { module })
  assert.equal(module.exports.answer, 42)
  assert.ok(sync.map)
  const async = await transform(source, '/example.ts')
  const compiled = await import(`data:text/javascript;base64,${Buffer.from(async.code).toString('base64')}`)
  assert.equal(compiled.answer, 42)
  assert.ok(async.map)
})

test('example migrations, relations and transactions work with the locked tooling', async () => {
  const client = createClient({ url: 'file::memory:' })
  const db = drizzle(client, { schema })
  try {
    const migration = { migrationsFolder: './drizzle', migrationsTable: 'drizzle_migrations' }
    await migrate(db, migration)
    await migrate(db, migration)
    const [ada] = await db.insert(schema.authors).values({ name: 'Ada', email: 'ada@example.com' }).returning()
    await db.insert(schema.posts).values({ authorId: ada.id, title: 'Draft' })
    const author = await db.query.authors.findFirst({
      where: eq(schema.authors.id, ada.id), with: { posts: true },
    })
    assert.equal(author.posts[0].title, 'Draft')
    assert.equal(author.posts[0].published, false)
    const joined = await db.select({ author: schema.authors.name }).from(schema.posts)
      .innerJoin(schema.authors, eq(schema.posts.authorId, schema.authors.id))
    assert.deepEqual(joined, [{ author: 'Ada' }])
    await db.transaction(async (tx) => {
      const [grace] = await tx.insert(schema.authors).values({ name: 'Grace', email: 'grace@example.com' }).returning()
      await tx.update(schema.posts).set({ authorId: grace.id, published: true })
    })
    await assert.rejects(db.transaction(async (tx) => {
      await tx.insert(schema.authors).values({ name: 'Linus', email: 'linus@example.com' })
      await tx.insert(schema.authors).values({ name: 'Duplicate', email: 'ada@example.com' })
    }))
    assert.deepEqual((await db.select().from(schema.authors).orderBy(schema.authors.id)).map((row) => row.name), ['Ada', 'Grace'])
    assert.equal((await db.select().from(schema.posts))[0].published, true)
    await db.delete(schema.posts)
    assert.deepEqual(await db.select().from(schema.posts), [])
  } finally {
    client.close()
  }
})
