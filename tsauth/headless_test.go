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
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
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

func TestHeadlessClientAuthentication(t *testing.T) {
	srv := headlessServer(t)
	h := newHeadless(t, srv.URL+"/terrascale", "wrong secret")
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

func TestHeadlessRequiresSecureIssuerOutsideLoopback(t *testing.T) {
	for _, issuer := range []string{"http://ts-auth:8080/terrascale", "http://auth.example.test/terrascale"} {
		if _, err := tsauth.NewHeadless(tsauth.HeadlessConfig{Issuer: issuer, ClientID: clientID, ClientSecret: clientSecret}); err == nil {
			t.Fatal("non-loopback HTTP issuer accepted")
		}
	}
	if _, err := tsauth.NewHeadless(tsauth.HeadlessConfig{Issuer: "http://127.0.0.1:8080/terrascale", ClientID: clientID, ClientSecret: clientSecret}); err != nil {
		t.Fatal(err)
	}
}
