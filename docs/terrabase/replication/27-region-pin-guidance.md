# Explicit region pins and recovery (TS-1132)

HTTP region-pin errors retain the contract introduced by #810: malformed pins return
`invalid_region_pin` and a pin naming another serving region returns `region_pin_mismatch`
before authentication. Database placement errors remain `wrong_region` with placement headers.
See [region identity](../region-identity.md). This recovery work extends socket protocols.

PostgreSQL accepts `database@region` or `PROJECT/database@region` in startup's database field. Keyword connection strings
and Npgsql use the literal `@`; URI paths encode it as `%40`. Empty, repeated or malformed suffixes fail closed with
`08004 misdirected_request`. Authorized placement/pin refusals use SQLSTATE `08004` with `wrong_region:` and the same
regions, HTTP recovery host and docs in the message. Every statement rechecks authorization and placement.
`current_database()` reports the configured name including its pin. Public TLS uses `pg.terrascale.tech:5432`; no regional
PostgreSQL DNS names exist. To select a regional address, retain `pg.terrascale.tech` for TLS verification and use that
region's address (libpq `hostaddr`). Retrying the same address cannot change regions.

Wire adds a first-frame-only operation `4` (pinned hello): the operation 0 body, followed by `u8 region length` (1–63)
and ASCII region bytes, with no trailing bytes. Invalid structure is `400 invalid_frame`; an invalid region is
`421 misdirected_request`. An authorized mismatch is `421 wrong_region`, with placement, HTTP recovery host and docs in
the UTF-8 error message. Authorization rechecks retain the pin. Operation 4 after hello remains `400 unknown_operation`.
The public Wire TLS edge route is pending; this change does not expose the plaintext node listener.

No storage format or peer protocol changes. Existing unpinned clients retain their protocol. During a rolling deployment,
old nodes refuse new socket pin encodings (PostgreSQL 28P01; Wire unsupported_version); they cannot silently ignore a pin.
Recovery docs: <https://docs.terrascale.tech/guides/errors> (verified HTTP 200).
