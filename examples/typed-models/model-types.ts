import { ExactDecimal } from '@terrascale/sdk'
import type { V1Collection } from '@terrascale/sdk'
import type { OrderDocument, OrderCreate, OrderPatch } from './models.js'
const create: OrderCreate = { quantity: 2 }
const nullable: OrderCreate = { quantity: 2, note: null }
const exact: OrderCreate = { quantity: 2n }
const stored: OrderDocument = { quantity: new ExactDecimal('2'), note: null }
const empty: OrderPatch = {}
const remove: OrderPatch = { note: null }
const change: OrderPatch = { quantity: 3 }
// @ts-expect-error quantity is required
const missing: OrderCreate = {}
// @ts-expect-error quantity is not nullable
const nullQuantity: OrderCreate = { quantity: null }
// @ts-expect-error exact optional properties reject undefined
const undefinedNote: OrderCreate = { quantity: 2, note: undefined }
// @ts-expect-error closed schema rejects undeclared fields
const extra: OrderCreate = { quantity: 2, other: true }
// @ts-expect-error null would delete a required field
const deleteRequired: OrderPatch = { quantity: null }
// @ts-expect-error note must be string or null
const wrongNote: OrderPatch = { note: 4 }
// @ts-expect-error bigint is a write input, not a decoded document number
const decodedBigint: OrderDocument = { quantity: 2n }
// Compile SDK integration without executing a request.
export function acceptedBySdk(collection: V1Collection) {
  return [() => collection.create(create), () => collection.put('01ARZ3NDEKTSV4RRFFQ69G5FAV', nullable),
    () => collection.patch('01ARZ3NDEKTSV4RRFFQ69G5FAV', { merge: remove })]
}
void [exact, stored, empty, change, missing, nullQuantity, undefinedNote, extra,
  deleteRequired, wrongNote, decodedBigint]
