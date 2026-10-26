# Customer CLI distribution

DECISION: the customer CLI is the Go `ts` executable in [ts-go](https://github.com/TerraScale/ts-go/tree/main/cmd/ts). Reason: the public installer, Homebrew and Go installation already distribute this implementation, including its authenticated customer workflows.

The unused C# help/version stub has been removed. This monorepo does not publish a second customer CLI. The existing ts-auth public OAuth client and device authorization flow continue to serve the Go CLI. No HTTP, authentication or storage format changes are introduced by this decision.

Install the CLI with the [public installation instructions](https://docs.terrascale.tech/cli). Its releases remain in the `0.0.X` series. The public installer continues to retrieve checksummed assets from ts-go releases.
