# Reimporting an exported import destination

NDJSON exports include the original import's two audit documents. When importing that export into a new database,
the reader recognizes only the fixed audit document IDs with the versioned import-record shape and lists them as
`import_audit_record` entries in `preview.skipped`. Review these exclusions with the preview before starting.

Customer documents keep their collections, ULIDs and bodies. The destination creates its own import audit; the old
job and its checkpoint are never adopted. Other documents in `terrascale-import`, including malformed audit-shaped
documents, still block the preview with `collection_name_collision`; a whole customer collection is never silently
discarded. Exports and the public request/response shapes are unchanged. JSON object property order may change, so
compare parsed documents by collection and ID when verifying a round trip.
