# Landing repository cutover

- Original main: `a6fa77f9b12a312dae75cf846dbd88d657b9ef9a`.
- Original main tree: `8f34f776c1b227cd67cc1b2ba3aab32a1ed7d82b`. The import commit's `landing-site/` tree matches exactly.
- Import-copy main after credential redaction: `0822d88428d058b934bf550ed2783c6348473bf4`. Main tree unchanged.
- All 927 commits retained through git-filter-repo --replace-text; commit map saved beside the original bundle on delta.
- All 21 branches and 67 PR refs copied under `refs/archive/ts-landing/{heads,pull}/` and verified by object ID. There were no source tags, issues, or releases.
- Original mirror bundle: `delta:/home/mariogk/Projects/TerraScale/.cos/monorepo-20261029/ts-landing.bundle`, mode 0600, full bundle verification passed.
- Bundle SHA-256: `8081f2f6df0abac265ee54a505db17796e9180f8f0de14988e74afd5c6d9f7e1`.
- Credential redacted: GitHub classic PAT (`ghp_`), historical `.roo/mcp.json:9`, original commit `47b8ffe8d45d1b56a3606ecdbdb7bd54e3391ac0`. No API was called with that token; user handles revocation.
- Intentional post-import changes: monorepo deployment docs, generated Go discovery checks, `/sdk` metadata for Go 1.25+ subdirectory discovery, `/install.sh` monorepo redirect. Shared `/pricing.json` and `/regions.json` unchanged.
- `make landing-test-fast` installs locked dependencies, runs existing tests, builds and validates the production static output. ts-ship maps landing-site/ and landing workflows to that target on delta, beta and mariogk-dev.
