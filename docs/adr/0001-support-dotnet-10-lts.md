# Support .NET 10 LTS

The TerraScale .NET SDK supports .NET 10 LTS and does not require customers to install a preview runtime. Release qualification and published packages must pass against the supported LTS target.

The SDK packages multi-target `net10.0` and `net11.0`; toolchains older than .NET 11 build only `net10.0`. The package metadata records the Git source revision (`RepositoryCommit` from `SourceRevisionId`). Compatibility with a public API candidate is release evidence, not a runtime compatibility switch.
