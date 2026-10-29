# Product-name migration check

`test/product-name.test.js` (part of `npm test`) scans every tracked
worktree path and bounded UTF-8 text file for retired version-suffixed
product names. It covers joined, hyphenated, underscored, spaced, dotted,
and mixed-case forms.

`legacy-name-allowlist.tsv` is limited to files that repository rules reserve
for human owners. Each entry binds its path, exact match count, and SHA-256
fingerprint of every matched token and location. The check rejects missing
paths, changed tokens, extra matches, and stale entries.

Active source, package metadata, generated code, contracts, workflows, and
operator documentation cannot use an allowance.

The scanner reads at most 8 MiB from one file. Larger text files fail closed and
need an explicit repository-design review. Directories, nested repositories,
binary files, and non-UTF-8 files are not treated as repository prose.
