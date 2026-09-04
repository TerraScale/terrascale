---
status: accepted
date: 2026-08-29
authority: ../../../GOAL.md
---

# Production SLOs, OCI placement, and operability

## Release posture

The first external TerraBase release is General Availability. TerraScale
does not use a public preview or beta as a substitute for qualification.
Internal development, staging, fault injection, customer-data-free canaries,
and a continuous thirty-day soak on the real three-Region production shape are
mandatory before GA. Resetting a failed soak requires a new uninterrupted
thirty-day window on the corrected release candidate.

TerraBase source and every repository-owned Rust crate use the MIT License.
Production artifacts, symbols, schemas, and private generated bindings still
move through authenticated TerraScale artifact repositories and production
delivery paths. The private TBEP contract is an internal service boundary, not
a supported public client API.

The supported engine targets are 64-bit Linux
`x86_64-unknown-linux-gnu` and `aarch64-unknown-linux-gnu`. GA requires native
qualification on both. Other operating systems and 32-bit targets are outside
the product contract.

## Canonical Region identity

TerraScale uses uppercase three-letter airport codes as `RegionCode` values in
all internal product state and every public contract. The initial catalog is:

| `RegionCode` | OCI adapter target | Initial Member placement |
| --- | --- | --- |
| `GRU` | `sa-saopaulo-1` | Separate OCI fault domains in its availability domain |
| `SFO` | `us-sanjose-1` | Separate OCI fault domains in its availability domain |
| `FRA` | `eu-frankfurt-1` | Separate OCI availability domains and fault domains |

The engine and private TBEP use a typed fixed three-byte ASCII value. Public
REST uses the same three characters as a string. Parsers reject
lowercase, aliases, wrong lengths, non-ASCII text, and unregistered codes; they
never normalize input. A retired code remains reserved and cannot later name a
different TerraScale geography.

`SFO` is the stable TerraScale airport code even though OCI labels the target
location with `SJC` in some provider material. TerraScale never exposes `SJC`
as an alias.

OCI Region names, realm names, availability-domain names, fault-domain names,
OCIDs, compartment IDs, and VCN IDs exist only in versioned
TerraScale.Orchestrator provider-adapter configuration and internal deployment
evidence. They do not appear in public APIs, database identities, placement
contracts, checkpoints, causal state, customer-visible logs, or customer
receipts. TerraBase, TerraScale.Database, both SDKs, and the Dashboard accept
and emit only `RegionCode`.

A provider mapping is signed, environment-specific, and unique for one catalog
generation. Changing the OCI target behind an admitted `RegionCode` requires a
durable Region-movement operation, residency and capacity validation, complete
data qualification, a new placement generation, customer notice, and rollback
evidence. One generation cannot map the same code to two active provider
Regions.

## OCI production topology

The default TerraBase Member uses OCI Ampere flexible compute on ARM64 Linux.
The Orchestrator admits the exact Ampere generation independently in each
Region only after availability, capacity, price, and repository benchmark
evidence pass. The initial admission profile starts at 8 OCPUs, 96 GiB of
memory, and 6.8 TB of performance-qualified OCI Block Volume capacity per
Member. Cost efficiency is measured as cost per admitted durable operation.
The processor brand does not prove a saving.

Tier 2 network block storage is the mandatory and complete production storage
profile. A default Ampere Member runs correctly with Tier 2 alone. Tier 1 local
NVMe is optional when an admitted shape provides it. Tier 3 S3-compatible
object storage is independently optional. Tier 1 and Tier 3 may both be absent
without degraded health, fallback delays, or background retries. Enabling one
does not require the other, and Tier 3 remains outside every write quorum and
is not backup.

Linux x86_64 remains a supported compatibility and mixed-cluster target, but
the default placement policy selects a qualified Ampere Member whenever it can
meet the database's capacity and SLO. An x86_64 substitution is a recorded
placement decision with its own qualification evidence, not a silent fallback.
Capacity reservations and OCI service-limit validation cover the steady RF=2
shape, one temporary surge Member per upgrade lane, failure replacement, and
measured growth before a Region enters the catalog.

Members run on separate hosts. Their Tier 2 Block Volume attachments are
independent. GRU and SFO have one OCI availability domain, so their two active
Members use separate fault domains. FRA places active Members in separate
availability domains and separate fault domains. Temporary surge Members must
preserve or improve this separation.

