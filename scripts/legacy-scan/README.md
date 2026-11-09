# Legacy scan (C1)

Run with Python 3 and Git; no packages or service binaries are required:

```sh
python3 /path/to/pinned/terrascale/scripts/legacy-scan/scan.py --repo ts-js --root . --output legacy-hits.md
```

The scanner reads tracked files, including dotfiles, examples, tests, workflows and active documentation. Untracked build outputs are outside its scope. Binary file contents are excluded. Symlinks are scanned as their destination text without following them; tracked target files are independently scanned. Filename hits use line 0. Reports count term occurrences, including filename hits, and assign the owning pivot task. Output includes only matched terms, never full source lines. This is a static lexical gate; E7/Q3 separately check running public responses for engine-name leaks.

`terms.json` defines patterns and default owners. The `allow-list/` folder defines exceptions scoped by path, optional repository, term IDs, optional file-context expressions and optional matched spans; one JSON file per top-level area (with `src/`, `tests/`, `docs/`, `docs-site/` and `sdk/` split one level deeper by their first path segment, where `\.` counts as a literal dot), and anything not anchored at a literal top-level directory going to `any-path.json`. Keep each file sorted by `path`, one entry per line; parallel changes in different areas then never meet in the same file. An exception on a line never permits other legacy text on that line. Engine branding is permitted only within named internal boundaries and required attribution files; active public docs and SDKs remain scanned. Historical measurement reports remain available. Retained HLC LWW terminology is scoped to new home-apply/conflict-resolution boundaries; it does not permit leaderless references. Tigris `flyctl storage` commands remain valid. Extend exceptions narrowly with a reason, and cover changes with process integration cases.

Every consuming repo pins the scanner checkout to a full commit SHA. Keep it outside the target checkout, so scanner files are never mistaken for target files. Update pins explicitly after scanner policy changes.

## Full-repository CI gate (C7 complete)

The monorepo commits `.legacy-scan-mode` containing `fail`. Main CI scans every tracked file with every term family and reads this tracked mode, so any unapproved occurrence fails CI, including occurrences in unchanged files. C7 is complete: the full-repository scan reported zero hits before enabling enforcement. No allow-list expansion was needed.

`--path-prefix` limits investigative scans to a repository-relative directory. Repeat `--term` to select retired document API term families; omitting both options preserves the full repository scan.

## Other repository scans

Repositories with deletion work remaining may commit `.legacy-scan-mode` containing `report`. Report mode lists all hits and returns 0. Switch to `fail` after confirming zero hits. Fail mode returns 1 on hits. Invalid mode, unreadable tracked files, missing Git metadata and policy errors return 2 in either mode. `--mode` overrides the tracked mode only for investigations and process tests.

Process integration verification uses isolated real Git repositories and concurrent scanner subprocesses:

```sh
nice -n 19 python3 scripts/legacy-scan/process-test.py
```

No API, storage, authentication or storage-format behavior changes. No visual change.
