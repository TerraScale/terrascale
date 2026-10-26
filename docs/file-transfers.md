# Signed file transfers

Compose `FileTransfer(capability)` from `terrascale.file_transfer`; its async twin
is `AsyncFileTransfer`. Call `put_bytes(data)` once after reserving an upload,
then explicitly complete that reservation through `BucketFiles`. Obtain a
download capability and call `get_bytes()` to return its immutable raw bytes.

DECISION: each operation uses a fresh HTTPS/TLS 1.3 client with no project auth,
cookies, environment proxies, redirects or retries. Capability header names must
be unique ignoring case; credential, routing and framing headers are refused.
Allowed signed headers and the opaque URL are preserved. No decompression runs.

Uploads require immutable bytes, 1–100 MiB, matching signed Content-Length and
canonical base64 SHA-256 when supplied. Downloads require HTTP 200, are bounded
to 100 MiB, and match response Content-Length and supplied SHA-256 checksums.
A GET grant cannot declare a nonempty request body. PUT accepts any HTTP 2xx.

`FileTransferError` retains only a status and `may_have_applied`; upstream text,
capabilities and transport exception chains are discarded. A failed PUT with
`may_have_applied=True` needs reconciliation before another attempt. Errors never
complete/cancel a reservation. Obtain expired grants again explicitly. Invalid
grants/bytes raise a generic ValueError before any network request.
