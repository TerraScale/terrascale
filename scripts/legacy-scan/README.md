# Legacy scan (C1)

Run with Python 3 and Git; no packages or service binaries are required:

```sh
python3 /path/to/pinned/terrascale/scripts/legacy-scan/scan.py --repo ts-js --root . --output legacy-hits.md
```

The scanner reads tracked files, including dotfiles, examples, tests, workflows and active documentation. Untracked build outputs are outside its scope. Binary file contents are excluded. Symlinks are scanned as their destination text without following them; tracked target files are independently scanned. Filename hits use line 0. Reports count term occurrences, including filename hits, and assign the owning pivot task. Output includes only matched terms, never full source lines. This is a static lexical gate; E7/Q3 separately check running public responses for engine-name leaks.

`terms.json` defines patterns and default owners. `allow-list.json` defines exceptions scoped by path, optional repository, term IDs and optional matched spans. An exception on a line never permits other legacy text on that line. Engine branding is permitted only within named internal boundaries and required attribution files; active public docs and SDKs remain scanned. Historical measurement reports and explicitly archived history remain available. Extend exceptions narrowly with a reason, and cover changes with process integration cases.

Every consuming repo pins the scanner checkout to a full commit SHA. Keep it outside the target checkout, so scanner files are never mistaken for target files. Update pins explicitly after scanner policy changes.

## C7 transition

Each repo commits `.legacy-scan-mode` containing `report` while deletion work remains. Report mode lists all hits and returns 0. C7 changes this file to `fail` after confirming zero hits, without changing the workflow or scanner. Fail mode returns 1 on hits. Invalid mode, unreadable tracked files, missing Git metadata and policy errors return 2 in either mode. `--mode` overrides the tracked mode only for investigations and process tests; CI reads the file.

Process integration verification uses isolated real Git repositories and concurrent scanner subprocesses:

```sh
nice -n 19 python3 scripts/legacy-scan/process-test.py
```

No API, storage, authentication or storage-format behavior changes. No visual change.
