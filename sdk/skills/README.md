# TerraScale agent skills

Agent skills for migrating to TerraScale and using it in your applications. Each
skill is an independent set of instructions installed with Vercel’s
[skills CLI](https://github.com/vercel-labs/skills).

All 17 skills below are ready to install, individually or by family.

## Install

Requires Node.js and npm. Run from the project where your agent works:

```sh
npx -y skills add https://github.com/TerraScale/terrascale/tree/main/sdk/skills
npx -y skills add https://github.com/TerraScale/terrascale/tree/main/sdk/skills --skill supabase-to-terrascale
npx -y skills add https://github.com/TerraScale/terrascale/tree/main/sdk/skills --list
```

The first command opens skill selection. `--skill` selects one or more named
skills; `--list` shows available skills without installing.
`npx -y skills list` lists skills already installed.

Add `--agent codex` or `--agent claude-code` to choose an agent, and `--global`
to install for your user instead of the current project. The CLI supports Codex,
Claude Code, Cursor, GitHub Copilot, Gemini CLI, OpenCode, Windsurf, and
[other agents](https://github.com/vercel-labs/skills/blob/main/docs/agents.md).
See the [CLI reference](https://github.com/vercel-labs/skills/blob/main/docs/commands.md)
for all installation options.

## Skills

Ready means the skill instructions are available. Check each skill's prerequisites
and release gates before use.

### Supabase

Install just this family:

```sh
npx -y skills add https://github.com/TerraScale/terrascale/tree/main/sdk/skills --skill supabase-to-terrascale supabase-database-to-terrascale supabase-auth-to-terrascale supabase-storage-to-terrascale supabase-edge-functions-to-terrascale supabase-client-to-terrascale
```

| Skill | Purpose | Status |
| --- | --- | --- |
| [supabase-to-terrascale](supabase-to-terrascale/SKILL.md) | Inventory a Supabase project's services and clients, then plan, rehearse and cut over to TerraScale. | ready |
| [supabase-database-to-terrascale](supabase-database-to-terrascale/SKILL.md) | Migrate Supabase schemas and data with SQL conversion, authorization replacement, reconciliation and repeatable migrations. | ready |
| [supabase-auth-to-terrascale](supabase-auth-to-terrascale/SKILL.md) | Migrate Supabase users, password hashes, social providers and sessions with user ID mapping, verification and rollback. | ready |
| [supabase-storage-to-terrascale](supabase-storage-to-terrascale/SKILL.md) | Migrate Supabase Storage buckets, objects, policies and file URLs to TerraScale. | ready |
| [supabase-edge-functions-to-terrascale](supabase-edge-functions-to-terrascale/SKILL.md) | Migrate Supabase Edge Functions to TerraScale Functions, including Deno rewrites, callers, cron schedules and webhook gateways. | ready |
| [supabase-client-to-terrascale](supabase-client-to-terrascale/SKILL.md) | Rewrite Supabase client integrations to TerraScale SQL and documented ORMs. | ready |

### Firebase

Install just this family:

```sh
npx -y skills add https://github.com/TerraScale/terrascale/tree/main/sdk/skills --skill firebase-to-terrascale firestore-to-terrascale firebase-auth-to-terrascale
```

| Skill | Purpose | Status |
| --- | --- | --- |
| [firebase-to-terrascale](firebase-to-terrascale/SKILL.md) | Inventory Firebase services, plan data and identity changes, replace storage and functions, and coordinate client cutover and rollback. | ready |
| [firestore-to-terrascale](firestore-to-terrascale/SKILL.md) | Migrate Firestore documents and Realtime Database JSON with typed exports, relational modeling, SQL imports and query and rule replacement. | ready |
| [firebase-auth-to-terrascale](firebase-auth-to-terrascale/SKILL.md) | Migrate Firebase Authentication or Identity Platform users, providers, custom claims, token checks and security rules. | ready |

### Other databases

Install just this family:

```sh
npx -y skills add https://github.com/TerraScale/terrascale/tree/main/sdk/skills --skill postgres-to-terrascale turso-to-terrascale sqlite-d1-to-terrascale planetscale-to-terrascale
```

| Skill | Purpose | Status |
| --- | --- | --- |
| [postgres-to-terrascale](postgres-to-terrascale/SKILL.md) | Migrate PostgreSQL from Neon, AWS RDS or Aurora, Google Cloud SQL, Azure or a self-hosted server to TerraScale. | ready |
| [turso-to-terrascale](turso-to-terrascale/SKILL.md) | Migrate Turso databases, per-tenant fleets and client code to TerraScale. | ready |
| [sqlite-d1-to-terrascale](sqlite-d1-to-terrascale/SKILL.md) | Migrate SQLite files and Cloudflare D1 databases, including Worker SQL calls and recovery planning. | ready |
| [planetscale-to-terrascale](planetscale-to-terrascale/SKILL.md) | Migrate PlanetScale Vitess, MySQL and MariaDB with consistent exports, schema and SQL translation, imports, driver changes and verified cutover. | ready |

### Using TerraScale

Install just this family:

```sh
npx -y skills add https://github.com/TerraScale/terrascale/tree/main/sdk/skills --skill terrascale-getting-started terrascale-migrations-and-branches terrascale-orm-setup terrascale-sdks
```

| Skill | Purpose | Status |
| --- | --- | --- |
| [terrascale-getting-started](terrascale-getting-started/SKILL.md) | Set up TerraScale from an empty project through a working SQL application. | ready |
| [terrascale-migrations-and-branches](terrascale-migrations-and-branches/SKILL.md) | Manage TerraScale schema migrations and database branches, including a database per pull request in CI. | ready |
| [terrascale-orm-setup](terrascale-orm-setup/SKILL.md) | Configure TerraScale ORM and typed SQL integrations with explicit schema ownership. | ready |
| [terrascale-sdks](terrascale-sdks/SKILL.md) | Write TerraScale SDK queries, batches and transactions in JavaScript, Python, Go, Rust or .NET. | ready |

## Contributing

Follow [AGENTS.md](../../AGENTS.md). Skills live at `sdk/skills/<name>/SKILL.md`; references
and optional scripts stay inside that skill’s folder. The CLI discovers this
layout without a manifest; see its
[authoring and discovery rules](https://github.com/vercel-labs/skills/blob/main/docs/creating-skills.md).

Run the same checks as CI:

```sh
node sdk/skills/validate-skills.mjs
node sdk/skills/validate-skills.mjs --cli-list
```

The second command runs `npx -y skills add . --list` and checks the discovered
names. Only an empty catalog may return the CLI’s explicit no-skills result.

MIT licensed.
