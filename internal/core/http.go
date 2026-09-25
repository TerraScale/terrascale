package core

import (
	"encoding/json/v2"
	"errors"
	"net/http"
	"net/url"
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
