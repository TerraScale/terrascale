package sdk

// Edge-case coverage for the ts-auth client, one section per matrix block:
// A decode edges, B status/error mapping, C request construction, D retry and
// cancellation, E pagination, F webhook verifier, G configuration. Cases that
// pin debatable behavior are marked "pins current behavior".

import (
	"bytes"
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"reflect"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

// tsAuthEdgeRecorder captures the exact wire form of every request.
type tsAuthEdgeRecorder struct {
	requests []string
	bodies   [][]byte
}

func (rec *tsAuthEdgeRecorder) record(r *http.Request) {
	body, _ := io.ReadAll(r.Body)
	rec.requests = append(rec.requests, r.Method+" "+r.URL.RequestURI())
	rec.bodies = append(rec.bodies, body)
	r.Body = io.NopCloser(bytes.NewReader(body))
}

func (rec *tsAuthEdgeRecorder) count(prefix string) int {
	total := 0
	for _, request := range rec.requests {
		if strings.HasPrefix(request, prefix) {
			total++
		}
	}
	return total
}

func newTSAuthEdgeInstanceFor(t *testing.T, baseURL string, cfg Config) *TSAuthInstanceClient {
	t.Helper()
	cfg.APIBaseURL = baseURL
	if cfg.AccessToken == "" {
		cfg.AccessToken = "sk_test_key"
	}
	client, err := NewClient(cfg)
	if err != nil {
		t.Fatal(err)
	}
	instance, err := client.TSAuth.Instance("acme")
	if err != nil {
		t.Fatal(err)
	}
	return instance
}

// newTSAuthEdgeInstance serves handle behind the recorder and returns an
// instance client built from cfg.
func newTSAuthEdgeInstance(t *testing.T, cfg Config, handle func(w http.ResponseWriter, r *http.Request, body []byte)) (*TSAuthInstanceClient, *tsAuthEdgeRecorder) {
	t.Helper()
	rec := &tsAuthEdgeRecorder{}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		rec.record(r)
		handle(w, r, rec.bodies[len(rec.bodies)-1])
	}))
	t.Cleanup(server.Close)
	return newTSAuthEdgeInstanceFor(t, server.URL, cfg), rec
}

type tsAuthEdgeResponse struct {
	status      int
	body        string
	contentType string // default: application/json when body is non-empty
	retryAfter  string
	wwwAuth     string
}

func (resp tsAuthEdgeResponse) write(w http.ResponseWriter) {
	if resp.body != "" || resp.contentType != "" {
		contentType := resp.contentType
		if contentType == "" {
			contentType = "application/json"
		}
		w.Header().Set("Content-Type", contentType)
	}
	if resp.retryAfter != "" {
		w.Header().Set("Retry-After", resp.retryAfter)
	}
	if resp.wwwAuth != "" {
		w.Header().Set("WWW-Authenticate", resp.wwwAuth)
	}
	w.WriteHeader(resp.status)
	_, _ = io.WriteString(w, resp.body)
}

func tsAuthEdgeJSON(w http.ResponseWriter, body string) {
	tsAuthEdgeResponse{status: http.StatusOK, body: body}.write(w)
}

func tsAuthEdgeRequireAPIError(t *testing.T, err error, route string, status int, code, message string) *APIError {
	t.Helper()
	var apiErr *APIError
	if !errors.As(err, &apiErr) {
		t.Fatalf("error = %v (%T), want *APIError", err, err)
	}
	if apiErr.Route != route || apiErr.Status != status || apiErr.Code != code || apiErr.Message != message {
		t.Fatalf("api error = %+v, want route %q status %d code %q message %q", apiErr, route, status, code, message)
	}
	return apiErr
}

// tsAuthEdgeRequireDecodeError pins the repo convention for a hostile 2xx
// body: a plain decode error (never *APIError, never a crash).
func tsAuthEdgeRequireDecodeError(t *testing.T, err error) {
	t.Helper()
	if err == nil {
		t.Fatal("decode error = nil, want decode failure")
	}
	var apiErr *APIError
	var validationErr *ValidationError
	if errors.As(err, &apiErr) || errors.As(err, &validationErr) {
		t.Fatalf("decode error = %v (%T), want plain decode error", err, err)
	}
	if !strings.Contains(err.Error(), "decode success body") && !strings.Contains(err.Error(), "non-JSON success body") {
		t.Fatalf("decode error = %v, want a success-body decode failure", err)
	}
}

func tsAuthEdgeRequireValidationError(t *testing.T, err error, field string) {
	t.Helper()
	var validationErr *ValidationError
	if !errors.As(err, &validationErr) || validationErr.Field != field {
		t.Fatalf("error = %v (%T), want *ValidationError with field %q", err, err, field)
	}
}

// Section A: response-decode edges.

func strPtr(value string) *string { return &value }

