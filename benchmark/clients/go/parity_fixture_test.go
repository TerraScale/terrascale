package main

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/json"
	"fmt"
	"math/big"
	"net"
	"net/http"
	"net/http/httptest"
	"sync/atomic"
	"testing"
	"time"

	"terrascale.tech/sdk/v1"
)

// parityFixture serves api.terrascale.tech over HTTPS with h1 and h2. Its
// certificate is issued for that name by a test CA the client trusts; the
// client verifies it normally.
type parityFixture struct {
	server   *httptest.Server
	roots    *x509.CertPool
	requests atomic.Int64
}

func newParityFixture(t *testing.T, handler func(n int64, w http.ResponseWriter, r *http.Request)) *parityFixture {
	t.Helper()
	caKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	ca := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: "parity test CA"}, NotBefore: time.Now().Add(-time.Hour),
		NotAfter: time.Now().Add(time.Hour), IsCA: true, BasicConstraintsValid: true, KeyUsage: x509.KeyUsageCertSign}
	caDER, err := x509.CreateCertificate(rand.Reader, ca, ca, &caKey.PublicKey, caKey)
	if err != nil {
		t.Fatal(err)
	}
	if ca, err = x509.ParseCertificate(caDER); err != nil {
		t.Fatal(err)
	}
	leafKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	leaf := &x509.Certificate{SerialNumber: big.NewInt(2), DNSNames: []string{parityHost}, NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour),
		KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}}
	leafDER, err := x509.CreateCertificate(rand.Reader, leaf, ca, &leafKey.PublicKey, caKey)
	if err != nil {
		t.Fatal(err)
	}
	f := &parityFixture{roots: x509.NewCertPool()}
	f.roots.AddCert(ca)
	f.server = httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		handler(f.requests.Add(1), w, r)
	}))
	f.server.EnableHTTP2 = true
	f.server.TLS = &tls.Config{Certificates: []tls.Certificate{{Certificate: [][]byte{leafDER}, PrivateKey: leafKey}}}
	f.server.StartTLS()
	t.Cleanup(f.server.Close)
	return f
}

// transport is the shipped SDK transport, dialing the fixture for api.terrascale.tech.
func (f *parityFixture) transport() *http.Transport {
	base := sdkTransport().(*http.Transport)
	base.Proxy = nil
	base.TLSClientConfig = &tls.Config{MinVersion: tls.VersionTLS13, RootCAs: f.roots}
	base.DialContext = func(ctx context.Context, network, _ string) (net.Conn, error) {
		return (&net.Dialer{}).DialContext(ctx, network, f.server.Listener.Addr().String())
	}
	return base
}

type parityTestToken string

func (s parityTestToken) Token(ctx context.Context) (string, error) { return string(s), ctx.Err() }

func parityTestTarget(lane string) parityTarget {
	return parityTarget{Lane: lane, Region: "gru", Project: v1.NewID(), Database: "api-perf-test",
		Document: parityDocument{id: v1.NewID(), version: "tbv2-opaque", body: json.RawMessage(`{"payload":"x"}`)}}
}

// serveDocument answers the target read exactly as TerraBase's v1 point read does.
func serveDocument(target parityTarget, region string, w http.ResponseWriter, r *http.Request) {
	if r.Header.Get("X-TerraScale-Region-Pin") != target.Region || r.URL.Query().Get("id") != target.Document.id ||
		r.URL.Path != fmt.Sprintf("/v1/projects/%s/databases/%s/collections/bench/documents/by-key", target.Project, target.Database) {
		http.Error(w, `{"error":{"code":"not_found"}}`, http.StatusNotFound)
		return
	}
	w.Header().Set("X-TerraBase-Region", region)
	w.Header().Set("Content-Type", "application/json")
	_, _ = fmt.Fprintf(w, `{"id":%q,"version":%q,"state":"value","document":%s}`, target.Document.id, target.Document.version, target.Document.body)
}

// The transport rejects a protocol other than the lane's, even when status and region match.
func TestParityTransportRejectsWrongProtocol(t *testing.T) {
	t.Parallel()
	target := parityTestTarget("raw-h1")
	f := newParityFixture(t, func(_ int64, w http.ResponseWriter, r *http.Request) { serveDocument(target, "gru", w, r) })
	base := f.transport()
	defer base.CloseIdleConnections()
	observed := &parityTransport{base: base, region: "gru", protocol: "raw-h1"} // base negotiates h2
	request, err := http.NewRequest(http.MethodGet, fmt.Sprintf("%s/v1/projects/%s/databases/%s/collections/bench/documents/by-key?id=%s",
		parityOrigin, target.Project, target.Database, target.Document.id), nil)
	if err != nil {
		t.Fatal(err)
	}
	request.Header.Set("X-TerraScale-Region-Pin", "gru")
	response, err := observed.RoundTrip(request)
	if err != nil {
		t.Fatal(err)
	}
	response.Body.Close()
	if counts := observed.counts(); counts.H2 != 1 || counts.Bad != 1 {
		t.Fatalf("counts %+v", counts)
	}
	if _, err := observed.RoundTrip(httptest.NewRequest(http.MethodGet, "https://auth.terrascale.tech/v1/token", nil)); err == nil {
		t.Fatal("non-document route accepted")
	}
}
