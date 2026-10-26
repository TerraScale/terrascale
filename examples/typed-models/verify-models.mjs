import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { generateDocumentModels } from '@terrascale/sdk/codegen'
const schema = JSON.parse(await readFile(new URL('./schema.json', import.meta.url), 'utf8'))
const first = await generateDocumentModels(schema, 'Order')
assert.equal(await generateDocumentModels(schema, 'Order'), first)
assert.equal(await readFile(new URL('./models.ts', import.meta.url), 'utf8'), first)
console.log('CLI output matches two deterministic API generations.')
