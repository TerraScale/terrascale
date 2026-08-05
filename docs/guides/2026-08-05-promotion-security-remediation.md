# Promotion Security Remediation

Release-candidate creation and upload run only after a protected push to `main`.
No pull-request job, including a same-repository `develop` to `main` PR,
receives candidate-store, publish, or signing credentials, uploads candidate
artifacts, or calls the candidate store.

The npm availability preflight now runs in a disposable directory with empty,
private npm user and global configuration files. It does not inherit npm
credentials, project `.npmrc`, or caller-provided npm configuration.

Privileged npm publishing uses the same disposable working directory and an
empty private global configuration, while its private user configuration holds
only the job-local npm token. The bootstrap NuGet credential directory is
removed by an exit trap on both successful and failed runs.

Pull requests run the SDK static and build checks directly, without the private
Deploy CLI artifact. There is no privileged canonical pull-request path:
private-feed bootstrap, planning, release checks, candidate creation, and
promotion run only on the protected `main` push. Promotion depends on the
candidate created in that same trusted workflow and fails closed if it is not
available; it never promotes a candidate uploaded by a pull request based only
on a matching tree.

Fork-reachable SDK checkouts disable persisted action credentials. The public
Documentation fixture is cloned without a credential helper rather than using
an action credential.

Release E2E runs only after the protected `main` push. A preflight rejects
missing or malformed endpoints, access token, or test resource identifiers before the
otherwise skip-capable live integration tests execute.

The promotion workflow verifies its candidate and publishes it to npm; it does
not create, finalize, or recover Forgejo Releases.