func TestTSAuthEdgeDecodeBodies(t *testing.T) {
	const unicodeName = "Ada 💜 王 é"
	instance, rec := newTSAuthEdgeInstance(t, Config{}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		switch r.Method + " " + r.URL.RequestURI() {
		case "GET /v1/acme/users/a1-missing-id":
			tsAuthEdgeJSON(w, `{"email":"ada@example.com"}`)
		case "GET /v1/acme/users/a1-null-id":
			tsAuthEdgeJSON(w, `{"id":null,"email":"ada@example.com"}`)
		case "GET /v1/acme/users/a2-null-reason":
			tsAuthEdgeJSON(w, `{"id":"user-1","ban_reason":null}`)
		case "GET /v1/acme/users/a2-absent-reason":
			tsAuthEdgeJSON(w, `{"id":"user-1"}`)
		case "GET /v1/acme/users/a3-extra-fields":
			tsAuthEdgeJSON(w, `{"id":"user-1","email":"ada@example.com","future_field":{"x":1}}`)
		case "GET /v1/acme/users/a4-wrong-type":
			tsAuthEdgeJSON(w, `{"id":"user-1","created_at":"not-a-number"}`)
		case "GET /v1/acme/users": // A5: list with no query
			tsAuthEdgeJSON(w, `{"data": [`)
		case "GET /v1/acme/users?limit=5": // A8: bare array where {data} expected
			tsAuthEdgeJSON(w, `[{"id":"user-1","email":"ada@example.com"}]`)
		case "GET /v1/acme/users?limit=9": // A9
			tsAuthEdgeJSON(w, `{"data":null}`)
		case "GET /v1/acme/users?limit=100": // A10
			users := make([]TSAuthUser, 100)
			for i := range users {
				users[i] = TSAuthUser{
					ID:             fmt.Sprintf("user-%03d", i),
					Email:          strPtr(fmt.Sprintf("user%03d@example.com", i)),
					Username:       strPtr("ada"),
					Name:           strPtr("Ada"),
					CreatedAt:      int64(1700000000 + i),
					PublicMetadata: map[string]any{"note": strings.Repeat("x", 600)},
				}
			}
			payload, err := json.Marshal(struct {
				Data []TSAuthUser `json:"data"`
			}{users})
			if err != nil {
				t.Fatal(err)
			}
			if len(payload) < 60_000 {
				t.Fatalf("large page payload = %d bytes, want a >60KB page", len(payload))
			}
			tsAuthEdgeJSON(w, string(payload))
		case "GET /v1/acme/users/a6-empty-body":
			w.WriteHeader(http.StatusOK)
		case "GET /v1/acme/users/a7-html":
			tsAuthEdgeResponse{status: http.StatusOK, body: "<html>bad gateway</html>", contentType: "text/html"}.write(w)
		case "GET /v1/acme/users/a11-unicode":
			tsAuthEdgeJSON(w, fmt.Sprintf(`{"id":"user-1","name":%q}`, unicodeName))
		case "PATCH /v1/acme/users/a11-unicode":
			tsAuthEdgeJSON(w, fmt.Sprintf(`{"id":"user-1","name":%q}`, unicodeName))
		case "GET /v1/acme/organizations": // A8: envelope where bare array expected
			tsAuthEdgeJSON(w, `{"data":[{"id":"org_1","name":"Acme","slug":"acme","created_at":1}]}`)
		default:
			http.NotFound(w, r)
		}
	})
	ctx := t.Context()

	// A1: a missing id decodes to the zero value without error.
	// pins current behavior: Go decoding has no required-field validation, so
	// the id is "" rather than a decode failure.
	user, err := instance.Users.Get(ctx, "a1-missing-id")
	if err != nil || user.ID != "" || user.Email == nil || *user.Email != "ada@example.com" {
		t.Fatalf("missing id decode = %+v, error = %v", user, err)
	}
	user, err = instance.Users.Get(ctx, "a1-null-id")
	if err != nil || user.ID != "" {
		t.Fatalf("null id decode = %+v, error = %v", user, err)
	}

	// A2: explicit null and absent optional both decode to a nil pointer.
	nullReason, err := instance.Users.Get(ctx, "a2-null-reason")
	if err != nil || nullReason.BanReason != nil {
		t.Fatalf("present-null ban_reason = %+v, error = %v", nullReason, err)
	}
	absentReason, err := instance.Users.Get(ctx, "a2-absent-reason")
	if err != nil || absentReason.BanReason != nil {
		t.Fatalf("absent ban_reason = %+v, error = %v", absentReason, err)
	}

	// A3: unknown extra fields are ignored.
	extra, err := instance.Users.Get(ctx, "a3-extra-fields")
	if err != nil || extra.Email == nil || *extra.Email != "ada@example.com" {
		t.Fatalf("extra fields decode = %+v, error = %v", extra, err)
	}

	// A4: wrong-typed field is a decode error, never a zero or crash.
	_, err = instance.Users.Get(ctx, "a4-wrong-type")
	tsAuthEdgeRequireDecodeError(t, err)

	// A5: malformed JSON body is a decode error.
	_, err = instance.Users.List(ctx, 0, "")
	tsAuthEdgeRequireDecodeError(t, err)

	// A6: empty 200 body on a JSON-expecting route is an error, not a silent
	// zero value.
	_, err = instance.Users.Get(ctx, "a6-empty-body")
	if err == nil || !strings.Contains(err.Error(), "tsauth.users.get: empty response body") {
		t.Fatalf("empty body error = %v, want the empty response body error", err)
	}

	// A7: non-JSON body (HTML) is an invalid-response error.
	_, err = instance.Users.Get(ctx, "a7-html")
	tsAuthEdgeRequireDecodeError(t, err)
	if !strings.Contains(err.Error(), "non-JSON success body") || !strings.Contains(err.Error(), "text/html") {
		t.Fatalf("html body error = %v, want the non-JSON success body error", err)
	}

	// A8: envelope mix-ups are decode errors in both directions.
	_, err = instance.Users.List(ctx, 5, "") // bare array where {data} expected
	tsAuthEdgeRequireDecodeError(t, err)
	_, err = instance.Organizations.List(ctx) // {data} where bare array expected
	tsAuthEdgeRequireDecodeError(t, err)

	// A9: null data decodes to an empty page without error. pins current
	// behavior: a null array is the nil slice (Go's "empty"), not an error.
	page, err := instance.Users.List(ctx, 9, "")
	if err != nil || page.Data != nil {
		t.Fatalf("null data decode = %+v, error = %v", page, err)
	}

	// A10: a 100-user (~72KB) page decodes with ids intact.
	page, err = instance.Users.List(ctx, 100, "")
	if err != nil || len(page.Data) != 100 || page.Data[0].ID != "user-000" || page.Data[99].ID != "user-099" {
		t.Fatalf("large page decode: len = %d, error = %v", len(page.Data), err)
	}

	// A11: unicode round-trips in both directions as exact UTF-8 bytes.
	unicodeUser, err := instance.Users.Get(ctx, "a11-unicode")
	if err != nil || unicodeUser.Name == nil || *unicodeUser.Name != unicodeName {
		t.Fatalf("unicode decode = %v, error = %v", unicodeUser.Name, err)
	}
	if _, err := instance.Users.Update(ctx, "a11-unicode", TSAuthUserPatch{}.WithName(unicodeName)); err != nil {
		t.Fatal(err)
	}
	patchIndex := -1
	for i, request := range rec.requests {
		if request == "PATCH /v1/acme/users/a11-unicode" {
			patchIndex = i
		}
	}
	if patchIndex < 0 || string(rec.bodies[patchIndex]) != `{"name":"`+unicodeName+`"}` {
		t.Fatalf("unicode patch body = %q, want the exact UTF-8 bytes of the name", rec.bodies[patchIndex])
	}
}

// Section B: status-code and error-mapping edges.

