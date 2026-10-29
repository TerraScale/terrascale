# Storage transfer helpers

After `create_file_upload`, call `capability.put_bytes(bytes).await?`, then `complete_file_upload(bucket, &capability.id)`. For reads, obtain a download capability and call `capability.get_bytes().await?`. Downloads are bounded to 100 MiB.

DECISION: helpers use separate HTTPS/TLS 1.3 clients without project authentication, proxies, redirects, retries or decompression, preserving exact bytes and safe signed headers. Credential/routing/framing headers and duplicate names reject the capability; none are silently dropped. PUT validates length/checksum headers. Obtain expired capabilities again explicitly. Errors omit URLs, headers and upstream bodies.
