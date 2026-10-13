# Dashboard production rollout

CI and Slow must both pass for the current main commit. CI builds the image
once, publishes `registry.fly.io/ts-dashboard:<commit>`, and retains Docker,
NuGet and npm caches. Deployment starts from workflow completion events rather
than polling or sleeping. A newer main commit supersedes an older candidate.

`scripts/fly-rollout.sh` reuses that image in gru, sjc and fra, in that order.
It creates missing regional capacity from the app config and retains exactly
one always-running shared-cpu-2x machine with 512 MB RAM and 4 GB swap per region.
Each region must report readiness, the exact tested commit, and its Fly region
before the next region is provisioned. No dashboard volumes or local data stores
exist.

Dashboard-owned persistent state uses the official ts-dotnet Documents SDK and
the pre-created `ts-dashboard` database (`00000000000000000000000004`) under
project `00000000000000000000000005`. Collection initialization never creates
or replaces a database. Conditional dashboard writes retain gru as their
authority; TerraBase owns replication to sjc and fra. ts-auth owns users, the
TerraScale organization, its owner and the two project identities.

First-party secrets are generated directly into Fly and GitHub secrets without
printing their values. GitHub holds an app-scoped `FLY_API_TOKEN`. Stripe issues
`STRIPE_SECRET_KEY` and `STRIPE_WEBHOOK_SECRET`; generated placeholders cannot
replace them. See [identity-descriptor.md](identity-descriptor.md).

## Timing and proof

Every successful regional check prints `LIVE region=<region> sha=<commit>` and
`MERGE_TO_LIVE region=<region> seconds=<seconds> sha=<commit>`. The latter is
measured from the main commit's committer timestamp, including CI and image
publication. The final machine inventory must show precisely gru, sjc and fra.

The previous workflows built a disposable image and did not deploy it. There
is no successful automated merge-to-three-region-live baseline to quote.
The measured after time and native login/convergence proof remain pending T2's
TerraBase deployment, T3's auth bootstrap, and valid Stripe secrets. Record the
exact commit and workflow URL with the actual timings after that rollout.
