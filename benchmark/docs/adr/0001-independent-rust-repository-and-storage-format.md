---
status: accepted
---

# Use an independent Rust repository and a new storage format

TerraBaseV2 is an independent Rust repository with a new storage format. V1
data moves through a manifest-verified logical migration; V2 does not claim to
open V1 files directly. This keeps the engine's format choices local while
making migration evidence explicit and reviewable.
