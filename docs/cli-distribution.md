# Customer CLI distribution

DECISION: the customer CLI is the Go `ts` executable in [terrascale](https://github.com/TerraScale/terrascale/tree/main/sdk/go/cmd/ts). Reason: the public installer, Homebrew and Go installation already distribute this implementation, including its authenticated customer workflows.

The unused C# help/version stub has been removed. This monorepo does not publish a second customer CLI. The existing ts-auth public OAuth client and device authorization flow continue to serve the Go CLI. No HTTP, authentication or storage format changes are introduced by this decision.

Install the CLI with the [public installation instructions](https://docs.terrascale.tech/cli). Its releases remain in the `0.0.X` series, published as `sdk/go/v0.0.X` tags in [TerraScale/terrascale](https://github.com/TerraScale/terrascale/releases) (see [sdk/go/scripts/install.sh](../sdk/go/scripts/install.sh)). The public installer retrieves checksummed assets from those releases.
