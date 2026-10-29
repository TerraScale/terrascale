import { eq } from 'drizzle-orm'
import { connect } from './src/db.js'
import { authors, posts } from './src/schema.js'

const db = await connect()

// Create
const [ada] = await db.insert(authors).values({ name: 'Ada', email: 'ada@example.com' }).returning()
await db.insert(posts).values([
  { authorId: ada.id, title: 'Notes on the Analytical Engine', published: true },
  { authorId: ada.id, title: 'Draft' },
])

// Read, with a relation
const withPosts = await db.query.authors.findFirst({ where: eq(authors.id, ada.id), with: { posts: true } })
console.log(withPosts.name, withPosts.posts.map((p) => p.title))

const joined = await db.select({ title: posts.title, author: authors.name })
  .from(posts).innerJoin(authors, eq(posts.authorId, authors.id))
console.log(joined)

// Update
await db.update(posts).set({ published: true }).where(eq(posts.title, 'Draft'))

// Transaction: both writes commit, or neither does.
await db.transaction(async (tx) => {
  const [grace] = await tx.insert(authors).values({ name: 'Grace', email: 'grace@example.com' }).returning()
  await tx.update(posts).set({ authorId: grace.id }).where(eq(posts.title, 'Draft'))
})

// A failed transaction writes nothing.
try {
  await db.transaction(async (tx) => {
    await tx.insert(authors).values({ name: 'Linus', email: 'linus@example.com' })
    await tx.insert(authors).values({ name: 'Dup', email: 'ada@example.com' }) // unique violation
  })
} catch (error) {
  console.log('rolled back:', error.cause?.code ?? error.code)
}
console.log('authors:', (await db.select().from(authors)).map((a) => a.name))

// Delete
const [grace] = await db.delete(authors).where(eq(authors.email, 'grace@example.com')).returning()
await db.delete(posts).where(eq(posts.authorId, grace.id))
console.log('posts left:', (await db.select().from(posts)).length)
