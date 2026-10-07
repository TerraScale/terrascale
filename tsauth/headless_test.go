package tsauth_test

// The headless client against a server that answers as ts-auth's
// Features/Headless (ts-auth c15faf6) does: client_secret_basic on every
// call, the JSON steps under {issuer}/headless/v1 and the password and
// email-code grants at {issuer}/oauth/token. ts-auth ships no release this
// host can run, so the server mirrors its documented answers.

import (
	"encoding/json"
	"errors"
	"io"
	"maps"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"terrascale.tech/sdk/tsauth"
)

const (
	clientID     = "ts-dashboard"
	clientSecret = "s3cret:with+symbols"
	userIP       = "203.0.113.7"
)

func headlessServer(t *testing.T) *httptest.Server {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		id, secret, ok := r.BasicAuth()
		id, _ = url.QueryUnescape(id)
		secret, _ = url.QueryUnescape(secret)
		if !ok || id != clientID || secret != clientSecret {
			w.WriteHeader(http.StatusUnauthorized)
			io.WriteString(w, `{"error":{"code":"invalid_client","message":"Client authentication failed."}}`)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/terrascale/oauth/token":
			r.ParseForm()
			f := r.PostForm
			switch {
			case f.Get("grant_type") == "refresh_token" && f.Get("refresh_token") == "refresh-1":
				io.WriteString(w, `{"access_token":"access-2","token_type":"Bearer","expires_in":3600,"refresh_token":"refresh-2","scope":"openid"}`)
			case f.Get("ip_address") != userIP:
				w.WriteHeader(http.StatusBadRequest)
				io.WriteString(w, `{"error":"invalid_request","error_description":"ip_address is required"}`)
			case f.Get("grant_type") == "urn:terrascale:params:oauth:grant-type:password" && f.Get("email") == "ana@example.com" && f.Get("password") == "correct horse":
				if f.Get("scope") != "openid email offline_access" {
					t.Errorf("scope %q", f.Get("scope"))
				}
				io.WriteString(w, `{"access_token":"access-1","token_type":"Bearer","expires_in":3600,"refresh_token":"refresh-1","id_token":"id-1","scope":"openid email offline_access"}`)
			case f.Get("grant_type") == "urn:terrascale:params:oauth:grant-type:email-code" && f.Get("code") == "123456" && f.Get("pending_token") == "pending-1":
				io.WriteString(w, `{"access_token":"access-new","token_type":"Bearer","expires_in":3600,"id_token":"id-new"}`)
			default:
				w.WriteHeader(http.StatusBadRequest)
				io.WriteString(w, `{"error":"invalid_grant","error_description":"The email, password or code is wrong, or the code has expired."}`)
			}
		case "/terrascale/oauth/revoke":
			r.ParseForm()
			if r.PostForm.Get("token") != "refresh-2" {
				t.Errorf("revoke form %v", r.PostForm)
			}
		default:
			step, ok := strings.CutPrefix(r.URL.Path, "/terrascale/headless/v1/")
			var body map[string]string
			if !ok || r.Method != http.MethodPost || r.Header.Get("Content-Type") != "application/json" || json.NewDecoder(r.Body).Decode(&body) != nil {
				w.WriteHeader(http.StatusBadRequest)
				io.WriteString(w, `{"error":{"code":"invalid_request","message":"The body must be a JSON object."}}`)
				return
			}
			if body["ip_address"] != userIP {
				t.Errorf("%s: ip_address %q", step, body["ip_address"])
			}
			switch {
			case body["email"] == "busy@example.com":
				w.Header().Set("Retry-After", "42")
				w.WriteHeader(http.StatusTooManyRequests)
				io.WriteString(w, `{"error":{"code":"rate_limited","message":"Too many attempts; retry later."}}`)
			case step == "sign-up" && len(body["password"]) < 8:
				w.WriteHeader(http.StatusBadRequest)
				io.WriteString(w, `{"error":{"code":"invalid_password","message":"The password must be 8 to 1024 bytes of UTF-8."}}`)
			case step == "sign-up":
				w.WriteHeader(http.StatusAccepted)
				io.WriteString(w, `{"pending_token":"pending-1"}`)
			case step == "password-reset/complete" && body["code"] != "654321":
				w.WriteHeader(http.StatusBadRequest)
				io.WriteString(w, `{"error":{"code":"invalid_code","message":"The code is wrong or has expired."}}`)
			case step == "password-reset/complete" && body["new_password"] == "a new password":
				w.WriteHeader(http.StatusNoContent)
			case step == "email-code/start" || step == "password-reset/start":
				w.WriteHeader(http.StatusAccepted)
			default:
				t.Errorf("unexpected step %s %v", step, body)
				w.WriteHeader(http.StatusNotFound)
			}
		}
	}))
	t.Cleanup(srv.Close)
	return srv
}

