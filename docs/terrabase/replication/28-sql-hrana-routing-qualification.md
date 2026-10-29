# SQL controls and Hrana routing qualification (TS-1333)

`SqlHranaRoutingE2ETests` uses three owned, local nodes and a database selected for gru and fra, excluding sjc.
It sends real HTTP requests; it never stops a production node. Both SQL HTTP and Hrana originate a small counter
and control matrix. Literal increments of an `INTEGER NOT NULL` column add across a partition; nullable arithmetic,
REAL arithmetic and a bound-parameter increment use computed values. The test also checks a bound NULL, REAL,
text and an integer greater than 2^53 without converting the integer through a double.

Direct requests distinguish the answering node from edge replay. An excluded node and a mismatched explicit pin
must refuse an update without changing the settled rows. An included pin entering through the excluded edge
must answer in its requested region. The test records `X-TerraBase-Region` and `X-TerraBase-Machine` on routing
responses. TS-1132 separately qualifies actionable pin guidance.

Hrana batons belong to a node. A stream with stored SQL uses a `.t` baton: landing on another node gives
`400 STREAM_EXPIRED`, so the client must open a new stream. A `.a` baton carries no transaction or stored SQL
state and can open a fresh stream on another node. The local failover scenario checks both behaviours, including
entry through an excluded region that replays to the database's home instead of the issuing replica.

The scenario acknowledges five distinct autocommit markers at the issuing replica, stops that local replica,
acknowledges three more at the home, closes the surviving stream and restarts the issuer on its original data.
Both selected regions must eventually return exactly those eight markers. Writes are never retried. HTTP SQL
and Hrana acknowledge local durability; this scenario checks recovery with the issuer's durable data restored,
and does not establish zero data loss while that data remains unavailable.

Production qualification remains separate: sjc is pending batch 12, and a production failover requires the
deployment owner. A passing local scenario alone does not qualify that production path.
