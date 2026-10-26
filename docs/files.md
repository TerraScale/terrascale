# Signed file lifecycle

```python
from terrascale.files import BucketFiles, FileUpload
from terrascale.storage import Storage

files = BucketFiles(Storage(client), bucket_id)
upload = files.create_file_upload(FileUpload(
    "hello.txt", "text/plain", source_size, base64_sha256, metadata={"userId": subject},
))
# PUT the exact source bytes to upload.url using every upload.headers entry,
# with a separate storage HTTP client and no project bearer token. Then:
ready = files.complete_file_upload(upload.id)
page = files.list_bucket_files()
download = files.create_file_download(ready.id)
files.delete_file(ready.id)
```

For asyncio, use `AsyncBucketFiles(AsyncStorage(async_client), bucket_id)` and
await each API method. These five operations never visit storage URLs. Use
`terrascale.file_transfer` to transfer bytes with an isolated storage client.

Uploads reserve 1–100 MiB and require canonical base64 of the 32-byte SHA-256
digest, not hex. Metadata and ownership are immutable; completion verifies the
stored size/checksum. List pages contain up to 50 authorized files, continued
with `after=page.next_cursor`. IDs and continuations are canonical ULIDs; numeric
or decimal-string sizes decode to exact Python integers. Deletion queues cleanup.

Signed URLs are bearer capabilities. Keep URLs and returned headers private;
both are excluded from DTO `repr`. Upload capabilities last five minutes and
download capabilities 60 seconds. Pass every returned header exactly, without
adding API authentication. Mutations are never automatically replayed after an
uncertain outcome; inspect file state before deciding whether to retry.
