// Local development only: TerraBase and ts-auth on this machine, with the Aspire dashboard. Production runs on OCI under ts-orch.
// Everything is disposable: each run starts from an empty TerraBase data directory and a fresh ts-auth master key that is
// never written to disk.

import { randomBytes } from 'node:crypto';
import { mkdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();
const instance = Number(process.env.TS_DEV_INSTANCE ?? '0');
if (!Number.isInteger(instance) || instance < 0) {
    throw new Error(`TS_DEV_INSTANCE must be a non-negative integer, got '${process.env.TS_DEV_INSTANCE}'`);
}
const port = (base: number) => base + 100 * instance;
const suffix = instance === 0 ? '' : `-${instance}`;
// Emptied at start, not at stop: Aspire ends the AppHost with a signal, so cleanup on stop never runs.
const dataDirectory = join(tmpdir(), `terrascale-aspire-terrabase${suffix}`);
rmSync(dataDirectory, { recursive: true, force: true });
mkdirSync(dataDirectory);

const terrabase = await builder.addProject('terrabase', '../src/TerraBase/TerraBase.csproj')
    .withEnvironment('DOTNET_ENVIRONMENT', 'Development')
    .withEnvironment('TERRABASE_DATA_DIRECTORY', dataDirectory)
    .withEnvironment('TERRABASE_PORT', `${port(8080)}`)
    .withEnvironment('TERRABASE_INTERNAL_PORT', `${port(8081)}`)
    .withEnvironment('TERRABASE_METRICS_PORT', `${port(9091)}`)
    .withEnvironment('TERRABASE_POSTGRES_PORT', `${port(5432)}`)
    .withHttpEndpoint({ name: 'http', port: port(8080), targetPort: port(8080), isProxied: false })
    .withHttpEndpoint({ name: 'internal', port: port(8081), targetPort: port(8081), isProxied: false })
    .withHttpEndpoint({ name: 'metrics', port: port(9091), targetPort: port(9091), isProxied: false })
    .withHttpHealthCheck({ path: '/readyz', endpointName: 'http' });

const masterKey = await builder.addParameter('auth-master-key', { value: randomBytes(32).toString('base64'), secret: true });

// The platform issuer's first signing key comes only from this operator command (docs/auth/signing-keys.md). It targets
// this run's TerraBase and never starts by itself: start it from the dashboard.
const signingKey = await builder.addProject('auth-signing-key', '../src/TerraScale.Authentication/TerraScale.Authentication.csproj')
    .withArgs(['provision-signing-key'])
    .withEnvironment('TS_AUTH_ENVIRONMENT', 'Development')
    .withEnvironment('TS_AUTH_MASTER_KEY', masterKey)
    .withEnvironment('TS_AUTH_TERRABASE_URL', `http://127.0.0.1:${port(8081)}`)
    .withExplicitStart()
    .waitFor(terrabase);

// Without a signing key /readyz answers 503 "no signing keys", so auth shows unhealthy until auth-signing-key has run.
await builder.addProject('auth', '../src/TerraScale.Authentication/TerraScale.Authentication.csproj')
    .withEnvironment('TS_AUTH_ENVIRONMENT', 'Development')
    .withEnvironment('TS_AUTH_MASTER_KEY', masterKey)
    .withEnvironment('TS_AUTH_TERRABASE_URL', `http://127.0.0.1:${port(8081)}`)
    .withEnvironment('TS_AUTH_LISTEN', `http://127.0.0.1:${port(8090)}`)
    .withEnvironment('TS_AUTH_METRICS_LISTEN', `http://127.0.0.1:${port(9092)}`)
    .withEnvironment('TS_AUTH_PUBLIC_ORIGIN', `http://localhost:${port(8090)}`)
    .withHttpEndpoint({ name: 'http', port: port(8090), targetPort: port(8090), isProxied: false })
    .withHttpEndpoint({ name: 'metrics', port: port(9092), targetPort: port(9092), isProxied: false })
    .withHttpHealthCheck({ path: '/readyz', endpointName: 'http' })
    .waitFor(terrabase);

await builder.build().run();
