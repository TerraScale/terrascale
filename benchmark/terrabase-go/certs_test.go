package terrabase

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"math/big"
	"net"
	"net/url"
	"testing"
	"time"
)

const testGatewayPrincipal = "spiffe://terrascale.test/gateway/database-api"

// testPKI is one throwaway certificate hierarchy built with the
// standard library alone: a root CA, one server certificate for the
// loopback lane, and one client certificate. Shared by the bufconn
// mTLS test and the live-harness integration test.
type testPKI struct {
	caPEM     []byte
	serverPEM []byte
	serverKey []byte
	clientPEM []byte
	clientKey []byte
}

func newTestPKI(t testing.TB) testPKI {
	t.Helper()
	caKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatalf("the CA key could not be generated: %v", err)
	}
	caTemplate := &x509.Certificate{
		SerialNumber:          big.NewInt(1),
		Subject:               pkix.Name{CommonName: "terrabase-test-ca"},
		NotBefore:             time.Now().Add(-time.Hour),
		NotAfter:              time.Now().Add(time.Hour),
		KeyUsage:              x509.KeyUsageCertSign | x509.KeyUsageDigitalSignature,
		BasicConstraintsValid: true,
		IsCA:                  true,
	}
	caDER, err := x509.CreateCertificate(rand.Reader, caTemplate, caTemplate, caKey.Public(), caKey)
	if err != nil {
		t.Fatalf("the CA certificate could not be created: %v", err)
	}
	caCertificate, err := x509.ParseCertificate(caDER)
	if err != nil {
		t.Fatalf("the CA certificate could not be parsed: %v", err)
	}

	serverPEM, serverKey := issueCertificate(t, caCertificate, caKey, &x509.Certificate{
		SerialNumber: big.NewInt(2),
		Subject:      pkix.Name{CommonName: "localhost"},
		DNSNames:     []string{"localhost"},
		IPAddresses:  []net.IP{net.ParseIP("127.0.0.1"), net.ParseIP("::1")},
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageServiceServerAuth},
	})
	clientPEM, clientKey := issueCertificate(t, caCertificate, caKey, &x509.Certificate{
		SerialNumber: big.NewInt(3),
		Subject:      pkix.Name{CommonName: "terrabase-test-client"},
		URIs:         []*url.URL{mustTestURL(t, testGatewayPrincipal)},
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageServiceClientAuth},
	})
	return testPKI{
		caPEM:     pemEncode(t, "CERTIFICATE", caDER),
		serverPEM: serverPEM,
		serverKey: serverKey,
		clientPEM: clientPEM,
		clientKey: clientKey,
	}
}

func mustTestURL(t testing.TB, value string) *url.URL {
	t.Helper()
	parsed, err := url.Parse(value)
	if err != nil {
		t.Fatalf("the test URI could not be parsed: %v", err)
	}
	return parsed
}

func issueCertificate(t testing.TB, ca *x509.Certificate, caKey *ecdsa.PrivateKey, template *x509.Certificate) (certPEM, keyPEM []byte) {
	t.Helper()
	template.NotBefore = time.Now().Add(-time.Hour)
	template.NotAfter = time.Now().Add(time.Hour)
	template.KeyUsage = x509.KeyUsageDigitalSignature
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatalf("the leaf key could not be generated: %v", err)
	}
	der, err := x509.CreateCertificate(rand.Reader, template, ca, key.Public(), caKey)
	if err != nil {
		t.Fatalf("the leaf certificate could not be created: %v", err)
	}
	keyDER, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		t.Fatalf("the leaf key could not be marshaled: %v", err)
	}
	return pemEncode(t, "CERTIFICATE", der), pemEncode(t, "EC PRIVATE KEY", keyDER)
}

func pemEncode(t testing.TB, blockType string, der []byte) []byte {
	t.Helper()
	return pem.EncodeToMemory(&pem.Block{Type: blockType, Bytes: der})
}
