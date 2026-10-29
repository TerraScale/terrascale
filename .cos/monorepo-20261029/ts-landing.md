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

## Live and deletion proof

- Shipped import and deployment: `39538865320dc39d00e928396e6e389f3d40bf22`.
- Landing Fast: 19.960 seconds on a dev host through ts-ship/ts-heavy-any.
- Main CI, Landing, Landing secret scan, and Legacy scan passed for that commit.
- Deploy run: https://github.com/TerraScale/terrascale/actions/runs/40972389910 .
- `https://terrascale.tech/build-info.json` returned revision `39538865320dc39d00e928396e6e389f3d40bf22`.
- Cloudflare deployment `ee9651a8-5008-4e72-8d16-209fdd772f70`, Worker version `2a807cd6-a572-462f-ade0-655ced8ad994`, created 2026-10-29T23:20:41.788426Z.
- `https://terrascale.tech/sdk?go-get=1` returned the monorepo subdirectory metadata; the parent received the live SDK HANDOFF.
- Live `/pricing.json` matches the committed catalog exactly.
- `gh repo delete TerraScale/ts-landing --yes` completed. Authenticated `gh api repos/TerraScale/ts-landing` and the public GitHub repository URL both returned HTTP 404.
