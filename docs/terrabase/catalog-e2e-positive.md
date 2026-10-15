# Catalog E12 is a required passing scenario

The catalog scenario creates a database in SJC, writes to it from GRU, and races the same database name in both regions. It now runs directly with all existing convergence assertions and owned-resource cleanup, without an expected-failure wrapper.

The deployed main run [38692987822](https://github.com/TerraScale/terrascale/actions/runs/38692987822) completed this scenario and failed only because its obsolete wrapper rejected the unexpected success. The older expected-failure lists and run tables record the earlier catalog gap; E12 is no longer in `Gaps.cs`.

Test selection and total scenario count are unchanged. A catalog regression now fails directly. This change has no production API, authentication, storage or format impact.
