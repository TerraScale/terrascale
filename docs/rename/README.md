# Product-name migration check

`scripts/check-product-name.mjs` scans every tracked or non-ignored worktree
path and bounded UTF-8 text file for retired version-suffixed product names. It
covers joined, hyphenated, underscored, spaced, dotted, and mixed-case forms.
The default local mode includes non-ignored untracked files. CI uses
`--tracked-only` before checking out any consumer repository.

`legacy-name-allowlist.tsv` is limited to files that repository rules reserve
for human owners. Each entry binds its path, exact match count, and SHA-256
fingerprint of every matched token and location. The check rejects missing
paths, changed tokens, extra matches, and stale entries.

Active source, package metadata, generated code, contracts, workflows, and
operator documentation cannot use an allowance.

The scanner reads at most 8 MiB from one file. Larger text files fail closed and
need an explicit repository-design review. Directories, nested repositories,
binary files, and non-UTF-8 files are not treated as repository prose.