Product workloads use private regional subnets. OCI dynamic routing gateways
and remote VCN peering carry authenticated cross-Region traffic. Service
gateways provide private access to OCI Vault and Object Storage. Public load
balancers terminate only the documented public services. No database Member,
TBEP listener, Vault route, storage attachment, or backup endpoint has a public
ingress path.

## Architecture and artifact qualification

The generic ARM64 release targets `aarch64-unknown-linux-gnu` and runs across
the admitted OCI Ampere generations. TerraScale may publish a separately tuned
Ampere artifact only after measurements show a benefit. Its signed manifest
names the required CPU and features, the Orchestrator schedules it only on a
matching shape, and it passes the same formats, recovery, replication, and
protocol suites as the generic artifact. An unlabelled `target-cpu=native`
binary cannot enter an artifact repository or deployment plan.

Native x86_64 and Ampere ARM64 runners build, lint, and test every release.
Qualification creates storage, WAL, segment, snapshot, backup, CRDT, and
protocol artifacts on each architecture and consumes them on the other. A
mixed-architecture replica set must converge on the same canonical state,
hashes, conflict winners, range fingerprints, and visible documents through normal
replication, repair, compaction, backup, restore, and an N/N-1 upgrade.

Performance baselines are architecture and storage-profile specific. The
primary GA workload and thirty-day production soak use the Ampere Tier-2-only
profile. Optional Tier 1 and Tier 3 profiles and any admitted x86_64 profile
have separate receipts. No result from one profile qualifies another.

## Availability and latency objectives

Availability is measured per database and calendar month at the public Database
API boundary. A valid admitted operation is good when it completes within its
documented deadline with the correct response. TerraScale errors, dependency
failures, exhausted internal capacity, failover, and planned maintenance count
as unavailable. Invalid credentials, rejected over-limit requests, customer
network failure outside TerraScale, and customer-requested suspension do not.

Read, mutation, reactive admission and delivery, and supported management
operation availability are calculated separately as well as together. Traffic
volume in one family cannot hide an outage in another.

| Selected active Regions | Monthly availability SLO |
| --- | --- |
| One | 99.9% |
| Two or three | 99.95% |

The same or stronger percentages must appear in the GA service terms. A release
or rollout that exhausts the error budget pauses feature promotion until the
service has recovered and corrective actions have passed.

At qualified admitted load, a public client inside the accepting OCI Region
must meet these service targets for 1 KiB hot documents: point-read p99 no more
than 10 ms, `RegionalDurable` single-document write p99 no more than 20 ms, and
local authorized reactive visibility p99 no more than 50 ms. Benchmark receipts
also report p50, p95, p99.9, throughput, errors, queue time, and resource use.
The primary gate runs on the Ampere Tier-2-only profile, proving that neither
local NVMe nor object storage is needed to meet the hot-path claim.
Cold reads, complex queries, coordinated operations, backup acknowledgement,
and recovery have separate preregistered budgets and never inherit the hot-path
claim.

For healthy GRU, SFO, and FRA sessions, a `RegionalDurable` change must become
available to an already-authorized reactive client in every selected Region
within 500 ms at p99. Each Region pair must also stay at or below twice its
measured direct-path p99 round-trip time plus 100 ms. Both limits apply. Clock
uncertainty, source acknowledgement, destination application, authorization,
view maintenance, and gateway publication are part of the measurement. Tests
do not subtract queueing or failed attempts.

These latency values are GA gates under the load and dataset declared by ADR
0026. They are not promises for an overloaded or partitioned network. Degraded
tests still require bounded queues, exact convergence, honest progress, and
recovery receipts.

## RPO, RTO, and backup defaults

`RegionalDurable` does not promise RPO 0 after complete destruction of its
accepting Region. Its loss boundary is the exact last commit that a surviving
Region or backup proves. The normal cross-Region latency objective reduces this
window but never turns asynchronous replication into a zero-loss guarantee.

With at least two selected active Regions, `GlobalDurable` has RPO 0 for the
complete loss of one Region. `BackupDurable` is the only acknowledgement that
claims the accepted commit reached the optional immutable backup cluster. No
other backup mode claims zero loss.

