import assert from 'node:assert/strict'
import { createHmac } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'

// Execute the published example so signature guidance cannot drift from its check.
const page = readFileSync(new URL('../docs/guides/change-webhooks.md', import.meta.url), 'utf8')
const code = page.match(/```js\n([\s\S]*?)\n```/)[1]
const { verifyWebhook } = await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`)
const secret = Buffer.alloc(32, 7).toString('base64url')
const hook = '01ARZ3NDEKTSV4RRFFQ69G5FAX'
const delivery = '01ARZ3NDEKTSV4RRFFQ69G5FAY'
const now = 1792756800000
const timestamp = String(now / 1000)
const body = Buffer.from(`{ "id": "${delivery}", "webhookId": "${hook}", "events": [] }`)
function signed(raw = body, time = timestamp) {
  return {
    'x-terrascale-delivery': delivery,
    'x-terrascale-timestamp': time,
    'x-terrascale-signature': `v1=${createHmac('sha256', Buffer.from(secret, 'base64url')).update(time).update('.').update(raw).digest('hex')}`,
  }
}

test('raw bytes, base64url key, identity and five-minute timestamp boundary', () => {
  assert.equal(verifyWebhook(body, signed(), secret, hook, now).id, delivery)
  for (const delta of [-300, 300]) {
    const time = String(Number(timestamp) + delta)
    assert.equal(verifyWebhook(body, signed(body, time), secret, hook, now).id, delivery)
  }
  assert.throws(() => verifyWebhook(Buffer.from(JSON.stringify(JSON.parse(body))), signed(), secret, hook, now))
  assert.throws(() => verifyWebhook(body, signed(), Buffer.alloc(32, 8).toString('base64url'), hook, now))
  assert.throws(() => verifyWebhook(body, signed(), 'short', hook, now))
  assert.throws(() => verifyWebhook(body, signed(), secret, 'another-hook', now))
  assert.throws(() => verifyWebhook(body, { ...signed(), 'x-terrascale-delivery': hook }, secret, hook, now))
})

test('reject stale/future timestamps, malformed headers, oversized bodies and wrong IDs', () => {
  for (const delta of [-301, 301]) {
    const time = String(Number(timestamp) + delta)
    assert.throws(() => verifyWebhook(body, signed(body, time), secret, hook, now))
  }
  for (const [name, value] of [
    ['x-terrascale-timestamp', undefined], ['x-terrascale-timestamp', [timestamp]],
    ['x-terrascale-timestamp', '1e9'], ['x-terrascale-signature', 'v1=00'],
    ['x-terrascale-signature', `v1=${'z'.repeat(64)}`], ['x-terrascale-signature', undefined],
  ]) assert.throws(() => verifyWebhook(body, { ...signed(), [name]: value }, secret, hook, now))
  const oversized = Buffer.alloc(512 * 1024 + 1)
  assert.throws(() => verifyWebhook(oversized, signed(oversized), secret, hook, now))
  for (const value of [null, { id: 'delivery_prefixed', webhookId: hook }]) {
    const raw = Buffer.from(JSON.stringify(value))
    assert.throws(() => verifyWebhook(raw, signed(raw), secret, hook, now))
  }
})
