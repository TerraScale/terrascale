package tbep

import (
	"crypto/tls"
	"crypto/x509"
	"fmt"
)

// certPool parses one PEM certificate bundle.
func certPool(bundle []byte) (*x509.CertPool, error) {
	if len(bundle) == 0 {
		return nil, fmt.Errorf("the bundle is empty")
	}
	pool := x509.NewCertPool()
	if !pool.AppendCertsFromPEM(bundle) {
		return nil, fmt.Errorf("the bundle carries no certificates")
	}
	return pool, nil
}

// tlsClientConfig builds the client TLS configuration: the engine's
// certificate is verified against the pool, and a client certificate is
// presented for mutual TLS when the key pair is supplied.
func tlsClientConfig(pool *x509.CertPool, clientCert, clientKey []byte) (*tls.Config, error) {
	config := &tls.Config{RootCAs: pool, MinVersion: tls.VersionTLS13}
	if len(clientCert) == 0 && len(clientKey) == 0 {
		return config, nil
	}
	pair, err := tls.X509KeyPair(clientCert, clientKey)
	if err != nil {
		return nil, fmt.Errorf("tbep: the client identity is invalid: %w", err)
	}
	config.Certificates = []tls.Certificate{pair}
	return config, nil
}
