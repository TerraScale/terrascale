package sdk

import (
	"net"
	"net/url"
	"strings"
)

// publicAPIHostname identifies the test API host for explicit rejection.
const publicAPIHostname = "api.terrascale.tech"

// resolveServiceBase validates and normalizes service URLs. Auth deployments
// may include a path prefix; status requires an origin without one.
func resolveServiceBase(raw, field string, allowPath bool) (string, error) {
	base := strings.TrimSpace(raw)
	u, err := url.Parse(base)
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" {
		return "", &ValidationError{Field: field, Message: "must be an absolute http or https URL"}
	}
	if u.RawQuery != "" || u.ForceQuery || strings.Contains(base, "#") {
		return "", &ValidationError{Field: field, Message: "must not contain a query or fragment"}
	}
	if u.User != nil {
		return "", &ValidationError{Field: field, Message: "must not contain userinfo"}
	}
	if u.Scheme == "http" && !isDevTransport(u.Hostname()) {
		return "", &ValidationError{Field: field, Message: "must use HTTPS outside loopback"}
	}
	if !allowPath && u.Path != "" && u.Path != "/" {
		return "", &ValidationError{Field: field, Message: "must not contain a path prefix"}
	}
	return strings.TrimSuffix(u.String(), "/"), nil
}

// isDevTransport permits HTTP only for loopback test servers.
func isDevTransport(hostname string) bool {
	host := strings.ToLower(hostname)
	if host == "localhost" {
		return true
	}
	ip := net.ParseIP(host)
	return ip != nil && ip.IsLoopback()
}
