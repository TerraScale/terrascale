# Schema document values

Collection schemas use the field type names in `CanonicalFieldType`. Values
inside document JSON use these representations:

| Schema type | JSON value |
| --- | --- |
| `string`, `text` | String |
| `int` | Signed 64-bit integer |
| `float` | Finite number, including integer values |
| `bool` | Boolean |
| `bytes` | Standard padded base64 string; the empty string represents no bytes |
| `timestamp` | Integer milliseconds since the Unix epoch |
| `json`, `register-mv` | Any JSON value |
| `counter-pn` | Signed 64-bit integer |
| `set-add-wins`, `set-remove-wins` | JSON array |

Base64 uses the standard `A–Z`, `a–z`, `0–9`, `+`, `/` alphabet and canonical
padding and trailing bits. An array of octets is not a `bytes` field. Timestamp
strings are not timestamp values. SDK codecs must preserve these representations
when converting native byte arrays and date/time objects.

An absent optional field is accepted. Required fields must be present; present
values must satisfy their declared type. CRDT field validation checks the JSON
value shape; merge semantics are handled by the document merge machinery.

The schema service accepts up to 512 fields per collection. Its durable
validation policy retains every canonical field type and runs on staged writes
and again against active metadata at commit. Schema migration progress, the
active validation policy, and the schema API envelope are private metadata,
outside customer document scans and document metering.

The HTTP schema routes return a strong `ETag`. Applying a schema requires an
`Idempotency-Key`; `If-Match` optionally guards the observed generation. A pending
migration is work still to be performed, not evidence that all existing documents
already satisfy the new schema.
