package main

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/base64"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"

	"terrascale.tech/sdk/tsauth"
)

// The stand-in must satisfy the public SDK's own response contract checks through the MITM proxy, exactly as the
// ts CLI reaches it: device sign-in, organization and project management, token exchange.
func TestStandInThroughProxyMeetsTsauthContract(t *testing.T) {
	ctx := context.Background()
	ca, err := newAuthority()
	if err != nil {
		t.Fatal(err)
	}
	auth, err := startAuth(ca)
	if err != nil {
		t.Fatal(err)
	}
	defer auth.close()
	var apiHostSeen string
	api := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		apiHostSeen = r.Host
		io.WriteString(w, "api")
	}))
	defer api.Close()
	port := api.Listener.Addr().(*net.TCPAddr).Port
	proxy, err := startRegionProxy(ca, port, auth)
	if err != nil {
		t.Fatal(err)
	}
	defer proxy.close()

	roots := x509.NewCertPool()
	roots.AppendCertsFromPEM(ca.pem)
	proxyURL, _ := url.Parse(proxy.url())
	hc := &http.Client{Timeout: 10 * time.Second, Transport: &http.Transport{
		Proxy: http.ProxyURL(proxyURL), TLSClientConfig: &tls.Config{RootCAs: roots, MinVersion: tls.VersionTLS13}, ForceAttemptHTTP2: true}}

	device, err := tsauth.NewDevice(tsauth.DeviceConfig{HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	authorization, err := device.Start(ctx)
	if err != nil {
		t.Fatal(err)
	}
	tokens, err := device.Wait(ctx, authorization)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := device.Refresh(ctx, tokens.RefreshToken); err != nil {
		t.Fatal(err)
	}
	client, err := tsauth.New(tsauth.Config{HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	user := client.WithToken(tokens.AccessToken)
	empty, err := http.NewRequest(http.MethodGet, "https://"+authHost+managementPath+"/organizations", nil)
	if err != nil {
		t.Fatal(err)
	}
	empty.Header.Set("Authorization", "Bearer "+tokens.AccessToken)
	if res, err := hc.Do(empty); err != nil {
		t.Fatal(err)
	} else {
		raw, _ := io.ReadAll(res.Body)
		res.Body.Close()
		if !strings.Contains(string(raw), `"items":[]`) {
			t.Fatalf("an empty page must carry items [] (sdk/go rejects null): %s", raw)
		}
	}
	o, err := user.CreateOrganization(ctx, "sim")
	if err != nil {
		t.Fatal(err)
	}
	p, err := user.CreateProject(ctx, o.ID, "sim")
	if err != nil {
		t.Fatal(err)
	}
	if page, err := user.Projects(ctx, o.ID, tsauth.PageOptions{Limit: 100}); err != nil || len(page.Items) != 1 {
		t.Fatalf("projects: %v %v", page, err)
	}
	source, err := tsauth.NewExchangeSource(tsauth.ExchangeConfig{ClientID: tsauth.CLIClientID, HTTPClient: hc,
		SubjectToken: func(context.Context) (string, error) { return tokens.AccessToken, nil }, Organization: o.ID, Project: p.ID})
	if err != nil {
		t.Fatal(err)
	}
	projectToken, err := source.Token(ctx)
	if err != nil {
		t.Fatal(err)
	}
	parts := strings.Split(projectToken, ".")
	var header map[string]string
	var claims map[string]any
	h, _ := base64.RawURLEncoding.DecodeString(parts[0])
	c, _ := base64.RawURLEncoding.DecodeString(parts[1])
	if json.Unmarshal(h, &header) != nil || json.Unmarshal(c, &claims) != nil {
		t.Fatal("undecodable project token")
	}
	if header["alg"] != "ES256" || header["typ"] != "at+jwt" || header["kid"] == "" || claims["project_id"] != p.ID ||
		claims["org_id"] != o.ID || claims["iss"] != auth.issuer || claims["aud"] != platformAud {
		t.Fatalf("project token header %v claims %v", header, claims)
	}

	res, err := hc.Get("https://" + apiHost + "/readyz")
	if err != nil {
		t.Fatal(err)
	}
	body, _ := io.ReadAll(res.Body)
	res.Body.Close()
	if string(body) != "api" || apiHostSeen != apiHost {
		t.Fatalf("api proxy answered %q with host %q", body, apiHostSeen)
	}
	if res, err := hc.Get("https://example.com/"); err == nil {
		res.Body.Close()
		t.Fatal("proxy reached a host other than the TerraScale origins")
	}
}

func TestForwarderPauseBlackholesAndResumeDelivers(t *testing.T) {
	echo, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer echo.Close()
	go func() {
		for {
			c, err := echo.Accept()
			if err != nil {
				return
			}
			go func() { io.Copy(c, c); c.Close() }()
		}
	}()
	f, err := startForwarder("127.0.0.1:0", echo.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	defer f.close()
	conn, err := net.Dial("tcp", f.ln.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	buf := make([]byte, 4)
	roundTrip := func(msg string, deadline time.Duration) error {
		if _, err := conn.Write([]byte(msg)); err != nil {
			return err
		}
		conn.SetReadDeadline(time.Now().Add(deadline))
		_, err := io.ReadFull(conn, buf)
		return err
	}
	if err := roundTrip("ping", 5*time.Second); err != nil || string(buf) != "ping" {
		t.Fatalf("open forwarder: %q %v", buf, err)
	}
	f.pause()
	if err := roundTrip("held", 200*time.Millisecond); err == nil {
		t.Fatal("paused forwarder delivered data")
	}
	f.resume()
	conn.SetReadDeadline(time.Now().Add(5 * time.Second))
	if _, err := io.ReadFull(conn, buf); err != nil || string(buf) != "held" {
		t.Fatalf("resumed forwarder: %q %v", buf, err)
	}
}
