package core

import (
	"encoding/json/v2"
	"errors"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"strings"
)

// VerifyCSRF rejects a request without a valid CSRF token with 403 and
// reports whether the handler may continue.
func (d Deps) VerifyCSRF(w http.ResponseWriter, r *http.Request) bool {
	if d.CSRF == nil || d.CSRF.Verify(r) != nil {
		http.Error(w, "invalid CSRF token", http.StatusForbidden)
		return false
	}
	return true
}

// FormPrincipal verifies a form post's CSRF token and session, then parses
// its form. It writes the response and reports false when the handler must
// stop; signed-out browsers are sent to the login page.
func (d Deps) FormPrincipal(w http.ResponseWriter, r *http.Request) (Principal, bool) {
	if !d.VerifyCSRF(w, r) {
		return Principal{}, false
	}
	p, ok := d.Sessions.Principal(r)
	if !ok {
		if WantsHTML(r) {
			RedirectToLogin(w, r)
		} else {
			WriteAccessError(w, ErrUnauthorized)
		}
		return Principal{}, false
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, "invalid form", http.StatusBadRequest)
		return Principal{}, false
	}
	return p, true
}

// WriteAccessError writes the plain-text response for authentication,
// authorization, and missing-resource errors. It writes nothing and reports
// false for any other error, which the feature maps itself.
func WriteAccessError(w http.ResponseWriter, err error) bool {
	switch {
	case errors.Is(err, ErrUnauthorized):
		http.Error(w, "authentication required", http.StatusUnauthorized)
	case errors.Is(err, ErrForbidden):
		http.Error(w, "forbidden", http.StatusForbidden)
	case errors.Is(err, ErrNotFound):
		http.Error(w, "not found", http.StatusNotFound)
	default:
		return false
	}
	return true
}

// RedirectToLogin sends a signed-out browser to the login page. Page requests
// carry their URL so sign-in returns the visitor to it.
func RedirectToLogin(w http.ResponseWriter, r *http.Request) {
	location := "/login"
	if r.Method == http.MethodGet {
		location += "?returnTo=" + url.QueryEscape(r.URL.RequestURI())
	}
	http.Redirect(w, r, location, http.StatusSeeOther)
}

// WriteJSON encodes value as the response body with the given status.
func WriteJSON(w http.ResponseWriter, status int, value any) {
	encoded, err := json.Marshal(value)
	if err != nil {
		http.Error(w, "encode response failed", http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_, _ = w.Write(encoded)
}

// messageParams carry the status message a dashboard redirect hands to the
// next page, such as /settings?error=… after a failed form post.
var messageParams = []string{"error", "notice", "toast"}

// draftPrefix starts the parameters that carry a failed form's input back to
// it, such as /api-keys?draftName=…, so nothing has to be typed again.
const draftPrefix = "draft"

// DropMessages removes the status message and form draft parameters from
// query and reports whether it had any. Pages honor them only after the
// dashboard's own redirects, so neither a foreign link nor a URL built from
// request input may carry them.
func DropMessages(query url.Values) bool {
	found := false
	for name := range query {
		if slices.Contains(messageParams, name) || strings.HasPrefix(name, draftPrefix) {
			query.Del(name)
			found = true
		}
	}
	return found
}

// WantsHTML reports whether a form post came from a browser page rather than
// an API client, so handlers can redirect instead of returning JSON.
func WantsHTML(r *http.Request) bool {
	return strings.Contains(r.Header.Get("Accept"), "text/html")
}

// ClampPage parses a 1-based page number and bounds it to [1, pages].
func ClampPage(raw string, pages int) int {
	page, err := strconv.Atoi(raw)
	if err != nil || page < 1 {
		return 1
	}
	return min(page, pages)
}

// MCPPath is where the dashboard serves its MCP endpoint.
const MCPPath = "/mcp"

// MCPEndpoint returns the absolute MCP endpoint URL for the request's host.
// TLS terminates at the Fly proxy, which reports the scheme it served.
func MCPEndpoint(r *http.Request) string {
	scheme := "http"
	if r.TLS != nil || r.Header.Get("X-Forwarded-Proto") == "https" {
		scheme = "https"
	}
	return scheme + "://" + r.Host + MCPPath
}
