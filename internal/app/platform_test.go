package app

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json/v2"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/adapters/terrabase"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/config"
	"terrascale.tech/dashboard/internal/platform/mcpserver"
	"terrascale.tech/dashboard/internal/platform/platformapi"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
)

// tsAuthTenant is ts-auth's terrascale tenant as the platform API sees it:
// discovery and a JWKS whose one ES256 key signs the tenant's tokens.
type tsAuthTenant struct {
	server *httptest.Server
	key    *ecdsa.PrivateKey
	// keysDown makes the JWKS answer 503.
	keysDown atomic.Bool
}

func newTSAuthTenant(t *testing.T) *tsAuthTenant {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	tenant := &tsAuthTenant{key: key}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /.well-known/openid-configuration", func(w http.ResponseWriter, _ *http.Request) {
		issuer := tenant.server.URL
		writeTestJSON(w, map[string]any{
			"issuer": issuer, "authorization_endpoint": issuer + "/oauth/authorize", "token_endpoint": issuer + "/oauth/token",
			"jwks_uri": issuer + "/.well-known/jwks.json", "id_token_signing_alg_values_supported": []string{"ES256"},
			"response_types_supported": []string{"code"}, "subject_types_supported": []string{"public"},
			"code_challenge_methods_supported": []string{"S256"},
			"grant_types_supported": []string{
				"authorization_code", "refresh_token",
				"urn:terrascale:params:oauth:grant-type:password", "urn:terrascale:params:oauth:grant-type:email-code",
			},
		})
	})
	mux.HandleFunc("GET /.well-known/jwks.json", func(w http.ResponseWriter, _ *http.Request) {
		if tenant.keysDown.Load() {
			http.Error(w, "unavailable", http.StatusServiceUnavailable)
			return
		}
		point, err := key.PublicKey.Bytes()
		if err != nil {
			t.Error(err)
		}
		writeTestJSON(w, map[string]any{"keys": []map[string]string{{
			"kty": "EC", "crv": "P-256", "kid": "tenant", "alg": "ES256", "use": "sig",
			"x": base64.RawURLEncoding.EncodeToString(point[1:33]), "y": base64.RawURLEncoding.EncodeToString(point[33:]),
		}}})
	})
	tenant.server = httptest.NewServer(mux)
	t.Cleanup(tenant.server.Close)
	return tenant
}

func writeTestJSON(w http.ResponseWriter, value any) {
	w.Header().Set("Content-Type", "application/json")
	_ = json.MarshalWrite(w, value)
}

// token signs a JWT with the tenant's key: by default an organization token
// for the platform API, exchanged for subject in organization, which change
// edits first.
func (tenant *tsAuthTenant) token(t *testing.T, subject, organization string, change func(header, claims map[string]any)) string {
	t.Helper()
	header := map[string]any{"alg": "ES256", "kid": "tenant", "typ": "at+jwt"}
	claims := map[string]any{
		"iss": tenant.server.URL, "sub": subject, "client_id": "ts-dashboard",
		"aud": []string{"https://gru.api.terrascale.tech", platformapi.Audience, "https://mcp.terrascale.tech"},
		"exp": time.Now().Add(15 * time.Minute).Unix(), "iat": time.Now().Unix(),
		"email": subject + "@example.test", "email_verified": true, "org_id": organization,
		"scope": "databases:read documents:read shapes:read usage:read billing:read billing:write",
	}
	if change != nil {
		change(header, claims)
	}
	encode := func(value any) string {
		encoded, err := json.Marshal(value)
		if err != nil {
			t.Fatal(err)
		}
		return base64.RawURLEncoding.EncodeToString(encoded)
	}
	signed := encode(header) + "." + encode(claims)
	digest := sha256.Sum256([]byte(signed))
	r, s, err := ecdsa.Sign(rand.Reader, tenant.key, digest[:])
	if err != nil {
		t.Fatal(err)
	}
	return signed + "." + base64.RawURLEncoding.EncodeToString(append(r.FillBytes(make([]byte, 32)), s.FillBytes(make([]byte, 32))...))
}

// TestPlatformHosts serves the platform API on platform.terrascale.tech to
// ts-auth organization and project tokens, and the MCP server on
// mcp.terrascale.tech, on the memory store and on TerraBase as production
// does.
func TestPlatformHosts(t *testing.T) {
	t.Run("memory", func(t *testing.T) {
		// The demo owner, whose ts-auth user the first token links by email.
		testPlatformHosts(t, config.Config{Demo: true}, "mario", demoAccountEmail, demoLabsOrganizationID, "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA")
	})
	t.Run("terrabase", func(t *testing.T) {
		if testing.Short() {
			t.Skip("starts pinned TerraBase release")
		}
		tb := terrabasetest.Start(t)
		db, key := terrabasetest.Database(t, tb, "dashboard")
		if err := terrabase.EnsureCollections(t.Context(), db); err != nil {
			t.Fatal(err)
		}
		store := terrabase.New(db)
		if err := store.SeedPlans(t.Context()); err != nil {
			t.Fatal(err)
		}
		account, err := store.CreateAccount(t.Context(), core.User{Name: "Ada", Email: "ada@example.test", AuthSubject: "ada", PasswordHash: "unused", Role: "owner"})
		if err != nil {
			t.Fatal(err)
		}
		testPlatformHosts(t, config.Config{
			TerraBaseURL: tb.URL, TerraBaseAPIKey: key, TerraBaseProjectID: db.Project(), TerraBaseDatabase: db.Name(),
			TerraBaseHTTPClient: tb.Client, CSRFSecret: []byte(strings.Repeat("p", 32)),
		}, "ada", account.User.Email, account.ActiveOrganizationID, account.ActiveProjectID)
	})
}

