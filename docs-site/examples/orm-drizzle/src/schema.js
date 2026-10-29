import { relations } from 'drizzle-orm'
import { index, integer, sqliteTable, text } from 'drizzle-orm/sqlite-core'

export const authors = sqliteTable('drizzle_authors', {
  id: integer('id').primaryKey(),
  name: text('name').notNull(),
  email: text('email').notNull().unique(),
})

export const posts = sqliteTable('drizzle_posts', {
  id: integer('id').primaryKey(),
  authorId: integer('author_id').notNull().references(() => authors.id),
  title: text('title').notNull(),
  published: integer('published', { mode: 'boolean' }).notNull().default(false),
}, (t) => [index('drizzle_posts_author_id_idx').on(t.authorId)])

export const authorsRelations = relations(authors, ({ many }) => ({ posts: many(posts) }))
export const postsRelations = relations(posts, ({ one }) => ({
  author: one(authors, { fields: [posts.authorId], references: [authors.id] }),
}))