func TestTSAuthEdgeStatusMapping(t *testing.T) {
	responses := map[string]tsAuthEdgeResponse{
		"GET /v1/acme/users/u401":             {status: http.StatusUnauthorized, body: `{"error":{"code":"unauthorized","message":"Invalid API key"}}`},
		"GET /v1/acme/users/u403":             {status: http.StatusForbidden, body: `{"error":{"code":"insufficient_scope","message":"missing scope users:read"}}`},
		"GET /v1/acme/users/u404-unf":         {status: http.StatusNotFound, body: `{"error":{"code":"user_not_found","message":"user not found"}}`},
		"GET /v1/acme/users/u404-generic":     {status: http.StatusNotFound, body: `{"error":{"code":"not_found","message":"no such resource"}}`},
		"POST /v1/acme/users":                 {status: http.StatusConflict, body: `{"error":{"code":"email_taken","message":"email already in use"}}`},
		"GET /v1/acme/users/u429":             {status: http.StatusTooManyRequests, body: `{"error":{"code":"rate_limited","message":"slow down"}}`, retryAfter: "7"},
		"GET /v1/acme/users/u429-big":         {status: http.StatusTooManyRequests, body: `{"error":{"code":"rate_limited","message":"slow down"}}`, retryAfter: "120"},
		"POST /v1/acme/billing/checkout":      {status: http.StatusServiceUnavailable, body: `{"error":{"code":"temporarily_unavailable","message":"try later"}}`},
		"GET /v1/acme/billing":                {status: http.StatusServiceUnavailable, body: `{"error":{"code":"billing_not_configured","message":"billing not configured"}}`},
		"GET /v1/acme/users/u500-null-fields": {status: http.StatusInternalServerError, body: `{"error":{"code":null,"message":null}}`},
		"GET /v1/acme/users/u500-empty":       {status: http.StatusInternalServerError},
		"DELETE /v1/acme/users/u204-or-200":   {status: http.StatusOK, body: `{"id":"user-1"}`},
	}
	tokenCalls := 0
	rateLimitedCalls := 0
	rec := &tsAuthEdgeRecorder{}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		rec.record(r)
		key := r.Method + " " + r.URL.RequestURI()
		switch key {
		case "POST /acme/oauth/token":
			tokenCalls++
			if tokenCalls == 1 { // B18: flat OAuth error plus an extra header
				tsAuthEdgeResponse{status: http.StatusUnauthorized, body: `{"error":"invalid_client","error_description":"client authentication failed"}`, wwwAuth: `Basic realm="tsauth"`}.write(w)
				return
			}
			// B19: flat OAuth error with no description.
			tsAuthEdgeResponse{status: http.StatusBadRequest, body: `{"error":"invalid_grant"}`}.write(w)
		case "GET /v1/acme/users?limit=4": // B16 retried variant
			rateLimitedCalls++
			if rateLimitedCalls == 1 {
				tsAuthEdgeResponse{status: http.StatusTooManyRequests, body: `{"error":{"code":"rate_limited","message":"slow down"}}`, retryAfter: "0"}.write(w)
				return
			}
			tsAuthEdgeJSON(w, `{"data":[]}`)
		default:
			response, ok := responses[key]
			if !ok {
				t.Errorf("unexpected request %q", key)
				http.NotFound(w, r)
				return
			}
			response.write(w)
		}
	}))
	t.Cleanup(server.Close)
	instance := newTSAuthEdgeInstanceFor(t, server.URL, Config{})
	retryingInstance := newTSAuthEdgeInstanceFor(t, server.URL, Config{MaxRetryAttempts: 1, RetryDelay: time.Millisecond})
	ctx := t.Context()

	// B12: 401 envelope maps to the unauthorized code with the message kept.
	_, err := instance.Users.Get(ctx, "u401")
	tsAuthEdgeRequireAPIError(t, err, "tsauth.users.get", http.StatusUnauthorized, "unauthorized", "Invalid API key")

	// B13: 403 insufficient_scope.
	_, err = instance.Users.Get(ctx, "u403")
	tsAuthEdgeRequireAPIError(t, err, "tsauth.users.get", http.StatusForbidden, "insufficient_scope", "missing scope users:read")

	// B14: specific and generic 404 codes are both preserved.
	_, err = instance.Users.Get(ctx, "u404-unf")
	tsAuthEdgeRequireAPIError(t, err, "tsauth.users.get", http.StatusNotFound, "user_not_found", "user not found")
	_, err = instance.Users.Get(ctx, "u404-generic")
	tsAuthEdgeRequireAPIError(t, err, "tsauth.users.get", http.StatusNotFound, "not_found", "no such resource")

	// B15: 409 email_taken.
	_, err = instance.Users.Create(ctx, CreateTSAuthUserRequest{Email: "ada@example.com"})
	tsAuthEdgeRequireAPIError(t, err, "tsauth.users.create", http.StatusConflict, "email_taken", "email already in use")

	// B16: 429 with Retry-After surfaces immediately when retries are off
	// (MaxRetryAttempts=0); the raw body and the server-sent Retry-After are
	// kept on the error, uncapped.
	_, err = instance.Users.Get(ctx, "u429")
	apiErr := tsAuthEdgeRequireAPIError(t, err, "tsauth.users.get", http.StatusTooManyRequests, "rate_limited", "slow down")
	if apiErr.Body != `{"error":{"code":"rate_limited","message":"slow down"}}` {
		t.Fatalf("429 body = %q", apiErr.Body)
	}
	if apiErr.RetryAfter == nil || *apiErr.RetryAfter != 7*time.Second {
		t.Fatalf("429 retry after = %v, want 7s", apiErr.RetryAfter)
	}
	if got := rec.count("GET /v1/acme/users/u429"); got != 1 {
		t.Fatalf("429 attempts = %d, want 1 (no retries configured)", got)
	}

	// A Retry-After beyond the 30s retry cap is still surfaced uncapped on
	// the error.
	_, err = instance.Users.Get(ctx, "u429-big")
	apiErrBig, ok := err.(*APIError)
	if !ok {
		t.Fatalf("big 429 error = %T, want *APIError", err)
	}
	if apiErrBig.RetryAfter == nil || *apiErrBig.RetryAfter != 120*time.Second {
		t.Fatalf("big 429 retry after = %v, want 120s (uncapped)", apiErrBig.RetryAfter)
	}
	// ...and is retried (immediately, per Retry-After: 0) only when retries
	// are configured.
	if _, err := retryingInstance.Users.List(ctx, 4, ""); err != nil {
		t.Fatalf("429 retry error = %v", err)
	}
	if rateLimitedCalls != 2 {
		t.Fatalf("429 Retry-After: 0 attempts = %d, want 2 (retried only when configured)", rateLimitedCalls)
	}

	// B17: 503 server-side codes on checkout/billing.
	_, err = instance.Billing.CreateCheckout(ctx, "pro")
	tsAuthEdgeRequireAPIError(t, err, "tsauth.billing.checkout", http.StatusServiceUnavailable, "temporarily_unavailable", "try later")
	_, err = instance.Billing.GetOverview(ctx)
	tsAuthEdgeRequireAPIError(t, err, "tsauth.billing.get", http.StatusServiceUnavailable, "billing_not_configured", "billing not configured")

	// B18: flat OAuth error with WWW-Authenticate maps without crashing.
	_, err = instance.OIDC.ExchangeAuthorizationCode(ctx, TSAuthOAuthClientAuthentication{ClientID: "cli_1", ClientSecret: "csek", Method: TSAuthClientSecretPost}, "code_1", "https://app.example/cb", "")
	tsAuthEdgeRequireAPIError(t, err, "tsauth.oidc.token.code", http.StatusUnauthorized, "invalid_client", "client authentication failed")

	// B19: flat OAuth error without a description keeps the code; the message
	// falls back to the error code itself. pins current behavior.
	_, err = instance.OIDC.RefreshToken(ctx, TSAuthOAuthClientAuthentication{ClientID: "cli_1", ClientSecret: "csek", Method: TSAuthClientSecretPost}, "rt_1")
	tsAuthEdgeRequireAPIError(t, err, "tsauth.oidc.token.refresh", http.StatusBadRequest, "invalid_grant", "invalid_grant")

	// B20: null error fields fall back to the status-derived message.
	_, err = instance.Users.Get(ctx, "u500-null-fields")
	tsAuthEdgeRequireAPIError(t, err, "tsauth.users.get", http.StatusInternalServerError, "server_error", "Internal Server Error")

	// B21: non-JSON (empty) 500 body falls back to the status-derived error.
	_, err = instance.Users.Get(ctx, "u500-empty")
	apiErr = tsAuthEdgeRequireAPIError(t, err, "tsauth.users.get", http.StatusInternalServerError, "server_error", "Internal Server Error")
	if apiErr.Body != "" {
		t.Fatalf("empty 500 body = %q, want empty", apiErr.Body)
	}

	// B22: DELETE answering 200 with a body breaks the no-content contract:
	// ts-auth calls without a decode target must answer exactly 204.
	err = instance.Users.Delete(ctx, "u204-or-200")
	if err == nil || !strings.Contains(err.Error(), "tsauth.users.delete: expected HTTP 204, got 200") {
		t.Fatalf("delete 200-with-body error = %v, want the 204 contract error", err)
	}
	if got := rec.count("DELETE /v1/acme/users/u204-or-200"); got != 1 {
		t.Fatalf("delete attempts = %d", got)
	}
}

