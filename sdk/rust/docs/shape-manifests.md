`Collection::get_shape_manifest(handle, offset, filter)` retrieves up to eight
mirror object capabilities with `documents:read` project credentials; end users
must use the origin shape stream. Signed URLs are opaque; Debug redacts them.
No capability is fetched automatically. Before applying an object, verify its
size and SHA-256 and decrypt using `Shape::decrypt_sealed` and its bound identity.
Use the origin on an empty manifest or failed verification. On `must_refetch`,
restart the origin shape at -1; refresh expired manifests at the next epoch.
Responses bypass automatic retries and error diagnostics omit capability data.
