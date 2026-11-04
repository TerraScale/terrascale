package sdk

import (
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"testing"
)

type httpAuthSafetyTransport func(*http.Request) (*http.Response, error)

func (f httpAuthSafetyTransport) RoundTrip(request *http.Request) (*http.Response, error) {
	return f(request)
}

func newHTTPAuthSafetyInstance(t *testing.T, transport httpAuthSafetyTransport) *TSAuthInstanceClient {
	t.Helper()
	client, err := NewClient(Config{
		TSAuthBaseURL: "https://auth.example.test",
		HTTPClient:    &http.Client{Transport: transport},
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
