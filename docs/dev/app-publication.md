# Application publication

Main pushes start ts-auth publication in `ci.yml` and dashboard publication in
`images-ghcr.yml`. Their existing manual dispatches remain available to the Sites
reconciler. Neither publisher depends on another job. Each service retains its
release lock and persistent deployment state, including the two-attempt limit.

The locked publisher checks the latest 30 successful pull-request runs of
`ci.yml`. A run must belong to this repository, its head commit must have the
same Git tree as the publisher's commit, and its latest jobs must contain a
successful `all-green`. The publisher checks these facts through GitHub's API
using its existing contents/actions read permissions. A matching tree permits
publication before the main journey completes. Main still runs its journey on
every push. Without a matching PR tree, the publisher requires the existing
successful main-push CI, `all-green`, and both journey shards. Sites dispatches
the publisher after that qualification as before. Worker deployments, SDK tags,
and TerraBase releases retain their existing main qualification.

An immutable image hit avoids .NET, Node, and Buildx builder setup. The digest,
architecture, OS, and revision label are still checked before promoting `main`.
Cache misses restore locked packages with the .NET package cache and publish
the existing production arm64 image. Dashboard publish performs the checked
compilation once, then runs the existing lint. PR validation is unchanged.

Every publication still creates and verifies fresh build provenance and SBOM
attestations. OCIR publication requires both resulting bundles; it follows
attestation and verification. The local-registry proof and independent
repositories publish concurrently,
each using the same image copy, digest check, bundle roundtrip, referrer
discovery, and tag verification. All children finish and any failure fails
publication before release selection.

`Select the signed release` creates the existing separate release-selection
attestation, verifies its bundles in every repository, and updates the same
release tags. Only independent application bundle attachments overlap. The
tag ordering and TerraBase's interleaved regional waits are unchanged.
`Wait for rollout` downloads and verifies the qualified orchestrator binary
and waits for every region. Live revision proof and the production journey's
source receipt follow that wait. Production journey detection recognizes the
completed wait step. Hosts verify the same provenance and release-selection
predicate from the same workflow identities and allowed triggers.

The cache-hit objective is selection within 120 seconds of the merge. Runner
queueing, a previous rollout holding the service lock, GitHub API latency,
registry latency, and signing latency remain outside a deterministic bound.
A missing recent PR tree match also waits for main qualification. Cache misses
still include checked compilation. The two new step durations expose selection
and rollout separately; a successful main publication is required to measure
the new elapsed time. Historical dispatch timing does not prove the objective.
