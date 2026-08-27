# Support .NET 10 LTS

The TerraScale .NET SDK supports .NET 10 LTS and does not require customers to install a preview runtime. Release qualification and published packages must pass against the supported LTS target.

The package metadata records the exact Git source revision supplied by the SDK pack targets. Candidate verification compares that commit with the expected checkout revision before restoring the immutable archive into a clean `net10.0` consumer outside repository build imports. A no-argument pack fails closed on any dirty source tree; publication validation consumes a separately staged package, digest, and revision handoff rather than rebuilding it. Compatibility with a public API candidate is release evidence, not a runtime compatibility switch.
