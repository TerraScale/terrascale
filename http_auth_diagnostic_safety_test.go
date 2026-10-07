package sdk

import (
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"testing"
	"time"
)

type httpAuthSafetyTransport func(*http.Request) (*http.Response, error)

func (f httpAuthSafetyTransport) RoundTrip(request *http.Request) (*http.Response, error) {
	return f(request)
}

func newHTTPAuthSafetyInstance(t *testing.T, transport httpAuthSafetyTransport) *TSAuthInstanceClient {
	t.Helper()
	client, err := NewClient(Config{
		TSAuthBaseURL:    "https://auth.example.test",
		HTTPClient:       &http.Client{Transport: transport},
		MaxRetryAttempts: 3,
		RetryDelay:       time.Millisecond,
	})
	if err != nil {
		t.Fatal(err)
	}
	instance, err := client.TSAuth.Instance("acme")
	if err != nil {
		t.Fatal(err)
	}
	return instance
}

func TestHTTPAuthDiagnosticSafetyMalformedBodies(t *testing.T) {
	const secret = "CANARY_CREDENTIAL"
	for _, test := range []struct {
		name      string
		body      string
		requestID string
	}{
		{"invalid-json", `{"access_token":"` + secret + `",`, "header-request"},
		{"wrong-diagnostic-type", `{"code":123,"access_token":"` + secret + `","request_id":"body-request"}`, "body-request"},
		{"plain-text", "upstream echoed " + secret, "header-request"},
		{"wrong-nested-type", `{"error":{"code":123,"client_secret":"` + secret + `"}}`, "header-request"},
	} {
		t.Run(test.name, func(t *testing.T) {
			headers := make(http.Header)
			headers.Set("X-Request-ID", "header-request")
			apiErr := tsAuthErrorFromResponse("tsauth.oidc.userinfo", http.MethodGet, "/acme/oauth/userinfo", http.StatusUnauthorized, headers, []byte(test.body))
			if apiErr.Status != http.StatusUnauthorized || apiErr.Code != "unauthenticated" || apiErr.RequestID != test.requestID {
				t.Fatalf("diagnostic classification = %v, request ID = %q", apiErr, apiErr.RequestID)
			}
			if apiErr.Message != http.StatusText(http.StatusUnauthorized) {
				t.Fatalf("malformed response message = %q", apiErr.Message)
			}
			for _, value := range []string{apiErr.Error(), apiErr.Message, apiErr.Body, fmt.Sprintf("%#v", apiErr)} {
				if strings.Contains(value, secret) {
					t.Fatal("malformed response retained a credential")
				}
			}
		})
	}
}

func TestHTTPAuthDiagnosticSafetyAllowlistedBodies(t *testing.T) {
	const secret = "CANARY_CREDENTIAL"
	for _, test := range []struct {
		name    string
		body    string
		code    string
		message string
		want    string
	}{
		{
			name:    "management",
			body:    `{"error":{"code":"rate_limited","message":"slow down","api_key":"` + secret + `"},"access_token":"` + secret + `"}`,
			code:    "rate_limited",
			message: "slow down",
			want:    `{"error":{"code":"rate_limited","message":"slow down"}}`,
		},
		{
			name:    "oauth",
			body:    `{"error":"invalid_client","error_description":"authentication failed","client_secret":"` + secret + `"}`,
			code:    "invalid_client",
			message: "authentication failed",
			want:    `{"error":"invalid_client","error_description":"authentication failed"}`,
		},
	} {
		t.Run(test.name, func(t *testing.T) {
			apiErr := tsAuthErrorFromResponse("tsauth.oidc.token.code", http.MethodPost, "/acme/oauth/token", http.StatusTooManyRequests, nil, []byte(test.body))
			if apiErr.Code != test.code || apiErr.Message != test.message || apiErr.Body != test.want {
				t.Fatalf("allowlisted diagnostic = %v, body = %q", apiErr, apiErr.Body)
			}
			if strings.Contains(fmt.Sprintf("%#v", apiErr), secret) {
				t.Fatal("unknown response fields retained a credential")
			}
		})
	}
}

