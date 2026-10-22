# Copy-refill projection cleanup

Forgetting metadata removes its physical index or collection projection and
reprojects uniqueness. Collection cleanup retains newer documents, and removing
a unique winner makes the next eligible document visible. These changes share
the writer transaction with state removal.

The peer protocol and on-disk schema are unchanged by this projection cleanup.
