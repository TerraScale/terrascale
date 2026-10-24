import { PrismaLibSql } from '@prisma/adapter-libsql'
import { PrismaClient } from './generated/prisma/client.ts'
import { accessToken } from './src/token.js'
import { libsqlUrl } from './src/url.js'

// Access tokens last 15 minutes: create a new client with a fresh token before then.
const adapter = new PrismaLibSql({ url: libsqlUrl(), authToken: await accessToken() })
const prisma = new PrismaClient({ adapter })

// Create, with a nested relation
const ada = await prisma.author.create({
  data: {
    name: 'Ada',
    email: 'ada@example.com',
    posts: { create: [{ title: 'Notes on the Analytical Engine', published: true }, { title: 'Draft' }] },
  },
})

// Read, with a relation
const withPosts = await prisma.author.findUnique({ where: { id: ada.id }, include: { posts: true } })
console.log(withPosts?.name, withPosts?.posts.map((p) => p.title))
console.log(await prisma.post.findMany({ where: { author: { name: 'Ada' } }, select: { title: true } }))

// Update
const draft = await prisma.post.findFirstOrThrow({ where: { title: 'Draft' } })
await prisma.post.update({ where: { id: draft.id }, data: { published: true } })

// Transaction: both writes commit, or neither does.
await prisma.$transaction(async (tx) => {
  const grace = await tx.author.create({ data: { name: 'Grace', email: 'grace@example.com' } })
  await tx.post.update({ where: { id: draft.id }, data: { authorId: grace.id } })
})

// A failed transaction writes nothing.
try {
  await prisma.$transaction([
    prisma.author.create({ data: { name: 'Linus', email: 'linus@example.com' } }),
    prisma.author.create({ data: { name: 'Dup', email: 'ada@example.com' } }), // unique violation
  ])
} catch (error) {
  // Over libSQL HTTP a unique violation is P2039 carrying SQLITE_CONSTRAINT_UNIQUE, not P2002.
  const { code, message } = error as { code?: string; message: string }
  console.log('rolled back:', code, message.includes('SQLITE_CONSTRAINT_UNIQUE') ? 'SQLITE_CONSTRAINT_UNIQUE' : '')
}
console.log('authors:', (await prisma.author.findMany()).map((a) => a.name))

// Delete
await prisma.post.deleteMany({ where: { author: { name: 'Grace' } } })
await prisma.author.delete({ where: { email: 'grace@example.com' } })
console.log('posts left:', await prisma.post.count())
await prisma.$disconnect()
