// Generate the public docs map and a fixed allowlist of downloadable examples.
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const check = process.argv.includes('--check');
const routes = [
  ['/', 'Developer documentation'],
  ['/getting-started/', 'Current quickstarts and service availability'],
  ['/getting-started/javascript', 'Published JavaScript SDK 0.0.13 installation and CRUD/query example'],
  ['/getting-started/csharp', 'C# quickstart with the SDK package built from source'],
  ['/getting-started/go', 'Go SDK installation and document, query, SQL and changes quickstart'],
  ['/getting-started/python', 'Python SDK installation and sync/async document, query, SQL and vector quickstart'],
  ['/getting-started/browser-todos', 'Browser todos with an existing end-user session and collection owner rules'],
  ['/getting-started/browser-backend-tokens', 'Connect an existing app backend session to project-scoped end-user tokens'],
  ['/cli/management', 'CLI organization/project lifecycle, backup settings, placement and resumable changes'],
  ['/cli/sql-and-export', 'CLI multiline SQL submission, session-only history and document-only export limits'],
  ['/cli/access', 'CLI sign-in, key scopes, saved context, consent, identity-conditioned deletion and dashboard billing'],
  ['/cli/automation', 'Go CLI typed JSON, safe errors, exit codes and NDJSON terminal records'],
  ['/guides/sdks', 'Supported runtimes, installation and SDK contracts'],
  ['/guides/authentication-capabilities', 'Current authentication methods, protocol scope and availability'],
  ['/guides/authentication', 'API keys, token exchange and scoped access'],
  ['/guides/queries', 'JSON filters, projection, opaque pagination and version preconditions'],
  ['/guides/libsql-clients', 'Run SQL from the official libSQL clients (JavaScript, Go, Rust, Python)'],
  ['/guides/orm-drizzle', 'Use Drizzle ORM: schema, drizzle-kit migrations, queries and transactions'],
  ['/guides/orm-prisma', 'Use Prisma ORM 7 with the libSQL adapter: schema, migrations, queries and transactions'],
  ['/guides/orm-sqlalchemy', 'Use SQLAlchemy 2 with sqlalchemy-libsql: models, create_all, queries and transactions'],
  ['/guides/errors', 'Typed errors, SDK retry behavior and uncertain mutation reconciliation'],
  ['/guides/rename-database', 'Rename by immutable database ID, connection strings and name conflicts'],
  ['/guides/dashboard', 'Customer dashboard and application database workflow'],
  ['/guides/functions', 'Deploy JavaScript functions, call them over HTTPS, trigger them on changes or cron, read logs'],
  ['/guides/file-storage', 'Project file buckets, signed uploads and downloads, owner and role permissions, and shared storage allowances'],
  ['/guides/mcp', 'Connect MCP clients with OAuth sign-in or a personal access token and discover permitted tools'],
  ['/guides/single-sign-on', 'Organization single sign-on over OIDC: verified domains, role defaults and the hosted sign-in flow'],
  ['/legal/data-deletion', 'Deletion, changes-feed and backup retention timelines'],
  ['/legal/sla', 'No SLA during Public Alpha; measured uptime and publication prerequisites'],
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
> @terrascale/sdk@0.0.13 is on npm, the Go module terrascale.tech/sdk@v0.0.3
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
  ['terrabase-js', ['quickstart.mjs', 'package.json', 'follow-changes.mjs']],
  ['terrabase-dotnet', ['Program.cs', 'TerraBaseStarter.csproj']],
  ['server-token-provider', ['server-client.mjs', 'package.json']],
  ['typed-models', ['package.json', 'schema.json', 'verify-models.mjs', 'model-types.ts', 'tsconfig.json', 'live.ts']],
]) {
  for (const file of files) outputs.push([
    `docs/public/examples/${folder}/${file}`,
    readFileSync(join(root, 'examples', folder, file)),
  ]);
}
outputs.push([
  'docs/public/examples/server-token-provider/README.txt',
  readFileSync(join(root, 'examples/server-token-provider/README.md')),
]);
outputs.push(['docs/public/examples/typed-models/README.txt', readFileSync(join(root, 'examples/typed-models/README.md'))]);
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
