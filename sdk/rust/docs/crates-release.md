# crates.io releases

The `Rust` workflow retains the `test` check and runs formatting, HTTP-fake and
conformance tests, Clippy, and credential-free
`cargo publish --dry-run --locked --all-features --registry crates-io` on Rust PRs,
merge groups, and release tags. The dry-run builds the unpacked crate; it does
not upload it. No TerraBase binary or production credential is used by tests.

Publication runs only for a pushed `sdk/rust/v0.0.X` tag. The tag must exactly match the
`terrascale` package version in `Cargo.toml`, and its commit must equal current
`origin/main` after qualification. Branch pushes, PRs, scheduled runs, manual
runs, and tag deletions cannot publish. Tags with suffixes or leading zeroes in
the patch number are rejected. Workflow actions are pinned to commit SHAs.

1. Ship the release changes to main with `ts-ship` and green CI. Keep versions at `0.0.X`;
   update both `Cargo.toml` and `Cargo.lock` in the release commit. Clear any pending
   release holds before creating a tag.
2. The release owner installs their authorized crates.io publishing token with
   this one command, entering it at the hidden prompt:

   ```sh
   gh secret set CARGO_REGISTRY_TOKEN --repo TerraScale/terrascale
   ```

   Do not pass `--body`, pipe a token, or paste one into chat. Agents do not
   create, read, copy, or install the credential.
3. After explicit publication authorization, the owner creates and pushes
   `sdk/rust/v<package-version>` at current main. Wait for the `Rust` workflow to finish
   and verify the exact version on crates.io before claiming availability.
   If main advances during qualification, publication fails; use a fresh patch
   release from current main rather than moving an existing release tag.

As of 2026-10-23, the crates.io API returned 404 for `terrascale`; no published
availability is claimed here. TS-801 is Done on the routing proof in PR #89;
its authenticated successful-query run was deferred by its owner. This package
dry-run proves packaging and compilation, not production API qualification.
Publishing remains a separate owner action requiring the registry credential
and a future tag. No tag or publication is needed to validate this PR.
