# Copy-refill projection cleanup

Forgetting an index definition removes its physical index, and forgetting a unique
constraint or its winning document reprojects uniqueness. Removing a unique winner
makes the next eligible document visible. These changes share the writer
transaction with state removal.

The peer protocol and on-disk schema are unchanged by this projection cleanup.
