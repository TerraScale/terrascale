package tsauth

import (
	"cmp"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"testing"
)

func newDeviceServer(t *testing.T, verification string, token func(w http.ResponseWriter, form url.Values)) (*Device, *httptest.Server) {
	var srv *httptest.Server
	srv = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		form, _ := url.ParseQuery(string(body))
		if form.Get("client_id") != CLIClientID || r.Header.Get("Authorization") != "" {
			t.Errorf("%s: public client form %v", r.URL.Path, form)
		}
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/terrascale/oauth/device":
			if form.Get("scope") != "openid email offline_access" {
				t.Errorf("device scope %q", form.Get("scope"))
			}
			fmt.Fprintf(w, `{"device_code":"dc","user_code":"1234-5678","verification_uri":%q,"expires_in":600,"interval":1}`, cmp.Or(verification, srv.URL+"/ui/terrascale/device"))
		case "/terrascale/oauth/token":
			token(w, form)
		default:
			t.Errorf("path %s", r.URL.Path)
		}
	}))
	t.Cleanup(srv.Close)
	d, err := NewDevice(DeviceConfig{Issuer: srv.URL + "/terrascale"})
	if err != nil {
		t.Fatal(err)
	}
	return d, srv
}

func TestDeviceWaitsForApprovalThenRefreshes(t *testing.T) {
	polls := 0
	d, _ := newDeviceServer(t, "", func(w http.ResponseWriter, form url.Values) {
		switch form.Get("grant_type") {
		case deviceGrant:
			if form.Get("device_code") != "dc" {
				t.Errorf("device_code %q", form.Get("device_code"))
			}
			if polls++; polls == 1 {
				w.WriteHeader(http.StatusBadRequest)
				fmt.Fprint(w, `{"error":"authorization_pending"}`)
				return
			}
			fmt.Fprint(w, `{"access_token":"at1","token_type":"Bearer","expires_in":3600,"refresh_token":"rt1"}`)
		case "refresh_token":
			if form.Get("refresh_token") != "rt1" {
				t.Errorf("refresh_token %q", form.Get("refresh_token"))
			}
			fmt.Fprint(w, `{"access_token":"at2","token_type":"Bearer","expires_in":3600,"refresh_token":"rt2"}`)
		}
	})
	a, err := d.Start(t.Context())
	if err != nil {
		t.Fatal(err)
	}
	if a.VerificationURIComplete != a.VerificationURI || a.UserCode != "1234-5678" {
		t.Fatalf("authorization %+v", a)
	}
	tokens, err := d.Wait(t.Context(), a)
	if err != nil || tokens.AccessToken != "at1" || tokens.RefreshToken != "rt1" || polls != 2 {
		t.Fatalf("Wait = %+v, %v after %d polls", tokens, err, polls)
	}
	tokens, err = d.Refresh(t.Context(), "rt1")
	if err != nil || tokens.AccessToken != "at2" || tokens.RefreshToken != "rt2" {
		t.Fatalf("Refresh = %+v, %v", tokens, err)
	}
}

func TestDeviceDenialAndForeignVerificationPage(t *testing.T) {
	d, _ := newDeviceServer(t, "", func(w http.ResponseWriter, _ url.Values) {
		w.WriteHeader(http.StatusBadRequest)
		fmt.Fprint(w, `{"error":"access_denied"}`)
	})
	a, err := d.Start(t.Context())
	if err != nil {
		t.Fatal(err)
	}
	var e *Error
	if _, err := d.Wait(t.Context(), a); !errors.As(err, &e) || e.Code != "access_denied" {
		t.Fatalf("Wait = %v, want access_denied", err)
	}
	d, _ = newDeviceServer(t, "https://evil.example/device", nil)
	if _, err := d.Start(t.Context()); err == nil {
		t.Fatal("Start accepted a verification page off the issuer")
	}
}
