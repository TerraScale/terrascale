# Fast CI budgets

`ts-ship` builds and runs the affected Fast suites on delta, mariogk-dev or beta before pushing main. Each area has a separate 30-second test budget after compilation, and each Fast test has a 10-second budget. Both currently produce named warnings until issue #1084 brings the suites within policy. The runner retains TRX results and prints the ten slowest tests. The existing 120-second suite cap, functional failures and missing timing reports still reject shipping.

The core Makefile targets use `scripts/ci/fast-budget.py`, so direct Fast runs and Docker test stages report the same test-duration budgets. JavaScript, Python, Go and Rust SDK runs also retain individual timings and print their ten slowest tests. Tests retain their existing cases and use TUnit default parallelism and automatic CPU/GC discovery. Compiler CPU limits apply only during compilation.

For Libraries, Auth and TerraBase, shipping also runs the checked Release build, formatting and static checks. Auth retains its audit and OpenAPI checks. TerraBase retains its analyzer-negative check, native single-file execution and local integration proof. These checks run on the existing Fast outputs before packaging, on a dev host, with a separate build deadline. The existing Cloudflare token and CI bucket handle artifact transport; registry publication keeps the existing Actions package permission.

`scripts/ci/qualified-build.py` retains both checked runtime architectures privately in the existing R2 CI bucket, along with a source-bound metadata receipt and check log. The `ci-build/<area>` status records the manifest's SHA-256. The receipt contains the private payload key and SHA-256, its area and its exact source revision. CI verifies the payload before extracting it and wraps those bytes in the existing runtime-only image. No TerraBase binaries are published as release assets.

CI requires both the latest successful `ts-ship/<area>` status within the current budgets and the latest successful `ci-build/<area>` status, authored by MarioGK for the exact commit. It rejects missing, pending, failed, old-budget or mismatched proof. A checked payload cannot be carried across a rebase: the new commit must qualify its own runtime bytes. Fast itself still executes before shipping; retained results are not used to omit a required test run.

The three Fast verification jobs have two-minute job deadlines. Four TerraBase shards and the duplicate hosted builds and packaging jobs are removed. Deployment publishes the checked runtime payload and keeps the existing regional release safeguards. `CI timing budget` fails a normal run exceeding six minutes, including setup and deployment, or any Fast job exceeding two minutes. Slow and explicit native maintenance remain independent.

To reproduce a budget rejection, use a disposable branch, add an 11-second delay to a retained Fast test, commit and run `ts-ship`. With rejection enabled, it must report `RED` for the test budget and leave main unchanged. During the temporary warning rollout it reports the named overrun while still rejecting functional failures. Restore the test before shipping the actual change.

The 30-second suite and 10-second test budgets currently emit named warnings until the fixture fixes in issue #1084 make every suite comply; both then return to rejection.
