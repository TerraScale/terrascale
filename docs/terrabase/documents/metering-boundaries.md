# Document read acceptance and operation units

The [published pricing contract](https://docs.terrascale.tech/pricing) bills each
started 4,096 bytes of a logical object successfully read or written, rounding
each list/query item independently. Failed, denied, invalid and not-found
requests are unbilled. Document API reads accept a charge only after authorization,
conditional checks, outgoing cursor validation and response-body construction.
Project and end-user credentials share this boundary, with or without the result
cache. An explained query returns documents and bills exactly as an ordinary query;
scan work, counts and explanation metadata add no document units.

A GET returning 304 accepts no read charge: RFC 9110 section 15.4.5 defines it as
a conditional response without content, and this policy bills returned logical
objects. Validating a client's cached representation does not return another
object. Malformed conditions (400), failed preconditions (412), missing documents
and tombstones (404), authorization failures and unique-conflict errors likewise
accept no charge. A successful empty page records one read operation, zero units.

The existing sizing rule remains the stored JSON length of each returned document,
independently rounded, including when the response projects fields. This patch
does not define new projection or aggregate/group sizing. Aggregate-only pages
retain their existing zero-document units; a broader returned-object pricing
policy for those results needs its own documentation and fixtures.

HTTP document, platform explorer and binary-wire responses explicitly accept the
charge once. Binary wire accepts only after outgoing response memory admission.
Internal service reads for imports, functions, authorization and
write preconditions do not accept an additional client read charge. Exports and
schema previews keep their separate existing accounting.

Acceptance means the server constructed an accepted response, not proof that the
client received every byte. Subsequent serialization/transport failures are not
refunded here. Streaming, disconnect and partial-delivery policies remain outside
this change; durable write accounting and SQL accounting are unchanged.
