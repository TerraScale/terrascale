# Shape snapshots reseal at 16 chunks per page (RT2c lane R2)

Amends the reseal rule of [shape-reads.md section 6](../shape-reads.md) and [free-reads.md 3.5](../free-reads.md). Pages,
chunks and the one-hour age rule are unchanged.

**Problem.** A bootstrap (`offset=-1`) answers the newest snapshot, then reads every chunk sealed after it, one request per
chunk. A snapshot was resealed only after 4096 chunks or one hour, and live seals cut a chunk per commit wake (a few documents
each), so a reader that bootstrapped minutes after the first one replayed hundreds of small chunks. In `shape-bench load
-steps 1,1000`, the 1000 readers of the second step each had to replay the first step's 171 chunks at different offsets, so
each needed its own flight: the organization's 8 flights answered 347,201 `429 shape_busy`, 12 readers reached the head
by the time writes started, and 19,301 of 600,000 writes were delivered. Run alone, the same step starts on an empty
collection and delivers all writes.

**Rule.** A bootstrap reseals the snapshot when the chunks past it reach 16 per snapshot page (at most 4096). A bootstrap
then costs at most about 17 requests per page, and resealing writes at most one page per 16 chunks sealed since the last
snapshot. Readers that bootstrap together still share each page through one flight.
