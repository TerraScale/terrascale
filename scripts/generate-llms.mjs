// Generate the public docs map and a fixed allowlist of downloadable examples.
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const check = process.argv.includes('--check');
const routes = [
  ['/', 'Developer documentation'],
  ['/getting-started/', 'Current quickstarts and service availability'],
  ['/getting-started/javascript', 'Published JavaScript SDK 0.0.3 installation and CRUD/query example'],
  ['/getting-started/csharp', 'C# candidate package-only quickstart; public NuGet release unavailable'],
  ['/guides/sdks', 'Supported runtimes, public package availability and current SDK contracts'],
  ['/guides/authentication', 'Scoped credentials, server/browser boundaries and authorization availability'],
  ['/guides/queries', 'JSON filters, projection, opaque pagination and version preconditions'],
  ['/guides/errors', 'Typed errors, SDK retry behavior and uncertain mutation reconciliation'],
  ['/guides/dashboard', 'Customer dashboard and application database workflow'],
  ['/guides/zero-configuration', 'Public always-on operating contract and secret-store delivery'],
  ['/concepts/replication', 'Current write results and separate multi-region qualification boundary'],
  ['/concepts/sync', 'Shape reads and legacy live-update compatibility'],
  ['/concepts/sql', 'Separate SQL entrypoint and contract compatibility'],
  ['/pricing/', 'Pricing overview'], ['/pricing', 'Generated canonical plan catalog'],
  ['/limits', 'Documents API limits and proposed plan safety policies'],
  ['/snippets', 'Current examples and legacy fixture boundaries'],
  ['/changelog', 'Documentation changes, not service release announcements'],
];
const text = `# TerraScale documentation

> TerraBase stores project-scoped JSON documents. The JavaScript SDK
> @terrascale/sdk@0.0.3 is published. C# and Go candidate availability and
> hosted authorization must be verified independently. A package release
> does not establish public self-service launch or multi-region guarantees.

## Documentation

${routes.map(([route, title]) => `- [${title}](https://docs.terrascale.tech${route})`).join('\n')}

## Current contract

- Documents use JSON, opaque versions and conditional writes.
- Queries use JSON filters and opaque continuation cursors, not legacy SQL.
- JavaScript and C# send once. Mutation deduplication and Idempotency-Key
  are unsupported. Reconcile a lost response before replaying a write.
- Configure an authorized HTTPS endpoint or caller-supplied resolver.
- Do not infer production JWT authorization, leaderless guarantees,
  automatic endpoint discovery or hosted readiness from a local test.
`;
const outputs = [['docs/public/llms.txt', Buffer.from(text)]];
for (const [folder, files] of [
  ['terrabase-js', ['quickstart.mjs', 'package.json']],
  ['terrabase-dotnet', ['Program.cs', 'TerraBaseStarter.csproj']],
]) {
  for (const file of files) outputs.push([
    `docs/public/examples/${folder}/${file}`,
    readFileSync(join(root, 'examples', folder, file)),
  ]);
}
for (const [name, content] of outputs) {
  const path = join(root, name);
  if (check) {
    if (!readFileSync(path).equals(content)) throw new Error(`Generated public asset is stale: ${name}`);
  } else {
    mkdirSync(dirname(path), { recursive: true });
    writeFileSync(path, content);
  }
}
console.log(`Public map and ${outputs.length - 1} example assets ${check ? 'verified' : 'generated'}.`);
