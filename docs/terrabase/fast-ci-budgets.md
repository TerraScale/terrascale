# Fast CI budgets

`ts-ship` builds and runs the affected Fast suites on mariogk-dev or beta before pushing main. Each suite has a separate 120-second test deadline after compilation. Each .NET Fast test must finish within 10 seconds. The runner retains TRX results and prints the ten slowest tests. An over-budget result fails shipping and publishes a failure status; it cannot qualify CI.

The core Makefile targets use `scripts/ci/fast-budget.py`, so direct Fast runs and Docker test stages enforce the same test-duration limits. Docker test stages also have a 120-second deadline. Tests retain their existing cases and use TUnit default parallelism and automatic CPU/GC discovery. Compiler CPU limits apply only during compilation.

For Libraries, Auth and TerraBase, shipping also runs the checked Release build, formatting and static checks. Auth retains its audit and OpenAPI checks. TerraBase retains its analyzer-negative check, native single-file execution and local integration proof. These checks run on the existing Fast outputs before packaging, on a dev host, with a separate build deadline. The existing Cloudflare token and CI bucket handle artifact transport; registry publication keeps the existing Actions package permission.

`scripts/ci/qualified-build.py` retains both checked runtime architectures privately in the existing R2 CI bucket, along with a source-bound metadata receipt and check log. The `ci-build/<area>` status records the manifest's SHA-256. The receipt contains the private payload key and SHA-256, its area and its exact source revision. CI verifies the payload before extracting it and wraps those bytes in the existing runtime-only image. No TerraBase binaries are published as release assets.

CI requires both the latest successful `ts-ship/<area>` status within the current budgets and the latest successful `ci-build/<area>` status, authored by MarioGK for the exact commit. It rejects missing, pending, failed, old-budget or mismatched proof. A checked payload cannot be carried across a rebase: the new commit must qualify its own runtime bytes. Fast itself still executes before shipping; retained results are not used to omit a required test run.

The three Fast verification jobs have two-minute job deadlines. Four TerraBase shards and the duplicate hosted builds and packaging jobs are removed. Deployment publishes the checked runtime payload and keeps the existing regional release safeguards. `CI timing budget` fails a normal run exceeding six minutes, including setup and deployment, or any Fast job exceeding two minutes. Slow and explicit native maintenance remain independent.

To reproduce a budget rejection, use a disposable branch, add an 11-second delay to a retained Fast test, commit and run `ts-ship`. It must report `RED` for the test budget and leave main unchanged. Restore the test before shipping the actual change.
