# TTL timestamp validation

TTL expires documents only when the configured member contains a valid RFC 3339
UTC timestamp earlier than the sweep time. Accepted forms use `T` or `t`, seconds,
an optional decimal fraction, and `Z`, `z` or `+00:00` as the UTC suffix.

Date-only, timezone-less, invalid dates, non-string values and non-UTC offsets
never expire. This corrects the permissive parser that could delete date-only
values and clarifies the older `collection-ttl.md` offset example: non-UTC values
are ignored permanently, rather than merely delayed by candidate selection.
Store expiration values in UTC when writing documents.

The sweep includes current-day candidates before checking each exact timestamp,
so lowercase `t` does not exclude an otherwise valid expired value. Future values
are preserved. Deletions retain their existing version preconditions.

This is a validation correction to the documented UTC contract. It adds no API,
SDK, configuration, persistence format or migration; existing invalid values must
be corrected explicitly by their owner if they are intended to expire. Fast
integration coverage writes each value through HTTP, runs the real sweep against
isolated storage and reads the resulting document through HTTP using a fake clock.