func TestHTTPAuthDiagnosticSafetyMalformedSuccess(t *testing.T) {
	const secret = "CANARY_CREDENTIAL"
	const numericSecret = "9182736450918273645091827364509182736450"
	attempts := 0
	instance := newHTTPAuthSafetyInstance(t, func(request *http.Request) (*http.Response, error) {
		attempts++
		body := `{"access_token":"` + secret + `","token_type":"Bearer","expires_in":` + numericSecret + `}`
		return &http.Response{StatusCode: http.StatusOK, Header: http.Header{"Content-Type": {"application/json"}}, Body: io.NopCloser(strings.NewReader(body)), Request: request}, nil
	})
	authentication := TSAuthOAuthClientAuthentication{ClientID: "client-1", Method: TSAuthClientAuthenticationNone}
	_, err := instance.OIDC.RequestClientCredentialsToken(t.Context(), authentication, "")
	if err == nil || attempts != 1 || !strings.Contains(err.Error(), "decode success body") {
		t.Fatalf("malformed success error = %v, attempts = %d", err, attempts)
	}
	var apiErr *APIError
	if errors.As(err, &apiErr) {
		t.Fatal("malformed success changed the plain decode-error category")
	}
	for _, value := range []string{err.Error(), fmt.Sprintf("%#v", err)} {
		if strings.Contains(value, secret) || strings.Contains(value, numericSecret) {
			t.Fatal("success decoder exposed a response credential")
		}
	}
}

func TestHTTPAuthDiagnosticSafetySpecialDecoderCause(t *testing.T) {
	const numericSecret = "9182736450918273645091827364509182736450"
	attempts := 0
	instance := newHTTPAuthSafetyInstance(t, func(request *http.Request) (*http.Response, error) {
		attempts++
		return &http.Response{StatusCode: http.StatusOK, Header: http.Header{"Content-Type": {"application/json"}}, Body: io.NopCloser(strings.NewReader(`{"keys":` + numericSecret + `}`)), Request: request}, nil
	})
	_, err := instance.OIDC.GetJwks(t.Context())
	var oidcErr *TSAuthOIDCError
	if !errors.As(err, &oidcErr) || oidcErr.Code != "invalid_jwks" || oidcErr.Operation != "jwks" || oidcErr.Uncertain || attempts != 1 {
		t.Fatalf("special decoder category = %v, attempts = %d", err, attempts)
	}
	for cause := err; cause != nil; cause = errors.Unwrap(cause) {
		if strings.Contains(cause.Error(), numericSecret) || strings.Contains(fmt.Sprintf("%#v", cause), numericSecret) {
			t.Fatal("special decoder exposed a credential in its error chain")
		}
	}
}

type httpAuthSafetyBody struct {
	reader  io.Reader
	readErr error
	closed  bool
}

func (b *httpAuthSafetyBody) Read(destination []byte) (int, error) {
	n, err := b.reader.Read(destination)
	if errors.Is(err, io.EOF) && b.readErr != nil {
		return n, b.readErr
	}
	return n, err
}

func (b *httpAuthSafetyBody) Close() error {
	b.closed = true
	return nil
}

func TestHTTPAuthDiagnosticSafetyResponseMetadata(t *testing.T) {
	const secret = "CANARY_CREDENTIAL"
	for _, test := range []struct {
		name          string
		status        int
		contentLength int64
		streamBytes   int
		readFailure   bool
		code          string
		attempts      int
		uncertain     bool
	}{
		{"advertised-size", http.StatusUnauthorized, MaxResponseBodyBytes + 1, 0, false, "response_too_large", 1, true},
		{"streamed-size", http.StatusServiceUnavailable, -1, int(MaxResponseBodyBytes) + 1, false, "response_too_large", 1, true},
		{"read-unauthorized", http.StatusUnauthorized, -1, 0, true, "unauthenticated", 1, false},
		{"read-unavailable", http.StatusServiceUnavailable, -1, 0, true, "server_error", 4, true},
		{"read-success", http.StatusOK, -1, 0, true, "response_read_failed", 4, true},
	} {
		t.Run(test.name, func(t *testing.T) {
			bodyText := secret
			if test.streamBytes > 0 {
				bodyText = strings.Repeat("x", test.streamBytes)
			}
			var bodies []*httpAuthSafetyBody
			attempts := 0
			instance := newHTTPAuthSafetyInstance(t, func(request *http.Request) (*http.Response, error) {
				attempts++
				body := &httpAuthSafetyBody{reader: strings.NewReader(bodyText)}
				if test.readFailure {
					body.readErr = errors.New("reader echoed " + secret)
				}
				bodies = append(bodies, body)
				return &http.Response{
					StatusCode:    test.status,
					ContentLength: test.contentLength,
					Header:        http.Header{"X-Request-Id": {"request-123"}, "Retry-After": {"0"}},
					Body:          body,
					Request:       request,
				}, nil
			})
			_, err := instance.OIDC.GetUserInfo(t.Context(), "access-token")
			var apiErr *APIError
			if !errors.As(err, &apiErr) {
				t.Fatalf("response error = %T, want APIError", err)
			}
			if apiErr.Status != test.status || apiErr.Code != test.code || apiErr.RequestID != "request-123" ||
				apiErr.Method != http.MethodGet || apiErr.Path != "/acme/oauth/userinfo" || apiErr.Route != "tsauth.oidc.userinfo" {
				t.Fatalf("response metadata = %#v", apiErr)
			}
			if apiErr.RetryAfter == nil || *apiErr.RetryAfter != 0 {
				t.Fatal("response lost Retry-After")
			}
			if attempts != test.attempts {
				t.Fatalf("attempts = %d, want %d", attempts, test.attempts)
			}
			for _, body := range bodies {
				if !body.closed {
					t.Fatal("response body was not closed")
				}
			}
			if apiErr.Body != "" || strings.Contains(fmt.Sprintf("%#v", apiErr), secret) || strings.Contains(apiErr.Error(), secret) {
				t.Fatal("response failure retained partial credentials or reader diagnostics")
			}
			wrapped := oidcOperationError("refresh", true, apiErr)
			var oidcErr *TSAuthOIDCError
			if !errors.As(wrapped, &oidcErr) || oidcErr.Uncertain != test.uncertain {
				t.Fatalf("OIDC response uncertainty = %v", wrapped)
			}
		})
	}
}

