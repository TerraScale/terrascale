import assert from 'node:assert/strict'
import test from 'node:test'
import { runJourney } from './quickstart.mjs'

import { createTerraBaseV1Client } from '@terrascale/sdk'

function fixture({ acceptStale = false, forbidden = false } = {}) {
  let document, version = 'test-version-1', queryCalls = 0, calls = 0
  const id = '01ARZ3NDEKTSV4RRFFQ69G5FAV'
  const json = (status, body) => new Response(JSON.stringify(body), {
    status, headers: { 'content-type': 'application/json' },
  })
  const write = () => ({ id, version, offset: '1', durability: 'local_durable' })
  const client = createTerraBaseV1Client({
    baseUrl: 'https://api.example.invalid', project: '01ARZ3NDEKTSV4RRFFQ69G5FAV',
    token: `tb_${'s'.repeat(43)}`,
    fetch: async (url, options) => {
      calls++
      const path = new URL(url)
      assert.ok(path.pathname.startsWith('/v1/projects/01ARZ3NDEKTSV4RRFFQ69G5FAV/databases/quickstart/collections/posts/'))
      assert.equal(new Headers(options.headers).get('idempotency-key'), null)
      if (forbidden) return json(403, { error: { code: 'forbidden_scope' } })
      if (options.method === 'POST' && path.pathname.endsWith('/documents')) {
        document = JSON.parse(options.body)
        return json(201, write())
      }
      if (options.method === 'POST' && path.pathname.endsWith('/query')) {
        const query = JSON.parse(options.body)
        assert.deepEqual(query.filter, { eq: ['/run', document.run] })
        assert.equal(query.limit, 20)
        queryCalls++
        if (queryCalls === 1) return json(200, { items: [], next_cursor: 'synthetic-cursor' })
        assert.equal(query.cursor, 'synthetic-cursor')
        return json(200, { items: [{ id, version, state: 'value', document }], next_cursor: null })
      }
      assert.equal(path.searchParams.get('id'), id)
      if (options.method === 'GET') return document
        ? json(200, { id, version, state: 'value', document })
        : json(404, { error: { code: 'not_found' } })
      assert.equal(path.pathname.endsWith('/documents/by-key'), true)
      const match = new Headers(options.headers).get('if-match')
      if (match !== `"${version}"` && !acceptStale)
        return json(412, { error: { code: 'precondition_failed' } })
      if (options.method === 'PATCH') {
        assert.equal(new Headers(options.headers).get('content-type'), 'application/merge-patch+json')
        document = { ...document, ...JSON.parse(options.body) }
        version = 'test-version-2'
        return json(200, write())
      }
      assert.equal(options.method, 'DELETE')
      document = undefined
      version = 'test-version-3'
      return json(200, write())
    },
  })
  return { client, calls: () => calls, queryCalls: () => queryCalls }
}

test('published SDK journey follows an empty cursor page and checks conditional writes and deletion', async () => {
  const f = fixture()
  await runJourney(f.client, 'quickstart', 'posts', AbortSignal.timeout(5000))
  assert.equal(f.queryCalls(), 2)
  assert.equal(f.calls(), 8)
})

test('journey fails when the service accepts a stale version', async () => {
  const f = fixture({ acceptStale: true })
  await assert.rejects(runJourney(f.client, 'quickstart', 'posts', AbortSignal.timeout(5000)), /Missing expected rejection/)
  assert.equal(f.calls(), 4)
})

test('authorization denial is propagated without replaying a mutation', async () => {
  const f = fixture({ forbidden: true })
  await assert.rejects(runJourney(f.client, 'quickstart', 'posts', AbortSignal.timeout(5000)), { code: 'forbidden_scope' })
  assert.equal(f.calls(), 1)
})