// Section C: request-construction edges (wire encoding).

func TestTSAuthEdgeWireEncoding(t *testing.T) {
	const cursor = "cur&a=b+c%d e#f?"
	const unicodeName = "Ada 💜 王 é"
	const formSecret = "p&ss=word+x%20 #1"
	instance, rec := newTSAuthEdgeInstance(t, Config{}, func(w http.ResponseWriter, r *http.Request, body []byte) {
		switch {
		case r.URL.Path == "/acme/oauth/token": // C33
			if string(body) != "client_id=cli_1&client_secret=p%26ss%3Dword%2Bx%2520+%231&code=code_1&grant_type=authorization_code&redirect_uri=https%3A%2F%2Fapp.example%2Fcb" {
				t.Errorf("token form body = %q", body)
			}
			if err := r.ParseForm(); err != nil || r.PostForm.Get("client_secret") != formSecret {
				t.Errorf("token form round-trip = %q, error = %v", r.PostForm.Get("client_secret"), err)
			}
			tsAuthEdgeJSON(w, `{"access_token":"at_1","token_type":"Bearer","expires_in":3600}`)
		case r.URL.Path == "/v1/acme/users" && r.Method == http.MethodGet:
			if got := r.URL.Query().Get("starting_after"); got != "" && got != cursor {
				t.Errorf("starting_after round-trip = %q", got)
			}
			tsAuthEdgeJSON(w, `{"data":[]}`)
		case r.URL.Path == "/v1/acme/users" && r.Method == http.MethodPost:
			tsAuthEdgeJSON(w, `{"id":"user-new"}`)
		case strings.HasPrefix(r.URL.Path, "/v1/acme/users/"):
			tsAuthEdgeJSON(w, `{"id":"user-1"}`)
		default:
			http.NotFound(w, r)
		}
	})
	ctx := t.Context()

	// C23: path-segment escaping. Go's url.PathEscape escapes / ? # % and
	// space but leaves & + : = @ $ raw (unlike EscapeDataString); the server
	// sees the segment exactly as pinned below. Dot ids are rejected
	// preflight, mirroring the .NET/Rust/TS SDKs.
	weirdID := "us/er+1 ?x#y%&w ü"
	if _, err := instance.Users.Get(ctx, weirdID); err != nil {
		t.Fatal(err)
	}
	for _, dotID := range []string{".", ".."} {
		_, err := instance.Users.Get(ctx, dotID)
		tsAuthEdgeRequireValidationError(t, err, "path_segment")
	}

	// C25: query values are encoded exactly once.
	if _, err := instance.Users.List(ctx, 2, cursor); err != nil {
		t.Fatal(err)
	}

	// C26: limit 1 and 100 are sent as-is; 0 is OMITTED (limit is optional);
	// 101 is still sent (the server validates). pins current behavior.
	for _, limit := range []uint32{1, 100, 0, 101} {
		if _, err := instance.Users.List(ctx, limit, ""); err != nil {
			t.Fatal(err)
		}
	}

	// C29/C30: tri-state PATCH bodies are valid JSON with exactly the
	// configured fields — every field at once, then exactly one.
	fullPatch := TSAuthUserPatch{}.
		WithName(unicodeName).
		UnsetUsername().
		WithPassword("hunter22!").
		WithEmail("ada2@example.com").
		WithBanned(true).
		WithBanReason("abuse").
		WithPublicMetadata(map[string]any{"tier": "gold"}).
		UnsetPrivateMetadata()
	if _, err := instance.Users.Update(ctx, "u1", fullPatch); err != nil {
		t.Fatal(err)
	}
	if _, err := instance.Users.Update(ctx, "u1", TSAuthUserPatch{}.WithName("Solo")); err != nil {
		t.Fatal(err)
	}

	// C31: an empty PATCH is rejected before any request is sent.
	requestsBefore := len(rec.requests)
	_, err := instance.Users.Update(ctx, "u1", TSAuthUserPatch{})
	tsAuthEdgeRequireValidationError(t, err, "patch")
	if len(rec.requests) != requestsBefore {
		t.Fatalf("empty patch issued %d requests", len(rec.requests)-requestsBefore)
	}

	// C32: create with every optional field omitted sends exactly {}.
	if _, err := instance.Users.Create(ctx, CreateTSAuthUserRequest{}); err != nil {
		t.Fatal(err)
	}

	// C34: metadata values serialize faithfully (nested objects, arrays,
	// numbers, bools, null).
	metadataPatch := TSAuthUserPatch{}.WithPublicMetadata(map[string]any{
		"count":  3,
		"nested": map[string]any{"list": []any{1, true, nil}, "ok": true},
	})
	if _, err := instance.Users.Update(ctx, "u2", metadataPatch); err != nil {
		t.Fatal(err)
	}

	// C33: form-encoded OIDC secret with reserved characters round-trips.
	if _, err := instance.OIDC.ExchangeAuthorizationCode(ctx, TSAuthOAuthClientAuthentication{ClientID: "cli_1", ClientSecret: formSecret, Method: TSAuthClientSecretPost}, "code_1", "https://app.example/cb", ""); err != nil {
		t.Fatal(err)
	}

	want := []string{
		// C23
		"GET /v1/acme/users/us%2Fer+1%20%3Fx%23y%25&w%20%C3%BC",
		// C25
		"GET /v1/acme/users?limit=2&starting_after=cur%26a%3Db%2Bc%25d+e%23f%3F",
		// C26
		"GET /v1/acme/users?limit=1",
		"GET /v1/acme/users?limit=100",
		"GET /v1/acme/users",
		"GET /v1/acme/users?limit=101",
		// C29
		"PATCH /v1/acme/users/u1",
		"PATCH /v1/acme/users/u1",
		// C32
		"POST /v1/acme/users",
		// C34
		"PATCH /v1/acme/users/u2",
		// C33
		"POST /acme/oauth/token",
	}
	if !reflect.DeepEqual(rec.requests, want) {
		t.Fatalf("requests:\n got %#v\nwant %#v", rec.requests, want)
	}
	wantBodies := []string{
		`{"ban_reason":"abuse","banned":true,"email":"ada2@example.com","name":"` + unicodeName + `","password":"hunter22!","private_metadata":null,"public_metadata":{"tier":"gold"},"username":null}`,
		`{"name":"Solo"}`,
		`{}`,
		`{"public_metadata":{"count":3,"nested":{"list":[1,true,null],"ok":true}}}`,
	}
	gotBodies := []string{
		string(rec.bodies[6]),
		string(rec.bodies[7]),
		string(rec.bodies[8]),
		string(rec.bodies[9]),
	}
	if !reflect.DeepEqual(gotBodies, wantBodies) {
		t.Fatalf("bodies:\n got %#v\nwant %#v", gotBodies, wantBodies)
	}
}