func TestHTTPAuthDiagnosticSafetyMutationRead(t *testing.T) {
	const secret = "CANARY_CREDENTIAL"
	attempts := 0
	discoveries := 0
	body := &httpAuthSafetyBody{reader: strings.NewReader(secret), readErr: errors.New("reader echoed " + secret)}
	instance := newHTTPAuthSafetyInstance(t, func(request *http.Request) (*http.Response, error) {
		if request.Method == http.MethodGet && request.URL.Path == "/acme/.well-known/openid-configuration" {
			discoveries++
			const discovery = `{"issuer":"https://auth.example.test/acme","authorization_endpoint":"https://auth.example.test/acme/oauth/authorize","token_endpoint":"https://auth.example.test/acme/oauth/token","userinfo_endpoint":"https://auth.example.test/acme/oauth/userinfo","jwks_uri":"https://auth.example.test/acme/.well-known/jwks.json","revocation_endpoint":"https://auth.example.test/acme/oauth/revoke","end_session_endpoint":"https://auth.example.test/acme/oauth/logout","response_types_supported":["code"],"code_challenge_methods_supported":["S256"],"id_token_signing_alg_values_supported":["ES256"],"token_endpoint_auth_methods_supported":["none"]}`
			return &http.Response{StatusCode: http.StatusOK, Header: http.Header{"Content-Type": {"application/json"}}, Body: io.NopCloser(strings.NewReader(discovery)), Request: request}, nil
		}
		if request.Method != http.MethodPost || request.URL.Path != "/acme/oauth/token" {
			t.Fatalf("unexpected refresh request %s %s", request.Method, request.URL.Path)
		}
		attempts++
		return &http.Response{
			StatusCode: http.StatusOK, ContentLength: -1,
			Header: http.Header{"X-Request-Id": {"token-request"}}, Body: body, Request: request,
		}, nil
	})
	authentication := TSAuthOAuthClientAuthentication{ClientID: "client-1", Method: TSAuthClientAuthenticationNone}
	application, err := instance.OIDC.NewApplication(TSAuthOIDCApplicationConfig{
		Issuer: "https://auth.example.test/acme", Authentication: authentication, RedirectURI: "https://app.example/callback",
	})
	if err != nil {
		t.Fatal(err)
	}
	_, err = application.Refresh(t.Context(), "refresh-token")
	var apiErr *APIError
	if !errors.As(err, &apiErr) || apiErr.Status != http.StatusOK || apiErr.Code != "response_read_failed" || apiErr.RequestID != "token-request" ||
		apiErr.Method != http.MethodPost || apiErr.Path != "/acme/oauth/token" || apiErr.Route != "tsauth.oidc.token.refresh" {
		t.Fatalf("token response classification = %v", err)
	}
	var oidcErr *TSAuthOIDCError
	if !errors.As(err, &oidcErr) || !oidcErr.Uncertain || oidcErr.Operation != "refresh" || attempts != 1 || discoveries != 1 || !body.closed {
		t.Fatalf("refresh error = %v, attempts = %d, discoveries = %d, body closed = %t", err, attempts, discoveries, body.closed)
	}
	if strings.Contains(apiErr.Error(), secret) || strings.Contains(fmt.Sprintf("%#v", apiErr), secret) {
		t.Fatal("token response retained partial credentials or reader diagnostics")
	}
}

