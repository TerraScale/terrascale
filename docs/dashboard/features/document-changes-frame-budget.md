# Document changes frame budget

The dashboard changes feed retains the full document, identity, region and
checkpoint contract. The browser bounds each event's non-delimiter JavaScript
UTF-16 characters, independently of network read boundaries, at **6,334,044**:

- `6 * 1,048,576` for a document: TerraBase `RequestJson.MaxDocumentBytes` admits
  at most 1 MiB of UTF-8 JSON. Default `System.Text.Json` can expand each unsafe
  ASCII character, such as `<`, into six ASCII characters (`\u003C`). This also
  conservatively covers non-ASCII escaping and supplementary Unicode characters.
- `2 * 19,246` for the checkpoint in SSE `id` and JSON `cursor`. SDK 0.0.12's
  `TerraBaseDatabase.MaxChangesCursorLength` derives this from producer
  `ChangeCursor.MaxBytes = 17 + 18 + 63 + 56*256` decoded bytes, then base64url.
- `4,096` for the remaining envelope: at most `6*(128+256+63) = 2,682`
  characters for escaped collection, stored ID and region; 20 decimal version
  digits, 16 hex origin digits, operation/state text and fewer than 512 literal
  JSON/SSE syntax characters fit in the remainder. Dashboard `ValidCollection`
  caps names at 128; TerraBase `CheckStoredId` permits 256 (API-created IDs are
  26-character ULIDs); `TerraBaseOptions.IsRegion` caps region names at 63.

This is a fixed parser character budget, not a wire-byte/document limit or a
configuration option. CR/LF delimiters do not consume it; a pending CR follows
that same rule. Completed frames reset the counter before the unfinished tail
is checked. Oversized complete and incomplete frames fail closed and cancel the
reader. API document admission and SDK upstream frame limits remain unchanged.