// Section C: preflight validation (no HTTP issued).

func TestTSAuthEdgePreflightValidation(t *testing.T) {
	instance, rec := newTSAuthEdgeInstance(t, Config{}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		t.Errorf("unexpected request %s %s", r.Method, r.URL.RequestURI())
		http.NotFound(w, r)
	})
	ctx := t.Context()

	// C24: blank/whitespace ids, names, and emails are rejected with the
	// field named and no HTTP issued.
	checks := []struct {
		field string
		err   error
	}{
		{"user_id", func() error { _, err := instance.Users.Get(ctx, ""); return err }()},
		{"user_id", func() error { _, err := instance.Users.Get(ctx, "  "); return err }()},
		{"name", func() error {
			_, err := instance.APIKeys.Create(ctx, CreateTSAuthAPIKeyRequest{Name: "\t"})
			return err
		}()},
		{"name", func() error {
			_, err := instance.Organizations.Create(ctx, CreateTSAuthOrganizationRequest{Name: " "})
			return err
		}()},
		{"email", func() error {
			_, err := instance.Organizations.CreateInvitation(ctx, "org_1", "  ", "dev")
			return err
		}()},
		{"plan", func() error { _, err := instance.Billing.CreateCheckout(ctx, " "); return err }()},
	}
	for _, check := range checks {
		tsAuthEdgeRequireValidationError(t, check.err, check.field)
	}
	if len(rec.requests) != 0 {
		t.Fatalf("preflight issued requests %#v", rec.requests)
	}

	// C27: instance slugs with dot segments, path separators, control
	// characters, and >255 characters are rejected at construction.
	unwired := &TSAuthClient{c: &Client{}}
	for _, slug := range []string{".", "..", "a/b", "a\\b", "has space", "\ta", strings.Repeat("a", 256)} {
		_, err := unwired.Instance(slug)
		tsAuthEdgeRequireValidationError(t, err, "instance_slug")
	}

	// C28: an API key with header-breaking bytes is rejected before any
	// request reaches the wire. pins current behavior: rejection happens at
	// request time (bearer header build), not at NewClient construction.
	headerKey, headerRec := newTSAuthEdgeInstance(t, Config{AccessToken: "sk_bad\r\nkey"}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		t.Errorf("unexpected request %s %s", r.Method, r.URL.RequestURI())
		http.NotFound(w, r)
	})
	_, err := headerKey.Users.Get(ctx, "user-1")
	if err == nil || !strings.Contains(err.Error(), "invalid control characters") {
		t.Fatalf("control-char key error = %v", err)
	}
	if len(headerRec.requests) != 0 {
		t.Fatalf("control-char key issued requests %#v", headerRec.requests)
	}
}

// Section D: retry, deadline, and cancellation edges.

type tsAuthEdgeTransport func(*http.Request) (*http.Response, error)

func (f tsAuthEdgeTransport) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

