# Resolved development and release dependencies

`requirements-ci.txt` pins and hashes the runtime, dev, Hatchling backend,
editable-build helper, pip-audit, pip and Twine graphs. The SDK's public dependency
ranges and minimum Python 3.10 remain in `pyproject.toml`. The lock targets Linux
and Python 3.10, the common compatible graph for the existing 3.10–3.13 CI matrix.
Every matrix entry installs this same graph from wheels with hash verification.

`scripts/compile-ci-lock.sh` extracts every `build-system.requires` entry from
`pyproject.toml` into the resolver inputs, alongside runtime/dev requirements and
`requirements-ci.in`. The backend requirement has no duplicate declaration.
CI checks the lock against these same inputs and runs pip-audit once, in the
existing Python 3.12 test job. A missing or incompatible locked backend fails the
input check before the audit or build. In addition, every Python 3.10–3.13 matrix
entry validates its active PEP 508 build requirements and backend extras against
the installed exact lock. This evaluates Python-version and other environment
markers with the real interpreter, so a requirement conditional on Python 3.11+
cannot be hidden by the resolver's Python 3.10 target. Missing, incompatible or
unlocked backend dependencies fail before editable installation or building.
Publication repeats both checks before building. The audit
fails on findings at **every severity**, dependency collection failures and feed
errors. Its HTTP cache honors feed freshness; uv's package cache is also reused.
There are no ignored advisories. Dependabot proposes pip and Action updates;
those PRs must also regenerate the complete lock when necessary.

Both builds use `uv build --no-build-isolation`: the preinstalled, audited
Hatchling builds the sdist and the wheel from that sdist. No isolated build may
resolve another backend or helper. Twine also runs from the lock rather than uvx.
The uv frontend is pinned to 0.13.5 in both workflows.

DECISION: retain Python 3.10 and use filelock 3.20.3 for CI tools. Advisories
GHSA-v4x8-3wr5-3wq8 and GHSA-9vhv-jxww-xfm5 start at 3.20.4; fixes in 4.2.1+
require Python 3.11. The explicit input constraint selects the newest clean
version compatible with the complete matrix, without hiding scanner findings.
Revisit it when the project's Python minimum changes or an upstream fix supports
3.10. This tool dependency does not enter the published SDK's requirements.

To update the lock, use uv 0.13.5 and run:

```sh
bash scripts/compile-ci-lock.sh --upgrade --output-file requirements-ci.txt
uv pip sync --require-hashes --only-binary :all: requirements-ci.txt
bash scripts/check-ci-lock.sh
pip-audit --strict --disable-pip --require-hashes -r requirements-ci.txt
```

Prefix local build/test/lint/audit commands with `nice -n 19`. Run the existing
Ruff, mypy and pytest checks across the supported matrix. Build without isolation,
run Twine, and smoke-test the wheel in a fresh consumer environment. No release,
tag, registry publication or signing approval is implied by these checks.
