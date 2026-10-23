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
  ['/getting-started/csharp', 'C# quickstart with the SDK package built from source'],
  ['/getting-started/go', 'Go SDK installation and document, query, SQL and changes quickstart'],
  ['/guides/sdks', 'Supported runtimes, installation and SDK contracts'],
  ['/guides/authentication', 'API keys, token exchange and scoped access'],
  ['/guides/queries', 'JSON filters, projection, opaque pagination and version preconditions'],
  ['/guides/errors', 'Typed errors, SDK retry behavior and uncertain mutation reconciliation'],
  ['/guides/dashboard', 'Customer dashboard and application database workflow'],
  ['/guides/mcp', 'Connect MCP clients with a personal access token and discover permitted tools'],
  ['/legal/data-deletion', 'Deletion, changes-feed and backup retention timelines'],
  ['/guides/zero-configuration', 'Public always-on operating contract and secret-store delivery'],
  ['/concepts/replication', 'Write durability and multi-region behavior'],
  ['/concepts/sync', 'Shape reads and legacy live-update compatibility'],
  ['/concepts/sql', 'Separate SQL entrypoint and contract compatibility'],
  ['/pricing/', 'Pricing overview'], ['/pricing', 'Generated canonical plan catalog'],
  ['/limits', 'Documents API limits and proposed plan safety policies'],
  ['/snippets', 'Current examples and legacy fixture boundaries'],
  ['/changelog', 'Documentation changes, not service release announcements'],
];
const text = `# TerraScale documentation

> TerraBase stores project-scoped JSON documents. The JavaScript SDK
> @terrascale/sdk@0.0.3 is on npm, the Go module terrascale.tech/sdk@v0.0.1
> is on the Go proxy, and the C# package builds from source. Exchange a
> dashboard API key at https://auth.terrascale.tech/terrascale/oauth/token and
> call https://api.terrascale.tech.

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
