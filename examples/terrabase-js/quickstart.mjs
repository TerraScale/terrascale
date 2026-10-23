import assert from 'node:assert/strict'
import { pathToFileURL } from 'node:url'
import {
  createTerraBaseV1Client, v1Filter,
  TerraBaseV1Error, V1NotFoundError, V1PreconditionFailedError,
} from '@terrascale/sdk'

// #region connection
export function connect({ endpoint, project, token }) {
  return createTerraBaseV1Client({ baseUrl: endpoint, project, token })
}
// #endregion connection

// #region collection
// A new database has no collections; 409 means this one already exists.
export async function ensureCollection(client, database, collection, signal) {
  try {
    await client.database(database).collections.create(collection, { signal })
  } catch (error) {
    if (!(error instanceof TerraBaseV1Error && error.status === 409)) throw error
  }
}
// #endregion collection

// #region journey
export async function runJourney(client, database, collection, signal) {
  const posts = client.database(database).collection(collection)
  const run = crypto.randomUUID()
  const created = await posts.create({ title: 'Hello TerraBase', run }, { signal })
  const current = await posts.get(created.id, { signal })
  assert.equal(current.document.title, 'Hello TerraBase')
  assert.equal(current.version, created.version)

  const updated = await posts.patch(created.id,
    { merge: { title: 'Updated' } }, { ifMatch: current.version, signal })
  await assert.rejects(
    posts.patch(created.id, { merge: { title: 'Stale write' } },
      { ifMatch: current.version, signal }),
    V1PreconditionFailedError,
  )

  let found = false
  for await (const page of posts.pages({ filter: v1Filter.eq('/run', run), limit: 20 }, { signal })) {
    for (const item of page.items) {
      assert.equal(item.id, created.id)
      assert.equal(item.document.title, 'Updated')
      assert.equal(found, false, 'Expected exactly one generated document')
      found = true
    }
  }
  assert.equal(found, true, 'Query did not return the generated document')

  await posts.delete(created.id, { ifMatch: updated.version, signal })
  await assert.rejects(posts.get(created.id, { signal }), V1NotFoundError)
}
// #endregion journey

function required(name) {
  const value = process.env[name]
  assert.ok(value, `Missing ${name}`)
  return value
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const client = connect({
      endpoint: required('TERRASCALE_API_URL'),
      project: required('TERRASCALE_PROJECT_ID'),
      token: required('TERRASCALE_ACCESS_TOKEN'),
    })
    const database = required('TERRASCALE_DATABASE_NAME')
    const collection = required('TERRASCALE_COLLECTION_NAME')
    const signal = AbortSignal.timeout(60_000)
    await ensureCollection(client, database, collection, signal)
    await runJourney(client, database, collection, signal)
    console.log('PASS_TERRABASE_CRUD_QUERY')
  } catch (error) {
    const cause = error instanceof TerraBaseV1Error ? `HTTP ${error.status} ${error.code}` : `${error.name}: ${error.message}`
    console.error(`Quickstart failed: ${cause}. Reconcile any interrupted write before retrying.`)
    process.exitCode = 1
  }
}
