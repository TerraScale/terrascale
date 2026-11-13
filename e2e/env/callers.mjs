import { generateKeyPairSync } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';

const state = process.argv[2];
const { privateKey, publicKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
writeFileSync(join(state, 'caller-key.pem'), privateKey.export({ type: 'pkcs8', format: 'pem' }), { mode: 0o600 });
const probe = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
const bridge = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
writeFileSync(join(state, 'caller-probe-key.pem'), probe.privateKey.export({ type: 'pkcs8', format: 'pem' }), { mode: 0o600 });
writeFileSync(join(state, 'caller-bridge-key.pem'), bridge.privateKey.export({ type: 'pkcs8', format: 'pem' }), { mode: 0o600 });
writeFileSync(join(state, 'callers.json'), JSON.stringify({
  version: 1,
  callers: [{
    kid: 'journey-probe.data.1', caller: 'journey-probe', kind: 'service', jwk: probe.publicKey.export({ format: 'jwk' }),
    grants: ['data:00000000000000000000000002'], notAfter: new Date(Date.now() + 86400000).toISOString(),
  }, {
    kid: 'journey-probe.catalog.1', caller: 'journey-probe', kind: 'service', jwk: probe.publicKey.export({ format: 'jwk' }),
    grants: ['catalog.read'], notAfter: new Date(Date.now() + 86400000).toISOString(),
  }, {
    kid: 'journey-bridge-probe.local.1', caller: 'journey-bridge-probe', kind: 'service', jwk: bridge.publicKey.export({ format: 'jwk' }),
    grants: ['catalog.read'], notAfter: new Date(Date.now() + 86400000).toISOString(),
  }, {
    kid: 'ts-auth.local.1', caller: 'ts-auth', kind: 'service', jwk: publicKey.export({ format: 'jwk' }),
    grants: ['data:00000000000000000000000002'], notAfter: new Date(Date.now() + 86400000).toISOString(),
  }, {
    kid: 'dashboard.local.1', caller: 'dashboard', kind: 'service', jwk: publicKey.export({ format: 'jwk' }),
    grants: ['catalog.read'], notAfter: new Date(Date.now() + 86400000).toISOString(),
  }, {
    kid: 'ts-auth.local.0', caller: 'ts-auth', kind: 'service', jwk: publicKey.export({ format: 'jwk' }),
    grants: ['data:00000000000000000000000002'], notAfter: new Date(Date.now() - 86400000).toISOString(),
  }, {
    kid: 'op:journey.local.1', caller: 'op:journey', kind: 'operator', jwk: publicKey.export({ format: 'jwk' }),
    grants: ['platform.read'], notAfter: new Date(Date.now() + 86400000).toISOString(),
  }],
}), { mode: 0o600 });
