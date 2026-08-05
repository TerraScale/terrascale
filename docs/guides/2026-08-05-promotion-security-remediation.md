# Promotion Security Remediation

The release-candidate upload job now requires both the `develop` branch name
and a pull-request head repository equal to the current canonical Forgejo
repository. Fork pull requests therefore cannot receive or use the candidate
store credential.

The npm availability preflight now runs in a disposable directory with empty,
private npm user and global configuration files. It does not inherit npm
credentials, project `.npmrc`, or caller-provided npm configuration.

The promotion workflow verifies its candidate and publishes it to npm; it does
not create, finalize, or recover Forgejo Releases.