func newHeadless(t *testing.T, issuer, secret string) *tsauth.Headless {
	h, err := tsauth.NewHeadless(tsauth.HeadlessConfig{Issuer: issuer, ClientID: clientID, ClientSecret: secret})
	if err != nil {
		t.Fatal(err)
	}
	return h
}

func TestHeadlessSignIn(t *testing.T) {
	srv := headlessServer(t)
	h := newHeadless(t, srv.URL+"/terrascale", clientSecret)
	ctx := t.Context()
	before := time.Now()
	tokens, err := h.SignInWithPassword(ctx, "ana@example.com", "correct horse", userIP, []string{"openid", "email", "offline_access"})
	if err != nil || tokens.AccessToken != "access-1" || tokens.RefreshToken != "refresh-1" || tokens.IDToken != "id-1" ||
		tokens.Expiry.Before(before.Add(time.Hour)) || tokens.Expiry.After(time.Now().Add(time.Hour)) {
		t.Fatalf("password sign-in: %+v %v", tokens, err)
	}
	_, err = h.SignInWithPassword(ctx, "ana@example.com", "wrong", userIP, nil)
	var oauthErr *tsauth.Error
	if !errors.As(err, &oauthErr) || oauthErr.Status != http.StatusBadRequest || oauthErr.Code != "invalid_grant" {
		t.Fatalf("wrong password: %v", err)
	}
	refreshed, err := h.Refresh(ctx, tokens.RefreshToken, nil)
	if err != nil || refreshed.AccessToken != "access-2" || refreshed.RefreshToken != "refresh-2" {
		t.Fatalf("refresh: %+v %v", refreshed, err)
	}
	if err := h.Revoke(ctx, refreshed.RefreshToken); err != nil {
		t.Fatalf("revoke: %v", err)
	}
	if _, err := h.SignInWithPassword(ctx, "ana@example.com", "correct horse", "", nil); err == nil {
		t.Fatal("sign-in without the end user's IP address")
	}
}

func TestHeadlessSignUpAndReset(t *testing.T) {
	srv := headlessServer(t)
	h := newHeadless(t, srv.URL+"/terrascale", clientSecret)
	ctx := t.Context()
	pending, err := h.SignUp(ctx, "new@example.com", "a long password", userIP)
	if err != nil || pending != "pending-1" {
		t.Fatalf("sign-up: %q %v", pending, err)
	}
	if tokens, err := h.SignInWithEmailCode(ctx, "new@example.com", "123456", userIP, pending, nil); err != nil || tokens.IDToken != "id-new" {
		t.Fatalf("sign-up code: %+v %v", tokens, err)
	}
	_, err = h.SignUp(ctx, "new@example.com", "short", userIP)
	if !errors.Is(err, tsauth.ErrInvalidPassword) {
		t.Fatalf("short password: %v", err)
	}
	if err := h.StartEmailCode(ctx, "ana@example.com", userIP); err != nil {
		t.Fatalf("email code: %v", err)
	}
	if err := h.StartPasswordReset(ctx, "ana@example.com", userIP); err != nil {
		t.Fatalf("reset start: %v", err)
	}
	if err := h.CompletePasswordReset(ctx, "ana@example.com", "000000", "a new password", userIP); !errors.Is(err, tsauth.ErrInvalidCode) {
		t.Fatalf("wrong reset code: %v", err)
	}
	if err := h.CompletePasswordReset(ctx, "ana@example.com", "654321", "a new password", userIP); err != nil {
		t.Fatalf("reset complete: %v", err)
	}
	err = h.StartEmailCode(ctx, "busy@example.com", userIP)
	var apiErr *tsauth.APIError
	if !errors.Is(err, tsauth.ErrRateLimited) || !errors.As(err, &apiErr) || apiErr.RetryAfter != 42*time.Second {
		t.Fatalf("rate limit: %v", err)
	}
	if err := h.StartEmailCode(ctx, "ana@example.com", ""); err == nil {
		t.Fatal("start without the end user's IP address")
	}
}