func TestTSAuthEdgeRetryAndCancellation(t *testing.T) {
	ctx := t.Context()

	// D35: GET + always-503 with MaxRetryAttempts=2 fails after exactly 3
	// attempts.
	instance, rec := newTSAuthEdgeInstance(t, Config{MaxRetryAttempts: 2, RetryDelay: time.Millisecond}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		tsAuthEdgeResponse{status: http.StatusServiceUnavailable}.write(w)
	})
	_, err := instance.Users.List(ctx, 0, "")
	tsAuthEdgeRequireAPIError(t, err, "tsauth.users.list", http.StatusServiceUnavailable, "server_error", "Service Unavailable")
	if got := rec.count("GET /v1/acme/users"); got != 3 {
		t.Fatalf("503 attempts = %d, want exactly 3", got)
	}

	// D36: Retry-After: 0 retries immediately, with no backoff sleep (the
	// configured 500ms base delay is bypassed).
	var retriedAt time.Time
	instance, rec = newTSAuthEdgeInstance(t, Config{MaxRetryAttempts: 1, RetryDelay: 5 * time.Second}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		if rec.count("GET /v1/acme/users") == 1 {
			tsAuthEdgeResponse{status: http.StatusServiceUnavailable, retryAfter: "0"}.write(w)
			return
		}
		retriedAt = time.Now()
		tsAuthEdgeJSON(w, `{"data":[]}`)
	})
	start := time.Now()
	if _, err := instance.Users.List(ctx, 0, ""); err != nil {
		t.Fatal(err)
	}
	if elapsed := retriedAt.Sub(start); elapsed > time.Second {
		t.Fatalf("Retry-After: 0 retried after %s, want no sleep", elapsed)
	}

	// D37: POST + 503 is never retried, even with retries enabled.
	instance, rec = newTSAuthEdgeInstance(t, Config{MaxRetryAttempts: 2, RetryDelay: time.Millisecond}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		tsAuthEdgeResponse{status: http.StatusServiceUnavailable, body: `{"error":{"code":"server_error","message":"unavailable"}}`}.write(w)
	})
	_, err = instance.Users.Create(ctx, CreateTSAuthUserRequest{Email: "ada@example.com"})
	tsAuthEdgeRequireAPIError(t, err, "tsauth.users.create", http.StatusServiceUnavailable, "server_error", "unavailable")
	if got := rec.count("POST /v1/acme/users"); got != 1 {
		t.Fatalf("post 503 attempts = %d, want exactly 1", got)
	}

	// D38: 400/409/401/403 are never retried, even for GET.
	responses := map[string]int{
		"/v1/acme/users/u400": http.StatusBadRequest,
		"/v1/acme/users/u409": http.StatusConflict,
		"/v1/acme/users/u401": http.StatusUnauthorized,
		"/v1/acme/users/u403": http.StatusForbidden,
	}
	instance, rec = newTSAuthEdgeInstance(t, Config{MaxRetryAttempts: 2, RetryDelay: time.Millisecond}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		status, ok := responses[r.URL.Path]
		if !ok {
			t.Errorf("unexpected request %s", r.URL.RequestURI())
			http.NotFound(w, r)
			return
		}
		tsAuthEdgeResponse{status: status, body: `{"error":{"code":"nope","message":"nope"}}`}.write(w)
	})
	for path := range responses {
		_, err := instance.Users.Get(ctx, strings.TrimPrefix(path, "/v1/acme/users/"))
		tsAuthEdgeRequireAPIError(t, err, "tsauth.users.get", responses[path], "nope", "nope")
		if got := rec.count("GET " + path); got != 1 {
			t.Fatalf("%s attempts = %d, want exactly 1", path, got)
		}
	}

	// D39: transport errors on GET retry when enabled; POST never retries.
	transportCalls := 0
	failing := tsAuthEdgeTransport(func(*http.Request) (*http.Response, error) {
		transportCalls++
		return nil, errors.New("connection reset by peer")
	})
	instance, _ = newTSAuthEdgeInstance(t, Config{
		HTTPClient:       &http.Client{Transport: failing},
		MaxRetryAttempts: 1,
		RetryDelay:       time.Millisecond,
	}, func(w http.ResponseWriter, r *http.Request, _ []byte) { http.NotFound(w, r) })
	_, err = instance.Users.List(ctx, 0, "")
	if err == nil || !strings.Contains(err.Error(), "tsauth.users.list") || !strings.Contains(err.Error(), "connection reset by peer") {
		t.Fatalf("GET transport error = %v", err)
	}
	var apiErr *APIError
	if errors.As(err, &apiErr) {
		t.Fatalf("GET transport error = %v, want a wrapped transport error", err)
	}
	if transportCalls != 2 {
		t.Fatalf("GET transport attempts = %d, want 2 (retried)", transportCalls)
	}
	transportCalls = 0
	_, err = instance.Users.Create(ctx, CreateTSAuthUserRequest{Email: "ada@example.com"})
	if err == nil || !strings.Contains(err.Error(), "connection reset by peer") {
		t.Fatalf("POST transport error = %v", err)
	}
	if transportCalls != 1 {
		t.Fatalf("POST transport attempts = %d, want exactly 1 (never retried)", transportCalls)
	}

	// D40: cancellation before the call surfaces a cancelled error with no
	// HTTP issued.
	instance, rec = newTSAuthEdgeInstance(t, Config{}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		t.Errorf("unexpected request %s %s", r.Method, r.URL.RequestURI())
		http.NotFound(w, r)
	})
	cancelledCtx, cancel := context.WithCancel(ctx)
	cancel()
	_, err = instance.Users.List(cancelledCtx, 0, "")
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("pre-cancelled error = %v, want context.Canceled", err)
	}
	if len(rec.requests) != 0 {
		t.Fatalf("pre-cancelled call issued requests %#v", rec.requests)
	}

	// D41: cancellation during the delayed retry sleep is honored. The
	// handler cancels deterministically after answering once, so the cancel
	// always lands before a second request regardless of scheduling.
	sleepCtx, cancelSleep := context.WithCancel(ctx)
	var answered atomic.Bool
	instance, rec = newTSAuthEdgeInstance(t, Config{MaxRetryAttempts: 1, RetryDelay: time.Millisecond}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		tsAuthEdgeResponse{status: http.StatusTooManyRequests, body: `{"error":{"code":"rate_limited","message":"slow down"}}`, retryAfter: "30"}.write(w)
		if answered.CompareAndSwap(false, true) {
			cancelSleep()
		}
	})
	start = time.Now()
	_, err = instance.Users.List(sleepCtx, 0, "")
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("sleep cancellation error = %v (%T), want context.Canceled", err, err)
	}
	if got := rec.count("GET /v1/acme/users"); got != 1 {
		t.Fatalf("attempts before cancel = %d, want 1", got)
	}
	if elapsed := time.Since(start); elapsed > 5*time.Second {
		t.Fatalf("cancellation took %s, want it honored during the sleep", elapsed)
	}
}

// Section E: pagination edges.

func TestTSAuthEdgePagination(t *testing.T) {
	pageFor := func(t *testing.T, pages map[string][]string, startingAfter string) string {
		t.Helper()
		ids, ok := pages[startingAfter]
		if !ok {
			t.Fatalf("unexpected starting_after %q", startingAfter)
			return ""
		}
		users := make([]string, 0, len(ids))
		for _, id := range ids {
			users = append(users, fmt.Sprintf(`{"id":%q,"email":"a@example.com"}`, id))
		}
		return `{"data":[` + strings.Join(users, ",") + `]}`
	}

	// E43: pages 2,2,empty end the walk at 4 users over 3 requests.
	pages := map[string][]string{"": {"user-1", "user-2"}, "user-2": {"user-3", "user-4"}, "user-4": {}}
	instance, rec := newTSAuthEdgeInstance(t, Config{}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		tsAuthEdgeJSON(w, pageFor(t, pages, r.URL.Query().Get("starting_after")))
	})
	users, err := instance.Users.ListAll(t.Context(), 2)
	if err != nil || len(users) != 4 || users[3].ID != "user-4" {
		t.Fatalf("list all = %d users, error = %v", len(users), err)
	}
	wantRequests := []string{
		"GET /v1/acme/users?limit=2",
		"GET /v1/acme/users?limit=2&starting_after=user-2",
		"GET /v1/acme/users?limit=2&starting_after=user-4",
	}
	if !reflect.DeepEqual(rec.requests, wantRequests) {
		t.Fatalf("requests:\n got %#v\nwant %#v", rec.requests, wantRequests)
	}

	// E44: a short page (1 row) terminates the walk immediately, without a
	// confirming empty page.
	pages = map[string][]string{"": {"user-1", "user-2"}, "user-2": {"user-3"}, "user-3": {}}
	instance, rec = newTSAuthEdgeInstance(t, Config{}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		tsAuthEdgeJSON(w, pageFor(t, pages, r.URL.Query().Get("starting_after")))
	})
	users, err = instance.Users.ListAll(t.Context(), 2)
	if err != nil || len(users) != 3 || users[2].ID != "user-3" {
		t.Fatalf("short-page walk = %d users, error = %v", len(users), err)
	}
	wantRequests = []string{
		"GET /v1/acme/users?limit=2",
		"GET /v1/acme/users?limit=2&starting_after=user-2",
	}
	if !reflect.DeepEqual(rec.requests, wantRequests) {
		t.Fatalf("requests:\n got %#v\nwant %#v", rec.requests, wantRequests)
	}

	// E45: an unknown cursor is decode-only (the server resumes from the
	// top); the walk itself never inspects cursors.
	instance, rec = newTSAuthEdgeInstance(t, Config{}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		tsAuthEdgeJSON(w, `{"data":[{"id":"user-1","email":"a@example.com"},{"id":"user-2","email":"a@example.com"}]}`)
	})
	page, err := instance.Users.List(t.Context(), 2, "unknown-cursor")
	if err != nil || len(page.Data) != 2 || page.Data[0].ID != "user-1" {
		t.Fatalf("unknown cursor page = %+v, error = %v", page, err)
	}
	if !reflect.DeepEqual(rec.requests, []string{"GET /v1/acme/users?limit=2&starting_after=unknown-cursor"}) {
		t.Fatalf("requests = %#v", rec.requests)
	}
}