Active databases retain thirty days of point-in-time recovery history. Optional
continuous immutable backup retains thirty-five days by default and produces
at least one independently verified recovery point every day. Ordinary backup
retention cannot exceed ninety days unless a recorded legal hold or explicit
compliant customer policy requires more. Restore always creates a new cluster,
new active database-key version, and verified frontier before traffic moves.
ADR 0040 stores these recovery points through Litestream in OCI Object Storage.
A persistent backup block volume is not part of the production design.

For a healthy multi-Region database, public routing must recover from complete
loss of one Region within five minutes. A one-Region database cannot claim that
failover objective. Verified PITR or immutable-backup restore of a database up
to 1 TiB targets four hours to a new qualified cluster. Larger restores use a
published size-based estimate and report durable operation progress under ADR
0028. RTO starts when TerraScale detects or receives the recoverable incident
and ends only when validation passes and the replacement is eligible for
traffic.

## Service and commercial model

The initial public offer has one `Production` service. It always uses the
durability, encryption, isolation, observability, support, and recovery
contracts in this goal. TerraScale does not sell a reduced-durability or free
production tier. Promotional credits do not change service behavior.

Production uses shared Member processes by default. Each database has an
isolated engine location, WAL, publication journal, encryption key hierarchy,
backup lifecycle, quotas, and resource-accounting identity inside that process.
Databases do not share one storage directory or encryption key. The Member uses
bounded fair scheduling and per-database admission so one database cannot own
all memory, queue, compaction, or I/O capacity.

The Orchestrator moves a database to dedicated Members when measured storage,
throughput, latency, isolation, regulatory, or noisy-neighbor thresholds
require it. Placement movement is a durable operation and does not change the
database format or public API.

The public meter catalog charges for stored byte-hours in each selected Region,
request units, public egress, Region seats, immutable backup byte-hours, and
restore work. Retries and internal replication are not separate billable
requests. TerraScale freezes numeric prices, request-unit weights, included
allowances, and automatic dedicated-placement thresholds from benchmark and
cost evidence before GA. Billing reconstructs only from durable usage segments.

## Compliance, support, and incident response

A completed SOC 2 Type I examination and report are GA blockers. The SOC 2
Type II observation period must already be active at GA, and TerraScale targets
the Type II report within twelve months. Controls may align with ISO 27001, but
ISO certification is not an initial release blocker. Marketing and contracts
claim only completed reports and their exact scope.

Production has staffed 24-hour incident response and a public status channel.
Support acknowledges incidents within these maximum times:

| Severity | Acknowledgement |
| --- | --- |
| Sev1, security or broad production outage | 15 minutes |
| Sev2, major degradation without a safe workaround | 1 hour |
| Sev3, limited impact with a workaround | 1 business day |
| Sev4, question or minor defect | 3 business days |

TerraScale updates an active Sev1 at least every thirty minutes and an active
Sev2 at least every two hours. A customer-impacting Sev1 or security incident
gets a reviewed post-incident report within five business days unless legal or
law-enforcement restrictions forbid disclosure. On-call drills, escalation,
customer communication, evidence preservation, credential rotation, restore,
and Region-loss exercises must pass before GA and at least quarterly afterward.

## Qualification evidence

GA evidence includes the uninterrupted thirty-day soak, availability and
latency SLI calculations, every Region pair, capacity and quota reservations,
fault-domain placement, cross-Region routing, Vault and Object Storage private
access, public-ingress denial, Region-code boundary tests, mapping changes,
Member and Region loss, surge upgrades, backup and restore, billing replay,
incident drills, license review, and the scoped SOC report. It also includes
native Linux x86_64 and Ampere ARM64 lanes, cross-architecture artifact
interchange, a mixed-architecture replica set, the Ampere cost per admitted
durable operation receipt, Tier-2-only recovery and SLO evidence, every supported
optional-tier combination, and intentional Tier 1 and Tier 3 disablement.

Every customer-visible receipt and support artifact uses airport `RegionCode`
values. A CI boundary scan fails if an OCI Region identifier or `SJC` appears in
OpenAPI, SDKs, SQL, checkpoints, placement contracts,
customer fixtures, examples, customer-visible logs, or customer receipts.
