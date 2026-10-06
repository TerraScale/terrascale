package sdk

import (
	"strings"
	"time"
)

// DefaultDiscoveryBaseURL is the canonical Atlas metadata origin. Metadata
// does not grant authority to perform product or management operations.
const DefaultDiscoveryBaseURL = "https://atlas.terrascale.tech"

// publicAPIHostname identifies the test API host for explicit rejection.
const publicAPIHostname = "api.terrascale.tech"

const discoveryTimeout = 3 * time.Second

// isDevOrigin reports a loopback development metadata hostname.
func isDevOrigin(host string) bool {
	return host == "localhost" || strings.HasSuffix(host, ".localhost") ||
		host == "127.0.0.1" || host == "::1" || host == "[::1]"
}