// Section F: webhook verifier edges.

func TestTSAuthEdgeWebhookVerifier(t *testing.T) {
	const secret = "whsec_12345678901234567890123456789012"
	payload := []byte(`{"id":"evt_1","type":"user.created","data":{"user":{"id":"user-1"}}}`)
	now := time.Unix(1700000000, 0)

	signFor := func(key, timestamp string, payload []byte) string {
		mac := hmac.New(sha256.New, []byte(key))
		mac.Write([]byte(timestamp))
		mac.Write([]byte("."))
		mac.Write(payload)
		return "sha256=" + hex.EncodeToString(mac.Sum(nil))
	}
	valid := signFor(secret, "1700000000", payload)

	// F47: uppercase hex never matches (server emits lowercase).
	if VerifyTSAuthWebhook(secret, "1700000000", strings.ToUpper(valid), payload) {
		t.Fatal("uppercase hex signature accepted")
	}

	// F48: a signature missing the sha256= prefix never matches.
	if VerifyTSAuthWebhook(secret, "1700000000", strings.TrimPrefix(valid, "sha256="), payload) {
		t.Fatal("prefixless signature accepted")
	}

	// F49: 63 and 65 hex characters behind a correct prefix never match.
	digest := strings.TrimPrefix(valid, "sha256=")
	if VerifyTSAuthWebhook(secret, "1700000000", "sha256="+digest[:63], payload) {
		t.Fatal("63-hex signature accepted")
	}
	if VerifyTSAuthWebhook(secret, "1700000000", "sha256="+digest+"0", payload) {
		t.Fatal("65-hex signature accepted")
	}

	// F50: a non-numeric timestamp is false in BOTH variants; negative and
	// far-future timestamps pass without tolerance (the signature is
	// authoritative) and fail with tolerance.
	nonNumeric := signFor(secret, "not-a-number", payload)
	if VerifyTSAuthWebhook(secret, "not-a-number", nonNumeric, payload) {
		t.Fatal("non-numeric timestamp accepted without tolerance")
	}
	if VerifyTSAuthWebhookWithTolerance(secret, "not-a-number", nonNumeric, payload, time.Minute, now) {
		t.Fatal("non-numeric timestamp accepted with tolerance")
	}
	negative := signFor(secret, "-5", payload)
	if !VerifyTSAuthWebhook(secret, "-5", negative, payload) {
		t.Fatal("negative timestamp rejected without tolerance")
	}
	if VerifyTSAuthWebhookWithTolerance(secret, "-5", negative, payload, time.Minute, now) {
		t.Fatal("negative timestamp accepted with tolerance")
	}
	farFuture := signFor(secret, "9999999999", payload)
	if !VerifyTSAuthWebhook(secret, "9999999999", farFuture, payload) {
		t.Fatal("far-future timestamp rejected without tolerance")
	}
	if VerifyTSAuthWebhookWithTolerance(secret, "9999999999", farFuture, payload, time.Minute, now) {
		t.Fatal("far-future timestamp accepted with tolerance")
	}

	// F51: an empty payload is a signable input.
	emptySignature := signFor(secret, "1700000000", []byte{})
	if !VerifyTSAuthWebhook(secret, "1700000000", emptySignature, []byte{}) {
		t.Fatal("empty payload rejected")
	}

	// F52: an empty secret is false (a missing secret is the empty string).
	if VerifyTSAuthWebhook("", "1700000000", valid, payload) {
		t.Fatal("empty secret accepted")
	}

	// F53: exactly at ±tolerance verifies; one second past does not.
	atLimit := signFor(secret, "1699999990", payload) // now - 10s
	if !VerifyTSAuthWebhookWithTolerance(secret, "1699999990", atLimit, payload, 10*time.Second, now) {
		t.Fatal("tolerance boundary (-10s) rejected")
	}
	atLimit = signFor(secret, "1700000010", payload) // now + 10s
	if !VerifyTSAuthWebhookWithTolerance(secret, "1700000010", atLimit, payload, 10*time.Second, now) {
		t.Fatal("tolerance boundary (+10s) rejected")
	}
	pastLimit := signFor(secret, "1699999989", payload) // now - 11s
	if VerifyTSAuthWebhookWithTolerance(secret, "1699999989", pastLimit, payload, 10*time.Second, now) {
		t.Fatal("one second past tolerance (-11s) accepted")
	}
	pastLimit = signFor(secret, "1700000011", payload) // now + 11s
	if VerifyTSAuthWebhookWithTolerance(secret, "1700000011", pastLimit, payload, 10*time.Second, now) {
		t.Fatal("one second past tolerance (+11s) accepted")
	}

	// F54: multi-byte UTF-8 payload bytes verify.
	unicodePayload := []byte(`{"name":"Ada 💜 王 é"}`)
	unicodeSignature := signFor(secret, "1700000000", unicodePayload)
	if !VerifyTSAuthWebhook(secret, "1700000000", unicodeSignature, unicodePayload) {
		t.Fatal("unicode payload rejected")
	}

	// F55: two different secrets produce different signatures and never
	// cross-verify.
	other := signFor("whsec_98765432109876543210987654321098", "1700000000", payload)
	if other == valid {
		t.Fatal("different secrets produced identical signatures")
	}
	if VerifyTSAuthWebhook(secret, "1700000000", other, payload) {
		t.Fatal("cross-secret signature accepted")
	}
}

// Section G: configuration edges.

