import assert from 'node:assert/strict'
import { env } from 'node:process'
import { generateId, isTerraBaseV1Error } from '@terrascale/sdk'
import { createServerClient } from '../server-token-provider/server-client.mjs'
import schema from './schema.json' with { type: 'json' }
import type { OrderCreate, OrderPatch } from './models.js'

const database = env.TERRASCALE_DATABASE_NAME
if (!database) throw new Error('Missing TERRASCALE_DATABASE_NAME.')
const { client } = createServerClient()
const db = client.database(database)
const name = `model_orders_${generateId().toLowerCase()}`
const options = { signal: AbortSignal.timeout(60_000) }
let created = false
// Retain this name if an ambiguous collection-create response needs reconciliation.
console.log(`Isolated collection: ${name}`)
try {
  await db.collections.create(name, options)
  created = true
  const orders = db.collection(name)
  await orders.putSchema(schema, options)
  const body: OrderCreate = { quantity: 2, note: null }
  const order = await orders.create(body, options)
  const read = await orders.get(order.id, options)
  assert.equal(read.document.quantity, 2)
  assert.equal(read.document.note, null)
  const patch: OrderPatch = { note: 'packed' }
  const updated = await orders.patch(order.id, { merge: patch }, { ...options, ifMatch: read.version })
  const remove: OrderPatch = { note: null }
  const removed = await orders.patch(order.id, { merge: remove }, { ...options, ifMatch: updated.version })
  assert.equal(Object.hasOwn((await orders.get(order.id, options)).document, 'note'), false)
  await orders.patch(order.id, { ops: [{ op: 'set', path: '/note', value: null }] },
    { ...options, ifMatch: removed.version })
  assert.equal((await orders.get(order.id, options)).document.note, null)
  // Valid TypeScript, invalid range: the server is authoritative.
  await assert.rejects(orders.create({ quantity: -1 }, options),
    error => isTerraBaseV1Error(error, 'schema_validation_failed'))
  await orders.delete(order.id, options)
  console.log('Typed create/read/merge/ops/delete and runtime validation passed.')
} finally {
  // Never drop a pre-existing collection or retry an uncertain mutation.
  if (created) {
    await db.collections.drop(name, { signal: AbortSignal.timeout(30_000) })
    console.log('Isolated collection removed.')
  }
}
