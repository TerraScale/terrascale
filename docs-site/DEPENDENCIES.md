# Build and example dependency compatibility

The site remains on VitePress 1.6.4. Its Cloudflare tooling uses `cf`
1.0.0-beta.21 and the matching build-output-utils 0.9.4. This continues the
existing cf prerelease line; CI uses Node 24.22.1 to satisfy its updated engine
requirement. The prebuilt Workers output, branch previews, publication checks
and deployment conditions remain covered by the existing workflow and tests.

Cloudflare's current parents still pin affected transitive versions. Scoped
npm overrides select zod 4.6.7 under `@cloudflare/config` and undici 7.30.1
under miniflare, retaining their existing major versions.

The Drizzle example retains the latest stable drizzle-kit 0.31.11 and
drizzle-orm 0.45.4. Its deprecated `@esbuild-kit/core-utils` dependency pins
esbuild 0.18.20. A scoped override shares esbuild 0.25.12 with drizzle-kit's
supported 0.25 line. The regenerated lock removes the obsolete nested binary
packages. CI runs migration generation and isolated in-memory libSQL checks
for migrations, relations, CRUD and transaction rollback. These checks do not
establish compatibility with a deployed TerraBase service.

CI audits every tracked `package-lock.json` before installation, including all
examples and the application generator. Registry/scanner failures and high or
critical findings fail the existing job. Lower severities remain visible.
The npm download cache covers all locks; advisory results are fetched anew on
each run. Dependabot covers the same six current lockfile directories.

To check every severity in a single project, run `npm audit
--package-lock-only --ignore-scripts` from its directory. To reproduce CI's
threshold, add `--audit-level=high`. No advisory is suppressed.
