package sdk

import (
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"maps"
	"net/http"
	"net/http/cookiejar"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"
)

func TestTSAuthResourceAudienceAndScopes(t *testing.T) {
	for _, value := range []string{"https://api.example.test/a%2Fb?tenant=x%2Fy&plus=+", "https://[::1]:443/resource", "https://api.example.test/"} {
		if err := tsAuthResourceAudience(value); err != nil {
			t.Fatalf("%q: %v", value, err)
		}
	}
	for _, value := range []string{"", "http://api.example.test", "HTTPS://api.example.test", "https://u:p@api.example.test", "https://api.example.test/#fragment", "https://api.example.test/%", "https://api.example.test/%GG", "https://api.example.test/a b", "https://api.example.test/π", "https://api.example.test/\\a", "https://api.example.test/[a]", "https:///a", "https://api.example.test/" + strings.Repeat("x", 2048)} {
		if err := tsAuthResourceAudience(value); err == nil {
			t.Fatalf("invalid audience %q", value)
		}
	}
	for _, scopes := range [][]string{{"read", "read"}, {"read write"}, {"bad\\scope"}, {"bad\"scope"}, {"π"}, {strings.Repeat("a", 257)}} {
		if err := tsAuthResourceScopes(scopes); err == nil {
			t.Fatalf("invalid scopes %q", scopes)
		}
	}
}

// Captured producer responses are decoded structurally without treating their
// historic timestamps or synthetic fixture accounts as current authority.
func TestTSAuthResourceCapturedIntrospection(t *testing.T) {
	for _, response := range capturedResourceResponses {
		if response.name != "resource-introspection" {
			continue
		}
		var result TSAuthResourceIntrospection
		if err := result.decodeHTTPResponse(response.status, []byte(response.body)); err != nil {
			t.Fatalf("capture %s: %v", response.sourceSHA, err)
		}
		if result.Active && result.Audience == "" {
			t.Fatal("active captured audience lost")
		}
	}
}

func TestTSAuthResourceIntrospectionRejectsMalformedShape(t *testing.T) {
	valid := map[string]any{"active": true, "token_type": "Bearer", "iss": "https://auth.example.test/acme", "sub": "client", "aud": "https://api.example.test/a", "client_id": "client", "scope": "read", "exp": 2000000000, "iat": 1900000000, "sid": nil, "principal_kind": "service"}
	mutations := []func(map[string]any){
		func(v map[string]any) { delete(v, "sid") }, func(v map[string]any) { v["sid"] = "session" },
		func(v map[string]any) { v["aud"] = []string{"https://api.example.test/a"} },
		func(v map[string]any) { v["family_id"] = "family" }, func(v map[string]any) { v["token_use"] = "access" },
		func(v map[string]any) { v["exp"] = 2000000000.5 }, func(v map[string]any) { v["iat"] = nil },
		func(v map[string]any) { v["sub"] = "user" }, func(v map[string]any) { v["token_type"] = "refresh_token" },
		func(v map[string]any) { v["principal_kind"] = "user" }, func(v map[string]any) { v["principal_kind"] = "unknown" },
		func(v map[string]any) { v["scope"] = nil }, func(v map[string]any) { v["scope"] = "read read" },
		func(v map[string]any) { v["active"] = nil }, func(v map[string]any) { v["active"] = false },
	}
	for index, mutate := range mutations {
		copy := maps.Clone(valid)
		mutate(copy)
		body, _ := json.Marshal(copy)
		var result TSAuthResourceIntrospection
		if err := result.decodeHTTPResponse(http.StatusOK, body); err == nil {
			t.Fatalf("mutation %d admitted", index)
		}
	}
	for _, body := range []string{`{"active":false,"active":true}`, `{"active":false} {}`, `{"ACTIVE":false}`, `[]`, `{"active":false,"scope":"read"}`, `{"active":null}`} {
		var result TSAuthResourceIntrospection
		if err := result.decodeHTTPResponse(http.StatusOK, []byte(body)); err == nil {
			t.Fatalf("malformed response %s", body)
		}
	}
}

