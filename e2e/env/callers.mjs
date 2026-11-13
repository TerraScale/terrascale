import { generateKeyPairSync } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';

const state = process.argv[2];
const { privateKey, publicKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
writeFileSync(join(state, 'caller-key.pem'), privateKey.export({ type: 'pkcs8', format: 'pem' }), { mode: 0o600 });
writeFileSync(join(state, 'callers.json'), JSON.stringify({
  version: 1,
  callers: [{
    kid: 'journey.local.1', caller: 'journey', kind: 'service', jwk: publicKey.export({ format: 'jwk' }),
    grants: ['data:00000000000000000000000002'], notAfter: new Date(Date.now() + 86400000).toISOString(),
  }, {
    kid: 'journey-catalog.local.1', caller: 'journey-catalog', kind: 'service', jwk: publicKey.export({ format: 'jwk' }),
    grants: ['catalog.read'], notAfter: new Date(Date.now() + 86400000).toISOString(),
  }, {
    kid: 'op.journey.1', caller: 'op:journey', kind: 'operator', jwk: publicKey.export({ format: 'jwk' }),
    grants: ['platform.read'], notAfter: new Date(Date.now() + 86400000).toISOString(),
  }],
}), { mode: 0o600 });
