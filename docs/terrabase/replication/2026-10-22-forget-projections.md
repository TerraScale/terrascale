# Copy-refill projection cleanup

Forgetting metadata removes its physical index or collection projection and
reprojects uniqueness. Collection cleanup removes explicitly absent keys while
retaining source-present members. Removing a unique winner makes the next eligible
document visible. These changes share
the writer transaction with state removal and maintained repair digests.

The peer protocol and on-disk schema are unchanged by this projection cleanup.
