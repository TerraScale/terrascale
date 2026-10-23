package pitrbench

import (
	"fmt"
	"net"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func TestInternalCredentialAndRedirectIsolation(t *testing.T) {
	t.Parallel()
	secret := strings.Repeat("a", 64)
	var calls, redirected atomic.Int32
	target := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { redirected.Add(1) }))
	defer target.Close()
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		if r.Header.Get("X-TerraBase-Peer-Credential") != secret {
			w.WriteHeader(http.StatusUnauthorized)
			return
		}
		if r.URL.Path == "/redirect" {
			http.Redirect(w, r, target.URL, http.StatusTemporaryRedirect)
		}
	}))
	var protocols http.Protocols
	protocols.SetUnencryptedHTTP2(true)
	server.Config.Protocols = &protocols
	server.Start()
	defer server.Close()
	_, port, _ := net.SplitHostPort(server.Listener.Addr().String())
	n, _ := strconv.Atoi(port)
	c := NewClient(n, secret, "", time.Second)
	for _, path := range []string{"/readyz", "/redirect"} {
		response, err := c.Do(t.Context(), http.MethodGet, path, nil)
		want := http.StatusOK
		if path == "/redirect" {
			want = http.StatusTemporaryRedirect
		}
		if err != nil || response.Status != want {
			t.Fatalf("%s: status %d, error %v", path, response.Status, err)
		}
	}
	if redirected.Load() != 0 {
		t.Fatal("credential-bearing request followed a redirect")
	}
	for _, invalid := range []string{"", "short", strings.Repeat("g", 64), secret + "\n"} {
		c.secret = invalid
		_, err := c.Do(t.Context(), http.MethodGet, "/readyz", nil)
		if err == nil || strings.Contains(fmt.Sprint(err), secret) {
			t.Fatal("invalid credential must fail without exposing its value")
		}
	}
	if calls.Load() != 2 {
		t.Fatal("invalid credential reached the listener")
	}
}

func TestInternalClientRejectsNonAbsolutePaths(t *testing.T) {
	t.Parallel()
	c := NewClient(8081, strings.Repeat("a", 64), "", time.Second)
	transport := &recordingTransport{}
	c.hc.Transport = transport
	for _, path := range []string{"", "readyz", "@example.test:8081/readyz"} {
		if _, err := c.Do(t.Context(), http.MethodGet, path, nil); err == nil {
			t.Fatalf("expected non-absolute path %q to fail", path)
		}
	}
	if transport.calls != 0 {
		t.Fatal("non-absolute path reached the transport")
	}
}

type recordingTransport struct{ calls int }

func (t *recordingTransport) RoundTrip(*http.Request) (*http.Response, error) {
	t.calls++
	return nil, fmt.Errorf("unexpected transport call")
}
