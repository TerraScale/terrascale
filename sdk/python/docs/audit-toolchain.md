# Isolated dependency audit toolchain

`requirements-audit.txt` pins the pip-audit CLI and its complete graph for Linux
Python 3.12. CI and publication install it into `$RUNNER_TEMP/ts-audit` outside the source tree, separate from the
SDK's Python 3.10–3.13 test/build environment. Both exact hashed locks are audited
at every severity, without advisory exclusions. Both input graphs are checked.

CVE-2026-112909 and CVE-2026-112910 also affect filelock 3.20.3. The previous
constraint in `requirements-ci.in` no longer provides a clean graph. Fixed
filelock requires Python 3.11+, so the audit tool uses 3.12 while SDK support
continues unchanged. filelock is absent from the SDK test/build lock.

Regenerate the SDK lock with `scripts/compile-ci-lock.sh`. Regenerate the audit
lock using uv 0.13.5:

```sh
uv pip compile requirements-audit.in --python-version 3.12 \
  --python-platform x86_64-unknown-linux-gnu --no-build --generate-hashes \
  --no-annotate --custom-compile-command 'See docs/audit-toolchain.md' \
  --constraints requirements-ci.txt --upgrade --output-file requirements-audit.txt
```

The historical filelock decision in `dependencies.md` is superseded by this
separate audit environment. Prefix local commands with `nice -n 19`.