// testPlatformHosts calls the platform API as the ts-auth user subject, whose
// verified email is address and who owns organization and its project.
func testPlatformHosts(t *testing.T, cfg config.Config, subject, address, organization, project string) {
	tenant := newTSAuthTenant(t)
	cfg.TSAuth = config.TSAuth{
		Issuer: tenant.server.URL, ClientID: "ts-dashboard", ClientSecret: "dashboard-secret",
		RedirectURL: "https://dash.terrascale.tech/login/callback",
	}
	application, err := New(cfg)
	if err != nil {
		t.Fatal(err)
	}
	call := func(method, target, token, body string) *httptest.ResponseRecorder {
		t.Helper()
		request := httptest.NewRequest(method, target, strings.NewReader(body))
		if token != "" {
			request.Header.Set("Authorization", "Bearer "+token)
		}
		response := httptest.NewRecorder()
		application.Handler.ServeHTTP(response, request)
		return response
	}
	decode := func(response *httptest.ResponseRecorder, want int, target any) {
		t.Helper()
		if response.Code != want {
			t.Fatalf("status = %d %s, want %d", response.Code, response.Body, want)
		}
		if err := json.Unmarshal(response.Body.Bytes(), target); err != nil {
			t.Fatalf("decode %s: %v", response.Body, err)
		}
	}
	email := func(_, claims map[string]any) { claims["email"] = address }
	token := func(change func(header, claims map[string]any)) string {
		return tenant.token(t, subject, organization, func(header, claims map[string]any) {
			email(header, claims)
			if change != nil {
				change(header, claims)
			}
		})
	}
	base := "http://" + platformapi.Host
	organizationURL := base + "/v1/organizations/" + organization

	var document struct {
		OpenAPI string                               `json:"openapi"`
		Paths   map[string]map[string]map[string]any `json:"paths"`
	}
	decode(call(http.MethodGet, base+"/openapi.json", "", ""), http.StatusOK, &document)
	billing := document.Paths["/v1/organizations/{organizationId}/billing"]["get"]
	if document.OpenAPI != "3.1.0" || billing["operationId"] != "get_billing" ||
		fmt.Sprint(billing["security"]) != "[map[tsAuth:[billing:read]]]" ||
		document.Paths["/v1/organizations/{organizationId}/plan"]["put"]["requestBody"] == nil {
		t.Fatalf("OpenAPI document = %+v", document)
	}

	// Keys ts-auth cannot serve say nothing about the token.
	tenant.keysDown.Store(true)
	if response := call(http.MethodGet, organizationURL+"/billing", token(nil), ""); response.Code != http.StatusServiceUnavailable {
		t.Fatalf("platform API without ts-auth keys = %d %s, want 503", response.Code, response.Body)
	}
	tenant.keysDown.Store(false)

	refused := map[string]string{
		"no token":         "",
		"identity token":   token(func(header, _ map[string]any) { header["typ"] = "JWT" }),
		"sign-in token":    token(func(_, claims map[string]any) { delete(claims, "org_id") }),
		"other audience":   token(func(_, claims map[string]any) { claims["aud"] = []string{"https://gru.api.terrascale.tech"} }),
		"API key token":    token(func(_, claims map[string]any) { claims["sub"] = "tsk_3k9f2m8q1x7v4c6n0b5z"; delete(claims, "email") }),
		"unverified email": token(func(_, claims map[string]any) { claims["email_verified"] = false }),
		"no scopes":        token(func(_, claims map[string]any) { claims["scope"] = " " }),
		"expired":          token(func(_, claims map[string]any) { claims["exp"] = time.Now().Add(-time.Minute).Unix() }),
		"other issuer":     token(func(_, claims map[string]any) { claims["iss"] = "https://gru.auth.terrascale.tech/other" }),
	}
	for name, refusedToken := range refused {
		// The challenge names an error only for a token that was presented.
		challenge := `Bearer realm="TerraScale platform", error="invalid_token"`
		if refusedToken == "" {
			challenge = `Bearer realm="TerraScale platform"`
		}
		response := call(http.MethodGet, organizationURL+"/billing", refusedToken, "")
		if response.Code != http.StatusUnauthorized || response.Header().Get("WWW-Authenticate") != challenge {
			t.Errorf("%s: platform API = %d %q, want 401 with %q", name, response.Code, response.Header().Get("WWW-Authenticate"), challenge)
		}
	}
	// The MCP server keeps personal access tokens until MCP OAuth ships.
	if response := call(http.MethodPost, "http://"+mcpserver.Host+"/", token(nil), "{}"); response.Code != http.StatusUnauthorized {
		t.Errorf("MCP server = %d for a ts-auth token, want 401", response.Code)
	}

	organizationToken := token(nil)
	var summary struct {
		OrganizationID string `json:"organizationId"`
		PlanKey        string `json:"planKey"`
		CanManage      bool   `json:"canManage"`
	}
	decode(call(http.MethodGet, organizationURL+"/billing?projectId=prj_elsewhere", organizationToken, ""), http.StatusOK, &summary)
	if summary.OrganizationID != organization || summary.PlanKey == "" || !summary.CanManage {
		t.Fatalf("billing = %+v", summary)
	}
	var invoices struct {
		Page int `json:"page"`
	}
	decode(call(http.MethodGet, organizationURL+"/invoices?page=1&status=paid", organizationToken, ""), http.StatusOK, &invoices)
	// Authentication schemes are case-insensitive (RFC 9110, section 11.1).
	lowercase := httptest.NewRequest(http.MethodGet, organizationURL+"/plans", nil)
	lowercase.Header.Set("Authorization", "bearer "+organizationToken)
	response := httptest.NewRecorder()
	application.Handler.ServeHTTP(response, lowercase)
	if response.Code != http.StatusOK {
		t.Fatalf("plans with a lowercase bearer scheme = %d %s", response.Code, response.Body)
	}
	var controls struct {
		SpendCapCents int64 `json:"spendCapCents"`
	}
	decode(call(http.MethodPut, organizationURL+"/spending-controls", organizationToken, `{"organizationCapCents":5000}`), http.StatusOK, &controls)
	if controls.SpendCapCents != 5000 {
		t.Fatalf("spend cap = %d, want 5000", controls.SpendCapCents)
	}
	projectToken := token(func(_, claims map[string]any) { claims["project_id"] = project })
	var usage struct {
		ProjectID string `json:"projectId"`
	}
	decode(call(http.MethodGet, organizationURL+"/projects/"+project+"/usage", projectToken, ""), http.StatusOK, &usage)
	if usage.ProjectID != project {
		t.Fatalf("usage project = %q, want %q", usage.ProjectID, project)
	}

	var failure struct {
		Error struct {
			Code string `json:"code"`
		} `json:"error"`
	}
	for _, c := range []struct {
		name, method, target, token, body string
		status                            int
		code                              string
	}{
		{"another organization", http.MethodGet, base + "/v1/organizations/org_someone_else/billing", organizationToken, "", http.StatusNotFound, "not_found"},
		{"an organization the user is not in", http.MethodGet, base + "/v1/organizations/org_someone_else/billing",
			tenant.token(t, subject, "org_someone_else", email), "", http.StatusNotFound, "not_found"},
		{"a project route with an organization token", http.MethodGet, organizationURL + "/projects/" + project + "/usage", organizationToken, "", http.StatusNotFound, "not_found"},
		{"another project", http.MethodGet, organizationURL + "/projects/prj_elsewhere/usage", projectToken, "", http.StatusNotFound, "not_found"},
		{"a missing scope", http.MethodPut, organizationURL + "/spending-controls",
			token(func(_, claims map[string]any) { claims["scope"] = "billing:read usage:read" }), `{"organizationCapCents":1}`, http.StatusForbidden, "insufficient_scope"},
		{"a malformed query", http.MethodGet, organizationURL + "/invoices?page=first", organizationToken, "", http.StatusBadRequest, "invalid_request"},
		{"an unknown field", http.MethodPut, organizationURL + "/spending-controls", organizationToken, `{"unknown":1}`, http.StatusBadRequest, "invalid_request"},
		{"a body that is not an object", http.MethodPut, organizationURL + "/spending-controls", organizationToken, `[1]`, http.StatusBadRequest, "invalid_request"},
		{"a null body", http.MethodPut, organizationURL + "/spending-controls", organizationToken, `null`, http.StatusBadRequest, "invalid_request"},
		{"a dashboard page", http.MethodGet, base + "/login", organizationToken, "", http.StatusNotFound, "not_found"},
	} {
		decode(call(c.method, c.target, c.token, c.body), c.status, &failure)
		if failure.Error.Code != c.code {
			t.Errorf("%s: %s %s = %s, want %s", c.name, c.method, c.target, failure.Error.Code, c.code)
		}
	}

	// The dashboard's own sign-in page keeps every other host.
	if response := call(http.MethodGet, "http://dash.terrascale.tech/login", "", ""); response.Code != http.StatusOK || !strings.Contains(response.Body.String(), `type="password"`) {
		t.Fatalf("dashboard /login = %d, want its password form", response.Code)
	}
}