func TestTSAuthResourceIntrospectionBindsIssuerAudienceTimeAndCredentials(t *testing.T) {
	const audience = "https://api.example.test/a%2Fb?tenant=x%2Fy&plus=+"
	const id = "resource&=+:ü"
	const secret = "secret&=+:π"
	var issuer string
	mode := "valid"
	calls := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls++
		if r.URL.Path != "/acme/oauth/introspect" || r.Method != http.MethodPost || r.Header.Get("Cookie") != "" {
			t.Error("unexpected routing or browser cookie")
		}
		decoded, err := base64.StdEncoding.DecodeString(strings.TrimPrefix(r.Header.Get("Authorization"), "Basic "))
		if err != nil || string(decoded) != url.QueryEscape(id)+":"+url.QueryEscape(secret) {
			t.Error("resource Basic components or role changed")
		}
		if err := r.ParseForm(); err != nil || len(r.PostForm) != 1 || r.PostForm.Get("token") != "opaque" {
			t.Error("extra form credentials or token changed")
		}
		w.Header().Set("Content-Type", "application/json")
		if mode == "invalid_client" {
			w.WriteHeader(401)
			io.WriteString(w, `{"error":"invalid_client","error_description":"redacted"}`)
			return
		}
		if mode == "server_error" {
			w.WriteHeader(500)
			io.WriteString(w, `{"error":"server_error","error_description":"redacted"}`)
			return
		}
		if mode == "inactive" {
			io.WriteString(w, `{"active":false}`)
			return
		}
		now := time.Now().Unix()
		iss, aud, iat, exp := issuer, audience, now, now+60
		if mode == "issuer" {
			iss = "https://other.example/acme"
		}
		if mode == "audience" {
			aud = "https://other.example/a"
		}
		if mode == "future" {
			iat = now + 60
			exp = now + 120
		}
		if mode == "expired" {
			iat = now - 60
			exp = now
		}
		json.NewEncoder(w).Encode(map[string]any{"active": true, "token_type": "Bearer", "iss": iss, "sub": "origin", "aud": aud, "client_id": "origin", "scope": "read", "exp": exp, "iat": iat, "sid": nil, "principal_kind": "service"})
	}))
	defer server.Close()
	issuer = server.URL + "/acme"
	jar, _ := cookiejar.New(nil)
	origin, _ := url.Parse(server.URL)
	jar.SetCookies(origin, []*http.Cookie{{Name: "browser", Value: "must-not-send"}})
	providerCalls := 0
	instance := newTSAuthTestInstance(t, Config{TSAuthBaseURL: server.URL, AccessToken: "sk_must_not_send", TokenProvider: func() string { providerCalls++; return "sk_must_not_send" }, HTTPClient: &http.Client{Jar: jar}, MaxRetryAttempts: 3})
	introspector, err := instance.NewResourceIntrospector(TSAuthResourceIntrospectorConfig{Issuer: issuer, ResourceAudience: audience, Authentication: TSAuthResourceAuthentication{ClientID: id, Secret: secret}})
	if err != nil {
		t.Fatal(err)
	}
	for _, current := range []string{"valid", "issuer", "audience", "future", "expired", "inactive", "invalid_client", "server_error"} {
		mode = current
		before := calls
		result, err := introspector.Introspect(t.Context(), "opaque")
		if calls != before+1 {
			t.Fatal("introspection replayed")
		}
		if current == "valid" || current == "inactive" {
			if err != nil || result.Active != (current == "valid") {
				t.Fatalf("%s: %v", current, err)
			}
		} else if err == nil {
			t.Fatalf("%s admitted", current)
		}
		if current == "invalid_client" || current == "server_error" {
			var failure *TSAuthOIDCError
			if !errors.As(err, &failure) || failure.Code != current || failure.Uncertain {
				t.Fatalf("error semantics: %v", err)
			}
		}
	}
	if providerCalls != 0 {
		t.Fatal("management credential provider was consulted")
	}
	if strings.Contains(fmt.Sprintf("%v %#v", introspector.config, introspector.config), secret) {
		t.Fatal("resource credential diagnostic leak")
	}
}

