# Product-name migration check

`scripts/check_product_name.py` scans every Git-tracked path and bounded UTF-8
text file for retired product spellings, the retired short prefix, and the old
benchmark-lane source symbol. Each rule covers joined, hyphenated, underscored,
spaced, dotted, repeated-separator, and mixed-case forms. The default local
mode also scans non-ignored untracked files. CI uses `--tracked-only`.

`legacy-name-allowlist.tsv` is limited to persisted or wire compatibility
identities and exact historical evidence. Each entry binds the path, match
count, rule, byte location, token bytes, and a SHA-256 fingerprint. The check
rejects a missing file, moved token, capitalization change, extra match, stale
entry, symlink, special file, or file larger than 8 MiB.

Active source, package metadata, generated code, receipt templates, workflows,
operator documentation, and new runtime artifacts cannot use an allowance
unless the matched bytes are a listed format reader or an exact provenance
identifier. Generic protocol and receipt version 2 identifiers are not product
aliases. The receipt schema and verdict tokens that must stay stable, plus all
other compatibility identities and removal rules, are documented in
`docs/design/product-rename-compatibility.md`.

Run the self-tests and both scan modes with:

```text
python3 -m unittest scripts/test_product_name_check.py
python3 scripts/check_product_name.py
python3 scripts/check_product_name.py --tracked-only
```

`--inventory` prints current path, count, and fingerprint tuples for review. It
does not write or approve exceptions.
