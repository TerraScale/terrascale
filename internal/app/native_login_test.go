package app

import (
	"encoding/json/v2"
	"io"
	"net/http"
	"net/http/cookiejar"
	"net/http/httptest"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"

	"terrascale.tech/dashboard/internal/adapters/terrabase"
	"terrascale.tech/dashboard/internal/platform/config"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
)

// refusingTSAuth is a ts-auth offering the headless API that refuses every
// password and accepts every code request, which is all the throttle in
// front of it needs.
func refusingTSAuth(t *testing.T) *httptest.Server {
	t.Helper()
	var server *httptest.Server
	mux := http.NewServeMux()
	mux.HandleFunc("GET /.well-known/openid-configuration", func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_ = json.MarshalWrite(w, map[string]any{
			"issuer": server.URL, "authorization_endpoint": server.URL + "/authorize", "token_endpoint": server.URL + "/oauth/token",
			"jwks_uri": server.URL + "/jwks", "response_types_supported": []string{"code"}, "subject_types_supported": []string{"public"},
			"id_token_signing_alg_values_supported": []string{"ES256"}, "code_challenge_methods_supported": []string{"S256"},
			"grant_types_supported": []string{"authorization_code", "refresh_token",
				"urn:terrascale:params:oauth:grant-type:password", "urn:terrascale:params:oauth:grant-type:email-code"},
		})
	})
	mux.HandleFunc("POST /oauth/token", func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusBadRequest)
		_, _ = io.WriteString(w, `{"error":"invalid_grant"}`)
	})
	mux.HandleFunc("POST /headless/v1/email-code/start", func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusAccepted)
	})
	server = httptest.NewServer(mux)
	t.Cleanup(server.Close)
	return server
}

var csrfField = regexp.MustCompile(`name="_csrf" value="([^"]+)"`)

func TestNativeAuthPagesWithTerraBaseDuringIssuerOutage(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	tb := terrabasetest.Start(t)
	db, key := terrabasetest.Database(t, tb, "dashboard")
	if err := terrabase.EnsureCollections(t.Context(), db); err != nil {
		t.Fatal(err)
	}
	if err := terrabase.New(db).SeedPlans(t.Context()); err != nil {
		t.Fatal(err)
	}
	var issuerCalls atomic.Int64
	issuer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		issuerCalls.Add(1)
		http.Error(w, "unavailable", http.StatusServiceUnavailable)
	}))
	t.Cleanup(issuer.Close)
	application, err := New(config.Config{
		TerraBaseURL: tb.URL, TerraBaseAPIKey: key, TerraBaseProjectID: db.Project(), TerraBaseDatabase: db.Name(),
		TerraBaseHTTPClient: tb.Client, CSRFSecret: []byte(strings.Repeat("r", 32)),
		TSAuth: config.TSAuth{Issuer: issuer.URL, ClientID: "ts-dashboard", ClientSecret: "secret", RedirectURL: "http://dashboard.test/login/callback"},
	})
	if err != nil {
		t.Fatal(err)
	}
	for _, path := range []string{"/login", "/login/code", "/onboarding", "/reset-password"} {
		response := httptest.NewRecorder()
		application.Handler.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "http://dashboard.test"+path, nil))
		if response.Code != http.StatusOK || response.Header().Get("Location") != "" || !strings.Contains(response.Body.String(), "<form") {
			t.Fatalf("GET %s during issuer outage = %d Location %q: %s", path, response.Code, response.Header().Get("Location"), response.Body)
		}
	}
	if got := issuerCalls.Load(); got != 0 {
		t.Fatalf("rendering native forms called the unavailable issuer %d times", got)
	}
}

