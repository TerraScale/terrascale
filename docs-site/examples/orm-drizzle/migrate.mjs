import { migrate } from 'drizzle-orm/libsql/migrator'
import { connect } from './src/db.js'

const db = await connect()
// Table names start with a letter, so rename Drizzle's default __drizzle_migrations.
await migrate(db, { migrationsFolder: './drizzle', migrationsTable: 'drizzle_migrations' })
console.log('migrated')