func TestHTTPAuthDiagnosticSafetyCompleteResponses(t *testing.T) {
	for _, status := range []int{http.StatusUnauthorized, http.StatusForbidden} {
		t.Run(http.StatusText(status), func(t *testing.T) {
			attempts := 0
			client, err := NewClient(Config{
				TSAuthBaseURL:    "https://auth.example.test",
				MaxRetryAttempts: 3, RetryDelay: time.Millisecond,
				HTTPClient: &http.Client{Transport: httpAuthSafetyTransport(func(request *http.Request) (*http.Response, error) {
					attempts++
					body := `{"code":"response_read_failed"}`
					if request.URL.Path == "/acme/oauth/token" {
						body = `{"error":"response_read_failed"}`
					}
					return &http.Response{StatusCode: status, Header: http.Header{"Content-Type": {"application/json"}, "X-Request-Id": {"complete-request"}}, Body: io.NopCloser(strings.NewReader(body)), Request: request}, nil
				})},
			})
			if err != nil {
				t.Fatal(err)
			}
			instance, err := client.TSAuth.Instance("acme")
			if err != nil {
				t.Fatal(err)
			}
			for _, test := range []struct {
				name string
				call func(*testing.T) error
			}{
				{"auth", func(t *testing.T) error {
					_, err := instance.OIDC.GetUserInfo(t.Context(), "access-token")
					return err
				}},
				{"oauth", func(t *testing.T) error {
					_, err := instance.OIDC.RequestClientCredentialsToken(t.Context(), TSAuthOAuthClientAuthentication{ClientID: "client-1", Method: TSAuthClientAuthenticationNone}, "")
					return err
				}},
			} {
				t.Run(test.name, func(t *testing.T) {
					before := attempts
					err := test.call(t)
					var apiErr *APIError
					if !errors.As(err, &apiErr) || apiErr.Status != status || apiErr.Code != "response_read_failed" || apiErr.RequestID != "complete-request" || attempts-before != 1 {
						t.Fatalf("complete response = %v, attempts = %d", err, attempts-before)
					}
				})
			}
		})
	}
}

func TestHTTPAuthDiagnosticSafetySecretFormatting(t *testing.T) {
	const secret = "CANARY_CREDENTIAL"
	instance := newHTTPAuthSafetyInstance(t, func(request *http.Request) (*http.Response, error) {
		var body string
		switch request.URL.Path {
		case "/acme/oauth/token":
			body = `{"access_token":"` + secret + `","token_type":"Bearer","expires_in":3600,"refresh_token":"` + secret + `","id_token":"` + secret + `"}`
		default:
			t.Fatalf("unexpected request path %q", request.URL.Path)
		}
		return &http.Response{StatusCode: http.StatusOK, Header: http.Header{"Content-Type": {"application/json"}}, Body: io.NopCloser(strings.NewReader(body)), Request: request}, nil
	})
	authentication := TSAuthOAuthClientAuthentication{ClientID: "client-1", ClientSecret: secret, Method: TSAuthClientSecretBasic}
	tokens, err := instance.OIDC.RequestClientCredentialsToken(t.Context(), authentication, "")
	if err != nil {
		t.Fatal(err)
	}
	if tokens.AccessToken != secret {
		t.Fatal("secret-producing callers did not retain the returned credential")
	}
	for _, test := range []struct {
		name  string
		value any
	}{
		{"tokens-pointer", tokens}, {"tokens-value", *tokens},
		{"authentication", authentication},
		{"configuration", TSAuthOIDCApplicationConfig{Authentication: authentication}},
		{"attempt", TSAuthAuthorizationAttempt{State: secret, Nonce: secret, CodeVerifier: secret}},
		{"login", TSAuthOIDCLogin{Tokens: *tokens}},
	} {
		t.Run(test.name, func(t *testing.T) {
			for _, format := range []string{"%v", "%+v", "%#v", "%#+v"} {
				value := fmt.Sprintf(format, test.value)
				if strings.Contains(value, secret) || !strings.Contains(value, "[REDACTED]") {
					t.Fatalf("format %q did not redact credentials", format)
				}
			}
		})
	}
}