// TestNativeSignInThrottlesWithTerraBase guards the sign-in design with the
// dashboard wired as production: TerraBase state, ts-auth's headless API,
// the Fly client address header and the CSRF guard. /login renders the
// dashboard's form; a first attempt from a new client for a new email
// reaches ts-auth rather than being throttled (#180 → #186); a refusal asks
// for a wait the budget allows.
func TestNativeSignInThrottlesWithTerraBase(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	tb := terrabasetest.Start(t)
	db, key := terrabasetest.Database(t, tb, "dashboard")
	if err := terrabase.EnsureCollections(t.Context(), db); err != nil {
		t.Fatal(err)
	}
	tsAuth := refusingTSAuth(t)
	var handler http.Handler
	dashboard := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { handler.ServeHTTP(w, r) }))
	t.Cleanup(dashboard.Close)
	application, err := New(config.Config{
		TerraBaseURL: tb.URL, TerraBaseAPIKey: key, TerraBaseProjectID: db.Project(), TerraBaseDatabase: db.Name(),
		TerraBaseHTTPClient: tb.Client, CSRFSecret: []byte(strings.Repeat("r", 32)), ClientIPHeader: "Fly-Client-IP",
		TSAuth: config.TSAuth{Issuer: tsAuth.URL, ClientID: "ts-dashboard", ClientSecret: "secret", RedirectURL: dashboard.URL + "/login/callback"},
	})
	if err != nil {
		t.Fatal(err)
	}
	handler = application.Handler

	jar, err := cookiejar.New(nil)
	if err != nil {
		t.Fatal(err)
	}
	browser := &http.Client{Jar: jar, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	page, err := browser.Get(dashboard.URL + "/login")
	if err != nil {
		t.Fatal(err)
	}
	body, _ := io.ReadAll(page.Body)
	page.Body.Close()
	// Sign-in design (do not revert, docs/development.md): with ts-auth
	// configured, /login is the dashboard's own form, never a redirect to
	// ts-auth's hosted page; only /login/sso redirects.
	token := csrfField.FindSubmatch(body)
	if page.StatusCode != http.StatusOK || token == nil || strings.HasPrefix(page.Header.Get("Location"), tsAuth.URL) {
		t.Fatalf("GET /login = %d Location %q, want the dashboard's sign-in form: %s", page.StatusCode, page.Header.Get("Location"), body)
	}
	for _, marker := range []string{`type="password"`, `href="/login/code`, `href="/login/sso`} {
		if !strings.Contains(string(body), marker) {
			t.Fatalf("GET /login has no %s: %s", marker, body)
		}
	}
	post := func(path string, form url.Values) *http.Response {
		t.Helper()
		form.Set("_csrf", string(token[1]))
		request, _ := http.NewRequestWithContext(t.Context(), http.MethodPost, dashboard.URL+path, strings.NewReader(form.Encode()))
		request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
		request.Header.Set("Origin", dashboard.URL)
		request.Header.Set("Fly-Client-IP", "203.0.113.9")
		response, err := browser.Do(request)
		if err != nil {
			t.Fatal(err)
		}
		response.Body.Close()
		return response
	}

	if response := post("/login", url.Values{"email": {"first-time@example.test"}, "password": {"not the password"}}); response.StatusCode != http.StatusUnauthorized {
		t.Fatalf("first password attempt = %d Retry-After %q, want ts-auth's refusal", response.StatusCode, response.Header.Get("Retry-After"))
	}
	if response := post("/login/code", url.Values{"email": {"code-first-time@example.test"}}); response.StatusCode != http.StatusOK {
		t.Fatalf("first code request = %d Retry-After %q, want the code step", response.StatusCode, response.Header.Get("Retry-After"))
	}
	// Spending the email's budget refuses the next guess for a wait the
	// budget allows, not until the end of time.
	var refused *http.Response
	for range 10 {
		refused = post("/login", url.Values{"email": {"first-time@example.test"}, "password": {"not the password"}})
	}
	wait, _ := strconv.Atoi(refused.Header.Get("Retry-After"))
	if refused.StatusCode != http.StatusTooManyRequests || wait < 1 || wait > 60 {
		t.Fatalf("guess past the budget = %d Retry-After %q, want 429 within a minute", refused.StatusCode, refused.Header.Get("Retry-After"))
	}
}
