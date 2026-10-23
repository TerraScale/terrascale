# Offline validation and scope record

Review the [design gates](README.md#implementation-phases); these checks validate documentation, not a running protocol server.

Reviewed producer HEAD: **`e30ffdc287530551b161b0130220be53d3c0475e`**. Only new files inside `docs/terrabase/pgwire-feasibility-20261013/` are included in this task. The existing worktree/branch was retained; no source, existing documentation, rules, package pins, authentication, engine or CI file was edited.

## Executed checks

| Check | Result and meaning |
| --- | --- |
| VECTORS.md fenced standard-library Python inspector, `nice -n 19` | **PASS:** 33 wire examples, 9 invalid examples, 15 values, 16 SQLSTATE mappings; 13 proposed flow expectations structurally checked. All fixture SQLSTATEs were also checked against the downloaded, pinned PostgreSQL 18 `errcodes.txt`. This does not execute those flow outcomes. |
| Scoped documentation lint, `nice -n 19` | **PASS:** 4 Markdown files, 8 relative links, UTF-8/newline/fence/whitespace/JSON checks. Initial run found two links to the not-yet-created CHECKS.md; adding this file resolved them. |
| `git diff --cached --check` and staged scope inspection | **PASS:** five new documentation/fixture files in the allowed directory, no modifications elsewhere. No source or existing documentation in the staged diff. |
| Native package/source inspection | Nuspec pin, library byte size/SHA-256 and `nm -D --defined-only` checked offline; 223 dynamic exports, 77 `turso_*`, zero server/interrupt-name matches. No native code loaded. See EVIDENCE for hash and limits. |
| Public primary-source reads | Exact Turso source and PostgreSQL 18 release protocol/error/type source retrieved without authentication; PostgreSQL REL_18_0 resolved with public `git ls-remote`. Initial guessed source paths returning 404 were corrected from workspace manifests/README; failed probes are not evidence. |

No local Markdownlint executable or equivalent documentation-only Make target was found. The repository `make lint`/`make check` invoke restores/builds/other suites, so they were not run under this task's explicit docs-only limits. The scoped lint below checks UTF-8 readability, final newlines, fences, trailing whitespace, first headings, relative-link targets and JSON validity. It is not a replacement for the required release checks.

The read-only advisory review found two fixture corrections before commit: assert the printed fixture counts, and make an oversized unread frame fatal (`08P01`, close) rather than imply result-limit recovery. Both were corrected and the offline inspector was rerun.

## Reproduce the offline documentation check

Run from the repository root. The inspector is extracted from VECTORS.md so the reviewed documentation and executed check are the same text. This command makes no network calls and writes no files.

```bash
nice -n 19 python3 - <<'PY'
from pathlib import Path
import json
import re

root = Path('docs/terrabase/pgwire-feasibility-20261013')
path = root / 'VECTORS.md'
code = path.read_text().split('```python\n', 1)[1].split('\n```', 1)[0]
exec(compile(code, str(path) + '#documentation-check', 'exec'))

problems = []
links = 0
markdown_files = sorted(root.glob('*.md'))
for path in markdown_files:
    text = path.read_text(encoding='utf-8')
    assert text.startswith('# ') and text.endswith('\n') and '\r' not in text, path
    fenced = False
    for number, line in enumerate(text.splitlines(), 1):
        if line.startswith('```'):
            fenced = not fenced
            continue
        if line.rstrip() != line:
            problems.append(f'{path}:{number}: trailing whitespace')
        if not fenced:
            for target in re.findall(r'\]\(([^)]+)\)', line):
                if target.startswith(('https://', 'http://', '#')):
                    continue
                links += 1
                if not (path.parent / target.split('#')[0]).exists():
                    problems.append(f'{path}:{number}: missing link {target}')
    assert not fenced, f'{path}: unclosed fence'
assert {path.name for path in root.iterdir()} == {'README.md', 'EVIDENCE.md', 'VECTORS.md', 'vectors.json', 'CHECKS.md'}
json.loads((root / 'vectors.json').read_text(encoding='utf-8'))
for problem in problems:
    print(problem)
assert not problems
print(f'PASS: scoped documentation lint, {len(markdown_files)} Markdown files, {links} relative links')
PY
```

Before the local commit, stage only this directory and inspect `git diff --cached --name-status`, `git diff --cached --stat` and `git diff --cached --check`. All paths must be new additions in this directory. After committing, `git show --format=fuller --stat HEAD`, `git show --format= --name-status HEAD`, `git show --check HEAD` and `git status --short --branch` establish the exact documentation commit and final scope. No push is part of this endpoint.

## Explicitly unverified / not executed

1. No .NET restore/build/format/analyzer/Fast/Slow, benchmark, CI workflow, live client, PostgreSQL instance, Turso connection, listener or port. Offline byte checks are not protocol conformance or service integration evidence.
2. No TLS handshake, ingress routing/handler, authentication payload, token validation/revocation, cancellation routing or backpressure/load behavior. Future gates require implementation admission and relevant owners.
3. No replication, durable ACK, restore, imports, storage format or engine verification/change. Read-only SQL producer behavior was reviewed from pinned source, not exercised here.
4. No authoritative Rust sibling contract, current Ku owner/assignment or remote GitHub backlog check; no product production call, credential handling, infrastructure installation, deployment, publication, merge or PR operation.

Future PR publication remains outside this initial task and requires explicit serial admission. A local documentation commit does not satisfy release-critical checks or authorize implementation.
