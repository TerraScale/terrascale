// Local development only: TerraBase and ts-auth on this machine, with the Aspire dashboard. Production runs on Fly.io.
// Ports are the services' fixed off-Fly ports, so ts-auth finds TerraBase at 127.0.0.1:8081 as it does without Aspire.
// Everything is disposable: each run starts from an empty TerraBase data directory and a fresh ts-auth master key that is
// never written to disk.

import { randomBytes } from 'node:crypto';
import { mkdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();
// Emptied at start, not at stop: Aspire ends the AppHost with a signal, so cleanup on stop never runs.
const dataDirectory = join(tmpdir(), 'terrascale-aspire-terrabase');
rmSync(dataDirectory, { recursive: true, force: true });
mkdirSync(dataDirectory);

const terrabase = await builder.addProject('terrabase', '../src/TerraBase/TerraBase.csproj')
    .withEnvironment('DOTNET_ENVIRONMENT', 'Development')
    .withEnvironment('TERRABASE_DATA_DIRECTORY', dataDirectory)
    .withHttpEndpoint({ name: 'http', port: 8080, targetPort: 8080, isProxied: false })
    .withHttpEndpoint({ name: 'internal', port: 8081, targetPort: 8081, isProxied: false })
    .withHttpEndpoint({ name: 'metrics', port: 9091, targetPort: 9091, isProxied: false })
    .withHttpHealthCheck({ path: '/readyz', endpointName: 'http' });

const masterKey = await builder.addParameter('auth-master-key', { value: randomBytes(32).toString('base64'), secret: true });

// The platform issuer's first signing key comes only from this operator command (docs/auth/signing-keys.md). It targets
// this run's TerraBase (ts-auth off Fly stores at 127.0.0.1:8081) and never starts by itself: start it from the dashboard.
const signingKey = await builder.addProject('auth-signing-key', '../src/TerraScale.Authentication/TerraScale.Authentication.csproj')
    .withArgs(['provision-signing-key'])
    .withEnvironment('TS_AUTH_ENVIRONMENT', 'Development')
    .withEnvironment('TS_AUTH_MASTER_KEY', masterKey)
    .withExplicitStart()
    .waitFor(terrabase);

// Without a signing key /readyz answers 503 "no signing keys", so auth shows unhealthy until auth-signing-key has run.
await builder.addProject('auth', '../src/TerraScale.Authentication/TerraScale.Authentication.csproj')
    .withEnvironment('TS_AUTH_ENVIRONMENT', 'Development')
    .withEnvironment('TS_AUTH_MASTER_KEY', masterKey)
    .withEnvironment('TS_AUTH_LISTEN', 'http://127.0.0.1:8090')
    .withEnvironment('TS_AUTH_METRICS_LISTEN', 'http://127.0.0.1:9092')
    .withEnvironment('TS_AUTH_PUBLIC_ORIGIN', 'http://localhost:8090')
    .withHttpEndpoint({ name: 'http', port: 8090, targetPort: 8090, isProxied: false })
    .withHttpEndpoint({ name: 'metrics', port: 9092, targetPort: 9092, isProxied: false })
    .withHttpHealthCheck({ path: '/readyz', endpointName: 'http' })
    .waitFor(terrabase);

await builder.build().run();
