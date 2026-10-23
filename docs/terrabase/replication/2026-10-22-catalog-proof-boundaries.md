# Catalog discovery and freshness proof boundaries

An existing catalog waits for discovery to list its own machine before the
startup gate evaluates peer pulls or its restore anchor. A matching anchor proves
that this machine did not roll back its file; it cannot prove that a directory
response omitting this machine describes the cluster. The gate retries after
discovery recovers. An empty, newly created catalog retains its bootstrap path.

Catalog freshness checks finish recorded-membership reads and incarnation
validation before their final proof-age check. A proof that expires during those
reads cannot authorize a freshness-sensitive operation. A subsequent successful
comparison can establish a new proof.

The integration regressions cover an existing anchored catalog with empty
discovery, recovery once self is listed, and proof expiry during incarnation
validation. These changes add no wire fields or persisted state and preserve the
existing peer trust and restore-anchor requirements during rolling deployment.
