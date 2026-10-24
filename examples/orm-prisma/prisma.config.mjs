import { defineConfig } from 'prisma/config'

// Prisma's schema engine cannot connect to libSQL over HTTP: migration SQL is
// generated offline with `prisma migrate diff` and applied by migrate.mjs.
export default defineConfig({
  schema: 'prisma/schema.prisma',
  datasource: { url: 'file:./unused.db' },
})
