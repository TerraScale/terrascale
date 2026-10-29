import { createClient } from '@libsql/client'
import { drizzle } from 'drizzle-orm/libsql'
import * as schema from './schema.js'
import { accessToken } from './token.js'

const { TERRASCALE_PROJECT_ID: project, TERRASCALE_DATABASE: database } = process.env

// Access tokens last 15 minutes: create a new client with a fresh token before then.
export async function connect() {
  const client = createClient({
    url: `https://api.terrascale.tech/v1/projects/${project}/databases/${database}/libsql/`,
    authToken: await accessToken(),
  })
  return drizzle(client, { schema })
}
