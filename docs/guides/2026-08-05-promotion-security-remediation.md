# Promotion Security Remediation

The release-candidate upload job now requires both the `develop` branch name
and a pull-request head repository equal to the current canonical Forgejo
repository. Fork pull requests therefore cannot receive or use the candidate
store credential.

The npm availability preflight now runs in a disposable directory with empty,
private npm user and global configuration files. It does not inherit npm
credentials, project `.npmrc`, or caller-provided npm configuration.

Privileged npm publishing uses the same disposable working directory and an
empty private global configuration, while its private user configuration holds
only the job-local npm token. The bootstrap NuGet credential directory is
removed by an exit trap on both successful and failed runs.

Fork pull requests run the SDK static and build checks directly, without the
private Deploy CLI artifact. Private-feed bootstrap, planning, and release
jobs require a canonical Forgejo pull-request head repository; the main push
promotion path remains trusted.

The promotion workflow verifies its candidate and publishes it to npm; it does
not create, finalize, or recover Forgejo Releases.