func TestTSAuthResourceGrantRefreshNarrowingAndUncertainty(t *testing.T) {
	const audience = "https://api.example.test/path%2Fraw?x=1&x=2"
	calls := 0
	mode := "code"
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls++
		r.ParseForm()
		form := r.PostForm
		if form.Get("resource") != "" && form.Get("resource") != audience {
			t.Error("resource repaired or changed")
		}
		if mode == "uncertain" {
			w.WriteHeader(500)
			io.WriteString(w, `{"error":"server_error"}`)
			return
		}
		scope := "read write"
		if form.Get("grant_type") == "refresh_token" {
			if form.Get("refresh_token") != "refresh" && form.Get("refresh_token") != "replacement" {
				t.Error("refresh binding lost")
			}
			if values, ok := form["scope"]; ok {
				scope = values[0]
			}
		}
		if form.Get("grant_type") == "client_credentials" {
			if form.Get("resource") != audience || form.Get("scope") == "" {
				t.Error("service grant omitted explicit resource/scope")
			}
			scope = form.Get("scope")
		}
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(map[string]any{"access_token": "opaque", "token_type": "Bearer", "expires_in": 60, "scope": scope, "refresh_token": "replacement"})
	}))
	defer server.Close()
	instance := newTSAuthTestInstance(t, Config{TSAuthBaseURL: server.URL})
	client, err := instance.NewResourceClient(TSAuthResourceClientConfig{Issuer: server.URL + "/acme", ResourceAudience: audience, Authentication: TSAuthOAuthClientAuthentication{ClientID: "origin", ClientSecret: "secret", Method: TSAuthClientSecretBasic}})
	if err != nil {
		t.Fatal(err)
	}
	grant, err := client.ExchangeAuthorizationCode(t.Context(), "code", "https://client.example.test/callback", strings.Repeat("a", 43), []string{"read", "write"}, true)
	if err != nil {
		t.Fatal(err)
	}
	before := calls
	if _, err := client.Refresh(t.Context(), grant, TSAuthResourceRefreshOptions{RefreshScopes: []string{"admin"}}); err == nil || calls != before {
		t.Fatal("scope expansion dispatched")
	}
	narrow, err := client.Refresh(t.Context(), grant, TSAuthResourceRefreshOptions{RefreshScopes: []string{"read"}, RepeatResource: true})
	if err != nil || narrow.Audience() != audience || len(narrow.GrantedScopes()) != 1 {
		t.Fatalf("narrow binding: %v", err)
	}
	if _, err := client.Refresh(t.Context(), narrow, TSAuthResourceRefreshOptions{RefreshScopes: []string{"write"}}); err == nil {
		t.Fatal("replacement expanded original narrowed scope")
	}
	empty, err := client.Refresh(t.Context(), narrow, TSAuthResourceRefreshOptions{RefreshScopes: []string{}})
	if err != nil || len(empty.GrantedScopes()) != 0 {
		t.Fatalf("explicit empty scope: %v", err)
	}
	mode = "uncertain"
	before = calls
	_, err = client.RequestClientCredentials(t.Context(), []string{"read"})
	var failure *TSAuthOIDCError
	if !errors.As(err, &failure) || !failure.Uncertain || calls != before+1 {
		t.Fatalf("uncertainty/replay: %v", err)
	}
	if _, err := client.RequestClientCredentials(t.Context(), nil); err == nil || calls != before+1 {
		t.Fatal("implicit service scope dispatched")
	}
}

func TestTSAuthResourceGrantShapesAndRequestScopeSnapshot(t *testing.T) {
	base := `{"access_token":"access","token_type":"Bearer","expires_in":60,"scope":"read"}`
	for _, test := range []struct {
		operation, body string
		valid           bool
	}{
		{"client_credentials", base, true},
		{"client_credentials", strings.TrimSuffix(base, "}") + `,"refresh_token":null}`, false},
		{"client_credentials", strings.TrimSuffix(base, "}") + `,"id_token":"id"}`, false},
		{"refresh", strings.TrimSuffix(base, "}") + `,"refresh_token":"replacement"}`, true},
		{"refresh", base, false},
		{"refresh", strings.TrimSuffix(base, "}") + `,"refresh_token":null}`, false},
		{"refresh", strings.TrimSuffix(base, "}") + `,"refresh_token":"replacement","id_token":"id"}`, false},
		{"exchange", strings.TrimSuffix(base, "}") + `,"refresh_token":null}`, true},
		{"device_poll", strings.TrimSuffix(base, "}") + `,"refresh_token":null}`, true},
		{"exchange", base, false},
	} {
		wire := tsAuthResourceTokenWire{operation: test.operation}
		if err := wire.decodeHTTPResponse(200, []byte(test.body)); (err == nil) != test.valid {
			t.Fatalf("%s grant shape: %v", test.operation, err)
		}
	}
	scopes := []string{"read"}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		r.ParseForm()
		if r.PostForm.Get("scope") != "read" {
			t.Error("request scope changed")
		}
		// This write occurs after dispatch; the SDK must hold its own set while
		// validating the response instead of reading this caller-owned slice.
		scopes[0] = "admin"
		w.Header().Set("Content-Type", "application/json")
		io.WriteString(w, base)
	}))
	defer server.Close()
	instance := newTSAuthTestInstance(t, Config{TSAuthBaseURL: server.URL})
	client, err := instance.NewResourceClient(TSAuthResourceClientConfig{Issuer: server.URL + "/acme", ResourceAudience: "https://api.example.test/a", Authentication: TSAuthOAuthClientAuthentication{ClientID: "origin", ClientSecret: "secret", Method: TSAuthClientSecretBasic}})
	if err != nil {
		t.Fatal(err)
	}
	grant, err := client.RequestClientCredentials(t.Context(), scopes)
	if err != nil || grant.GrantedScopes()[0] != "read" {
		t.Fatalf("caller slice affected dispatched grant: %v", err)
	}
}
