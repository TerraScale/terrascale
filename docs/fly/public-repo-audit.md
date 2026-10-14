# Public repository secret audit (2026-10-14)

TerraScale/terrascale became public on 2026-10-14. Before and right after the switch, gitleaks 8.30.1 and trufflehog 3.95.3 scanned:

- every commit on every branch, tag and `refs/pull/*`
- every issue and PR body, issue comment and review comment
- the Actions logs of the 150 latest runs, plus every run whose `preview` job ran

| Source | Result |
|---|---|
| Git history | 0 live secrets. Hits are SHA-256 digests, ULIDs, GPG fingerprints, git SHAs and test fixtures (dummy keys, `.example`/`.test` URLs). |
| Issue and PR text | 0 hits. |
| Actions logs | `scripts/fly/preview.sh` printed each PR preview's Tigris bucket credentials (`flyctl storage create` stdout). The affected runs are deleted, the output is silenced (#286) and the keys are being revoked. |

Guards now in place:

- fork PRs need approval before workflows run
- the `preview` job runs only for same-repository PRs
- deploys run only on pushes to `main`
- `main` requires a PR, the `test` check and the merge queue
