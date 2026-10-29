# Shape vector provenance

`shape-vectors.json` was copied byte-for-byte using `gh api` from
[TerraScale/terrascale docs/terrabase/shape-vectors.json](https://github.com/TerraScale/terrascale/blob/993927ce7ecf8f9b889d24d4d1d21ec96eaf9416/docs/terrabase/shape-vectors.json)
at source commit `993927ce7ecf8f9b889d24d4d1d21ec96eaf9416`.

SHA-256: `0e9966cae7fcc6255fa2b07c6632ae877d42a9dbc5b299b65ac9bad2c6d8017c`.
Format: `TB-SHAPE-VECTORS-V1`, protocol version `1`.

Update by copying a new pinned upstream revision, preserving exact bytes and
recording its commit/hash here. All keys, tokens, URLs and documents are
synthetic. Tests use only these fixtures and local `httptest` servers; no
TerraBase download, build or execution is permitted in this SDK repository.

[contracts/shapes.md](../../contracts/shapes.md) records SDK scope and limits.
The `presigned-manifest` HTTP vector is explicitly deferred until FR-22. It
stays in the copied file unchanged. Producer identity, filter canonicalization,
durability and server-side validation vectors are not SDK implementation claims.