func TestTSAuthEdgeConfig(t *testing.T) {
	// G56: a base URL with a query, fragment, or userinfo is a construction
	// error; a path and an empty base (the default origin) are ALLOWED at
	// construction (the product client defaults to the product origin).
	for _, base := range []string{
		"https://api.example.com/?q=1",
		"https://api.example.com/#fragment",
		"https://user:pass@api.example.com/",
	} {
		if _, err := NewClient(Config{APIBaseURL: base}); err == nil {
			t.Fatalf("base URL %q accepted", base)
		}
	}
	if _, err := NewClient(Config{APIBaseURL: "http://127.0.0.1:1/prefix"}); err != nil {
		t.Fatalf("base URL with path rejected: %v", err)
	}
	defaultClient, err := NewClient(Config{})
	if err != nil {
		t.Fatalf("empty base URL rejected: %v", err)
	}

	// G56b: ts-auth refuses the default product origin: the deployment origin
	// must be explicit (TSAuthBaseURL, or a non-default APIBaseURL).
	_, err = defaultClient.TSAuth.Instance("acme")
	var originErr *ValidationError
	if !errors.As(err, &originErr) || originErr.Field != "ts_auth_base_url" ||
		originErr.Message != "ts-auth requires Config.TSAuthBaseURL (or an explicit non-default Config.APIBaseURL) pointing at the ts-auth deployment origin." {
		t.Fatalf("default-origin ts-auth error = %v, want the explicit-origin ValidationError", err)
	}
	explicitClient, err := NewClient(Config{APIBaseURL: DefaultAPIBaseURL, TSAuthBaseURL: "https://auth.example.com"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := explicitClient.TSAuth.Instance("acme"); err != nil {
		t.Fatalf("ts-auth with TSAuthBaseURL rejected: %v", err)
	}

	// G57: plain HTTP is allowed only for loopback (always) and .test hosts
	// (only with an explicit HTTPClient); a production http host is rejected
	// even with an explicit transport.
	if _, err := NewClient(Config{APIBaseURL: "http://localhost:9987"}); err != nil {
		t.Fatalf("http loopback rejected: %v", err)
	}
	if _, err := NewClient(Config{APIBaseURL: "http://127.0.0.1:9987"}); err != nil {
		t.Fatalf("http loopback IP rejected: %v", err)
	}
	if _, err := NewClient(Config{APIBaseURL: "http://[::1]:9987"}); err != nil {
		t.Fatalf("http IPv6 loopback rejected: %v", err)
	}
	if _, err := NewClient(Config{APIBaseURL: "http://auth.test"}); err == nil {
		t.Fatal("http .test host accepted without an explicit transport")
	}
	if _, err := NewClient(Config{APIBaseURL: "http://auth.test", HTTPClient: &http.Client{}}); err != nil {
		t.Fatalf("http .test host with explicit transport rejected: %v", err)
	}
	for _, cfg := range []Config{
		{APIBaseURL: "http://auth.example.com"},
		{APIBaseURL: "http://auth.example.com", HTTPClient: &http.Client{}},
	} {
		if _, err := NewClient(cfg); err == nil {
			t.Fatalf("plain-http production host accepted: %+v", cfg)
		}
	}

	// G58: the base URL with and without a trailing slash behaves
	// identically.
	var requests []string
	slashServer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests = append(requests, r.Method+" "+r.URL.RequestURI())
		tsAuthEdgeJSON(w, `{"id":"user-1"}`)
	}))
	defer slashServer.Close()
	for _, base := range []string{slashServer.URL, slashServer.URL + "/"} {
		instance := newTSAuthEdgeInstanceFor(t, base, Config{})
		if _, err := instance.Users.Get(t.Context(), "user-1"); err != nil {
			t.Fatal(err)
		}
	}
	if !reflect.DeepEqual(requests, []string{"GET /v1/acme/users/user-1", "GET /v1/acme/users/user-1"}) {
		t.Fatalf("trailing-slash requests = %#v", requests)
	}

	// G60: mutating the Config record after construction does not affect the
	// client (NewClient snapshots it).
	var authorizations []string
	cfgServer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		authorizations = append(authorizations, r.Header.Get("Authorization"))
		tsAuthEdgeJSON(w, `{"id":"user-1"}`)
	}))
	defer cfgServer.Close()
	cfg := Config{APIBaseURL: cfgServer.URL, AccessToken: "sk_original"}
	client, err := NewClient(cfg)
	if err != nil {
		t.Fatal(err)
	}
	cfg.AccessToken = "sk_mutated"
	cfg.APIBaseURL = "http://127.0.0.1:1/unreachable"
	snapshotInstance, err := client.TSAuth.Instance("acme")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := snapshotInstance.Users.Get(t.Context(), "user-1"); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(authorizations, []string{"Bearer sk_original"}) {
		t.Fatalf("authorizations = %#v, want the snapshot token only", authorizations)
	}
}

// G61: TSAuthBaseURL routes ts-auth traffic to the ts-auth origin, never the
// product origin.
func TestTSAuthEdgeExplicitOrigin(t *testing.T) {
	tsAuthRequests := 0
	tsAuthServer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		tsAuthRequests++
		tsAuthEdgeJSON(w, `{"id":"user-1"}`)
	}))
	defer tsAuthServer.Close()
	productServer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		t.Errorf("product origin hit: %s %s", r.Method, r.URL.RequestURI())
		http.NotFound(w, r)
	}))
	defer productServer.Close()

	client, err := NewClient(Config{APIBaseURL: productServer.URL, TSAuthBaseURL: tsAuthServer.URL, AccessToken: "sk_test_key"})
	if err != nil {
		t.Fatal(err)
	}
	instance, err := client.TSAuth.Instance("acme")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := instance.Users.Get(t.Context(), "user-1"); err != nil {
		t.Fatal(err)
	}
	if tsAuthRequests != 1 {
		t.Fatalf("ts-auth origin requests = %d, want 1", tsAuthRequests)
	}
}

// B23: an oversized body keeps its observed status as a non-retryable
// response_too_large APIError.
func TestTSAuthEdgeOversizedResponse(t *testing.T) {
	attempts := 0
	instance, _ := newTSAuthEdgeInstance(t, Config{MaxRetryAttempts: 3, RetryDelay: time.Millisecond}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		attempts++
		w.Header().Set("Content-Length", strconv.Itoa(int(MaxResponseBodyBytes)+1))
		w.WriteHeader(http.StatusInternalServerError)
	})
	_, err := instance.Users.Get(t.Context(), "user-1")
	tsAuthEdgeRequireAPIError(t, err, "tsauth.users.get", http.StatusInternalServerError, "response_too_large",
		fmt.Sprintf("response body exceeds %d bytes", MaxResponseBodyBytes))
	if attempts != 1 {
		t.Fatalf("oversized attempts = %d, want exactly 1 (never retried)", attempts)
	}
}

// A12: the dead-letter last_error decodes whatever string the service emits,
// including status-shaped text like "HTTP 500".
func TestTSAuthEdgeDeadLetterLastError(t *testing.T) {
	instance, _ := newTSAuthEdgeInstance(t, Config{}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		tsAuthEdgeJSON(w, `[{"id":"ep1:ev1","endpoint_id":"ep1","event_id":"ev1","attempts":3,"last_error":"HTTP 500","updated_at":1700000000}]`)
	})
	deliveries, err := instance.Webhooks.ListDeadLetter(t.Context())
	if err != nil || len(deliveries) != 1 || deliveries[0].LastError == nil || *deliveries[0].LastError != "HTTP 500" {
		t.Fatalf("dead letter last_error = %+v, error = %v", deliveries, err)
	}
}
