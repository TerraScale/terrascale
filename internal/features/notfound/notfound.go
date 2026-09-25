// Package notfound renders the standalone dashboard page for missing browser pages.
package notfound

import (
	"io"
	"net/http"
	"strings"

	"terrascale.tech/dashboard/internal/ui/icons"
)

// Render writes an illustrated 404 for browser GET requests and a bare 404 for
// API, JSON, event-stream, Datastar, and non-GET requests.
func Render(w http.ResponseWriter, r *http.Request) {
	if r.Method == http.MethodHead {
		w.WriteHeader(http.StatusNotFound)
		return
	}
	if !wantsPage(r) {
		http.NotFound(w, r)
		return
	}

	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.WriteHeader(http.StatusNotFound)
	_, _ = io.WriteString(w, `<!DOCTYPE html>
<html lang="en" data-theme="dark">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Page not found · TerraScale</title>
<link rel="icon" type="image/svg+xml" href="/brand/logo-icon-blue.svg">
<link rel="stylesheet" href="/css/terrascale.css" data-dashboard-css>
</head>
<body>
<main class="ts-notfound" aria-labelledby="notfound-title">
<header class="ts-notfound-header"><a href="/" aria-label="TerraScale dashboard"><img src="/brand/logo-terrascale-principal.svg" alt="TerraScale" width="183" height="44" decoding="async"></a></header>
<div class="ts-notfound-floor" aria-hidden="true"></div>
<div class="ts-notfound-inner">
<div class="ts-notfound-copy">
<p class="ts-notfound-eyebrow">Page not found</p>
<img class="ts-notfound-number" src="/img/404-number.webp" alt="" width="415" height="160" aria-hidden="true" decoding="async">
<h1 id="notfound-title">We can’t find that page.</h1>
<p class="ts-notfound-text">The page you’re looking for has moved, or never existed. Let’s get you back on track.</p>
<div class="ts-notfound-actions">
<a class="ts-notfound-button ts-notfound-button-primary" href="/">`)
	_ = icons.Home(20).Render(r.Context(), w)
	_, _ = io.WriteString(w, ` Back to dashboard</a>
<a class="ts-notfound-button ts-notfound-button-secondary" href="https://terrascale.tech/contact">`)
	_ = icons.Mail(20).Render(r.Context(), w)
	_, _ = io.WriteString(w, ` Contact support</a>
</div>
</div>
<img class="ts-notfound-art" src="/img/404-illustration.webp" alt="A surprised cat playing with blue 404 numbers" width="900" height="700" decoding="async">
</div>
</main>
</body>
</html>`)
}

func wantsPage(r *http.Request) bool {
	if r.Method != http.MethodGet || r.URL.Path == "/api" || strings.HasPrefix(r.URL.Path, "/api/") {
		return false
	}
	accept := strings.ToLower(r.Header.Get("Accept"))
	if strings.Contains(accept, "application/json") || strings.Contains(accept, "text/event-stream") {
		return false
	}
	for name := range r.Header {
		if strings.EqualFold(name, "Datastar-Request") {
			return false
		}
	}
	return true
}
