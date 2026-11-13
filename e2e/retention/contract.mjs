import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const contract = JSON.parse(readFileSync(new URL('../../sdk/dotnet/contracts/terrabase/database-deletion.openapi.json', import.meta.url), 'utf8'));
const lifecycle = contract.paths['/v1/projects/{project}/deleted-databases/{deletedDatabase}'];
assert(lifecycle.get.responses['200'], 'purge jobs remain readable');
assert(lifecycle.delete.responses['202'], 'unverified purge is accepted with its durable job');
assert(lifecycle.delete.responses['204'], 'verified purge completes without a response body');
const accepted = lifecycle.delete.responses['202'].content['application/json'].schema;
const status = lifecycle.get.responses['200'].content['application/json'].schema;
assert.deepEqual(accepted, status, 'GET and pending DELETE share the generated purge job contract');
assert(contract.paths['/v1/projects/{project}/databases/{database}'].delete.responses['204'], 'ordinary deletion starts retention with 204');
console.log('generated deletion contract: ordinary DELETE 204; purge DELETE 202/204; GET 200 with the same job schema');
