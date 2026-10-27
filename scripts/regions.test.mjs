import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { REGIONS } from '../src/lib/world-map.generated.ts';

const catalog = JSON.parse(readFileSync(new URL('../static/regions.json', import.meta.url), 'utf8'));

test('public region catalog identifies the three OCI launch regions', () => {
  assert.deepEqual(catalog.regions.map(({ id, name, provider, providerRegion }) =>
    ({ id, name, provider, providerRegion })), [
    { id: 'gru', name: 'São Paulo', provider: 'oci', providerRegion: 'sa-saopaulo-1' },
    { id: 'sjc', name: 'San Francisco Bay Area', provider: 'oci', providerRegion: 'us-sanjose-1' },
    { id: 'fra', name: 'Frankfurt', provider: 'oci', providerRegion: 'eu-frankfurt-1' },
  ]);
});

test('generated map uses the public catalog names and coordinates', () => {
  assert.deepEqual(REGIONS.map(({ id, city, lat, lng }) => ({ id, name: city, lat, lng })),
    catalog.regions.map(({ id, name, lat, lng }) => ({ id, name, lat, lng })));
});
