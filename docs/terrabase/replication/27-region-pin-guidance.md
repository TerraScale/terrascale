# Explicit region pins and recovery (TS-1132)

A region pin fails closed. An authorized request arriving outside its pin, or outside its database's placement, receives
`421 wrong_region`. HTTP documents, SQL and Hrana include `error.regions`, `error.host` and `error.docs`, plus the existing
placement headers. An `allRegions` database lists the serving regions from membership and omits the home header.
Unknown or unreachable databases receive generic pin guidance without confirming their existence.
No database placement is returned before authentication, scope and database reach checks. A credentialed database request
with an invalid key now receives 401 even when its otherwise valid pin reaches another region.

Use `https://<region>.api.terrascale.tech` with a matching `X-TerraScale-Region-Pin`. An excluded pin never replays to another
region. An included pin can replay only to itself, once. A regional host permits fallback only to report unavailable,
never to serve outside its pin. Unpinned requests retain placement routing.
Contradictory or malformed pins fail before authentication, without tenant details. Replay headers can suppress routing;
they never authorize serving outside a pin. An unavailable regional host returns 503 region_unavailable after failed
replay, or when membership has no machine there; this is distinct from a database placement/pin mismatch.


No storage format or peer protocol changes. Existing unpinned clients retain placement routing.
Recovery docs: <https://docs.terrascale.tech/guides/errors> (verified HTTP 200).