func TestHeadlessClientAuthentication(t *testing.T) {
	srv := headlessServer(t)
	h := newHeadless(t, srv.URL+"/terrascale", "wrong secret")
	if err := h.StartEmailCode(t.Context(), "ana@example.com", userIP); !errors.Is(err, tsauth.ErrInvalidClient) {
		t.Fatalf("JSON step with a wrong secret: %v", err)
	}
	_, err := h.SignInWithPassword(t.Context(), "ana@example.com", "correct horse", userIP, nil)
	var oauthErr *tsauth.Error
	if !errors.As(err, &oauthErr) || oauthErr.Status != http.StatusUnauthorized {
		t.Fatalf("grant with a wrong secret: %v", err)
	}
	for name, cfg := range map[string]tsauth.HeadlessConfig{
		"no secret":   {ClientID: clientID},
		"no client":   {ClientSecret: clientSecret},
		"http issuer": {Issuer: "http://auth.example/terrascale", ClientID: clientID, ClientSecret: clientSecret},
	} {
		if _, err := tsauth.NewHeadless(cfg); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}
}

func TestHeadlessAllowInsecureHTTP(t *testing.T) {
	const issuer = "http://ts-auth:8080/terrascale/"
	srv := headlessServer(t)
	fixtureURL, err := url.Parse(srv.URL)
	if err != nil {
		t.Fatal(err)
	}
	var calls atomic.Int64
	h, err := tsauth.NewHeadless(tsauth.HeadlessConfig{
		Issuer: issuer, ClientID: clientID, ClientSecret: clientSecret, AllowInsecureHTTP: true,
		HTTPClient: &http.Client{Transport: roundTripFunc(func(r *http.Request) (*http.Response, error) {
			calls.Add(1)
			if r.URL.Scheme != "http" || r.URL.Host != "ts-auth:8080" {
				t.Errorf("development issuer request: %s", r.URL)
			}
			mapped := r.Clone(r.Context())
			mapped.URL.Host = fixtureURL.Host
			mapped.Host = fixtureURL.Host
			return srv.Client().Transport.RoundTrip(mapped)
		})},
	})
	if err != nil {
		t.Fatal(err)
	}
	if err := h.ResendEmailCode(t.Context(), "new@example.com", userIP, "pending-1"); err != nil {
		t.Fatalf("development resend: %v", err)
	}
	if tokens, err := h.SignInWithPassword(t.Context(), "ana@example.com", "correct horse", userIP, []string{"openid", "email", "offline_access"}); err != nil || tokens.AccessToken != "access-1" {
		t.Fatalf("development sign-in: %+v %v", tokens, err)
	}
	if calls.Load() != 2 {
		t.Errorf("development requests: %d, want 2", calls.Load())
	}
	if _, err := tsauth.NewHeadless(tsauth.HeadlessConfig{Issuer: issuer, ClientID: clientID, ClientSecret: clientSecret}); err == nil {
		t.Error("non-loopback HTTP accepted without opt-in")
	}
	if _, err := tsauth.New(tsauth.Config{Issuer: issuer}); err == nil {
		t.Error("management client accepted non-loopback HTTP")
	}
	for _, invalidIssuer := range []string{
		issuer + "?x=1", issuer + "?", "http://user:secret@ts-auth:8080/terrascale",
		issuer + "#fragment", "http://ts-auth:8080/tenant%2Fname", "ftp://ts-auth:8080/terrascale", "/terrascale",
	} {
		if _, err := tsauth.NewHeadless(tsauth.HeadlessConfig{
			Issuer: invalidIssuer, ClientID: clientID, ClientSecret: clientSecret, AllowInsecureHTTP: true,
		}); err == nil {
			t.Errorf("invalid issuer %q accepted with HTTP opt-in", invalidIssuer)
		}
	}
}

