# Automatic tagged NuGet releases

This replaces the manual Trusted Publishing instructions in `package-release.md`.
Versions remain `0.0.X`; this change does not bump the version, create a tag or
publish a package.

## Credential handoff

The repository owner installs the approved nuget.org API key using this command:

```sh
gh secret set NUGET_API_KEY --repo TerraScale/terrascale
```

Paste the key only into gh's hidden prompt. Do not pass `--body`, put the key in
chat, or have an agent install it. Use a key restricted to publishing
`TerraScale.DotNet`; the owner handles provisioning, expiry and rotation.
GitHub Packages continues to use the workflow's `GITHUB_TOKEN`.

## Prepare without credentials

Run the release workflow from the monorepo:

```sh
gh workflow run sdk-dotnet-release.yml --repo TerraScale/terrascale --ref main
```

Use `--ref main` after the change ships. Manual dispatch runs only `package`: version
validation, locked release build/tests and reproducible packaging. It uploads
the qualified `.nupkg` and `provenance.json` as the `package` artifact, with no
registry writes, nuget.org secret or GitHub Packages access. An Actions token
is still needed to dispatch and download workflow artifacts. CI's existing
`test` check also qualifies the package, including workflow-only changes.

From a clean committed checkout, run qualification on a dev host with `ts-heavy-any`; commands within that job use `sdk/dotnet` as their working directory:

```sh
cd sdk/dotnet
python3 scripts/pack-reproducible.py /tmp/ts-dotnet-qualified-package
```

Qualification compares two clean locked builds, verifies packed contract bytes
and source revision, then restores the exact artifact into the HTTP-fake consumer
on .NET 10 and 11. It does not download or run a TerraBase binary. Neither
preparation path publishes anything or proves live-service compatibility.

## Tagged release behavior

Ship a version bump to the next unused
`0.0.X` and tag its shipped `main` commit as `sdk/dotnet/v0.0.X`.
Do not reuse a published version.

1. `package` requires the `sdk/dotnet/v0.0.X` tag to match the package version and its commit to
   belong to `origin/main`, runs release checks and qualifies one artifact.
2. `github-packages` verifies its SHA-256 before publishing those bytes.
3. `restore` downloads that version into fresh caches, verifies the same hash
   and runs the installed-package consumer on both frameworks.
4. `nuget-org` downloads the qualified artifact, verifies its hash again and
   automatically publishes using `NUGET_API_KEY` only after `restore` succeeds.

No OIDC policy or manual SHA input is required. A missing or expired key fails
the nuget.org job; it never permits an unqualified package to publish. GitHub
Packages may already contain the version at that point. After fixing the key,
rerun the failed job while the qualified artifact remains available. GitHub
Packages reruns accept duplicates only after the subsequent restore proves
their bytes match; nuget.org duplicates fail rather than replacing a version.

Only reviewed workflows should receive write access to this repository:
contributors able to change a workflow can access its Actions secrets. Artifact
hashes verify integrity, not protection against a malicious workflow author.
nuget.org repository signing adds `.signature.p7s`, so later nuget.org downloads
are not expected to have the original unsigned archive hash.
