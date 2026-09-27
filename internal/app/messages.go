package app

import (
	"net/http"
	"net/url"
	"strings"

	"terrascale.tech/dashboard/internal/core"
)

// dropForeignMessages strips status messages from page requests that did not
// come from the dashboard itself. Only the dashboard's own redirects set
// them, so a link in an email or on another site cannot make a trusted page
// show attacker-written text.
func dropForeignMessages(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if (r.Method == http.MethodGet || r.Method == http.MethodHead) && r.URL.RawQuery != "" && !fromDashboard(r) {
			if query := r.URL.Query(); core.DropMessages(query) {
				r = r.Clone(r.Context())
				r.URL.RawQuery = query.Encode()
			}
		}
		next.ServeHTTP(w, r)
	})
}

// fromDashboard reports whether the browser says the request came from a
// dashboard page, including a redirect after one of its form posts. Fetch
// Metadata answers when present; older browsers fall back to the Referer.
func fromDashboard(r *http.Request) bool {
	if site := r.Header.Get("Sec-Fetch-Site"); site != "" {
		return site == "same-origin"
	}
	referer, err := url.Parse(r.Header.Get("Referer"))
	return err == nil && referer.Host != "" && strings.EqualFold(referer.Host, r.Host)
}