func TestHeadlessResendEmailCode(t *testing.T) {
	for _, tc := range []struct {
		name, pendingToken string
		status             int
		code               tsauth.ErrorCode
	}{
		{"sign-in", "", http.StatusAccepted, ""},
		{"sign-up", "pending-1", http.StatusAccepted, ""},
		// ts-auth prevents enumeration: invalid pending tokens get 202 without an email.
		{"invalid pending token", "invalid-token", http.StatusAccepted, ""},
		{"invalid client", "pending-1", http.StatusUnauthorized, tsauth.ErrInvalidClient},
		{"rate limited", "pending-1", http.StatusTooManyRequests, tsauth.ErrRateLimited},
	} {
		t.Run(tc.name, func(t *testing.T) {
			var calls atomic.Int64
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				calls.Add(1)
				if r.Method != http.MethodPost || r.URL.RequestURI() != "/terrascale/headless/v1/email-code/start" {
					t.Errorf("resend endpoint: %s %s", r.Method, r.URL.RequestURI())
				}
				if r.Header.Get("Content-Type") != "application/json" || r.Header.Get("Accept") != "application/json" {
					t.Errorf("resend headers: %v", r.Header)
				}
				id, secret, ok := r.BasicAuth()
				id, _ = url.QueryUnescape(id)
				secret, _ = url.QueryUnescape(secret)
				if !ok || id != clientID || secret != clientSecret {
					t.Error("resend client authentication")
				}
				var body map[string]string
				want := map[string]string{"email": "new@example.com", "ip_address": userIP}
				if tc.pendingToken != "" {
					want["pending_token"] = tc.pendingToken
				}
				if err := json.NewDecoder(r.Body).Decode(&body); err != nil || !maps.Equal(body, want) {
					t.Errorf("resend body: %v %v, want %v", body, err, want)
				}
				w.Header().Set("Content-Type", "application/json")
				if tc.code == tsauth.ErrRateLimited {
					w.Header().Set("Retry-After", "60")
				}
				w.WriteHeader(tc.status)
				if tc.code != "" {
					json.NewEncoder(w).Encode(map[string]any{"error": map[string]any{"code": tc.code, "message": "Resend rejected."}})
				}
			}))
			t.Cleanup(srv.Close)
			h := newHeadless(t, srv.URL+"/terrascale", clientSecret)
			err := h.ResendEmailCode(t.Context(), "new@example.com", userIP, tc.pendingToken)
			if tc.code == "" {
				if err != nil {
					t.Fatalf("resend: %v", err)
				}
			} else {
				var apiErr *tsauth.APIError
				if !errors.Is(err, tc.code) || !errors.As(err, &apiErr) || apiErr.Status != tc.status || apiErr.Message != "Resend rejected." {
					t.Fatalf("resend error: %v", err)
				}
				var retryAfter time.Duration
				if tc.code == tsauth.ErrRateLimited {
					retryAfter = 60 * time.Second
				}
				if apiErr.RetryAfter != retryAfter {
					t.Errorf("RetryAfter: %v, want %v", apiErr.RetryAfter, retryAfter)
				}
			}
			if err := h.ResendEmailCode(t.Context(), "new@example.com", "", tc.pendingToken); err == nil {
				t.Fatal("resend without the end user's IP address")
			}
			if calls.Load() != 1 {
				t.Errorf("resend requests: %d, want 1", calls.Load())
			}
		})
	}
}
