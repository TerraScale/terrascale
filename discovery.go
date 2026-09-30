package sdk

import (
	"errors"
	"fmt"
	"strings"
	"time"
)

// DefaultDiscoveryBaseURL is the canonical Atlas metadata origin. Metadata
// does not grant authority to perform product or management operations.
const DefaultDiscoveryBaseURL = "https://atlas.terrascale.tech"

// publicAPIHostname identifies the test API host for explicit rejection.
const publicAPIHostname = "api.terrascale.tech"

const discoveryTimeout = 3 * time.Second

// ErrManagementDiscoveryUnavailable indicates that no management endpoint
// authority has been configured. Native admission and the public region catalog
// cannot supply a product or management origin.
var ErrManagementDiscoveryUnavailable = errors.New("management endpoint authority is unavailable; configure an explicit product API origin")

// RoutingError reports that a request has no authorized endpoint. Err preserves
// the underlying local admission failure or actual discovery response.
type RoutingError struct {
	Route  string
	Method string
	Path   string
	Err    error
}

func (e *RoutingError) Error() string {
	detail := "endpoint authority is unavailable"
	if e.Err != nil {
		detail = e.Err.Error()
	}
	return fmt.Sprintf("sdk: %s %s (%s): no authorized endpoint: %s", e.Method, e.Path, e.Route, detail)
}

func (e *RoutingError) Unwrap() error { return e.Err }

// isDevOrigin reports a loopback development metadata hostname.
func isDevOrigin(host string) bool {
	return host == "localhost" || strings.HasSuffix(host, ".localhost") ||
		host == "127.0.0.1" || host == "::1" || host == "[::1]"
}
