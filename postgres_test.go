package sdk

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"encoding/binary"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	"math"
	"math/big"
	"net"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
)

func postgresTestConfig(t *testing.T) PostgresConfig {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	certificate := &x509.Certificate{SerialNumber: big.NewInt(1), IsCA: true, BasicConstraintsValid: true,
		NotBefore: time.Now().Add(-time.Minute), NotAfter: time.Now().Add(time.Hour), KeyUsage: x509.KeyUsageCertSign}
	der, err := x509.CreateCertificate(rand.Reader, certificate, certificate, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	id := "01ARZ3NDEKTSV4RRFFQ69G5FAE"
	return PostgresConfig{Host: "127.0.0.1", Port: 55432, TLSServerName: "postgres.example", Database: "main",
		KeyID: id, Password: id + "_" + strings.Repeat("0", 52),
		CACertificatePEM: pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})}
}

func TestPostgresParametersKeepInt8AndHostileTextSeparate(t *testing.T) {
	payload := "quote ' ; DROP TABLE items; -- \\ Unicode α 🌍"
	query := PostgresQuery{Statement: "INSERT INTO items (id, body) VALUES ($1,$2),($3,$4)",
		Parameters: []PostgresValue{PostgresInt8(math.MinInt64), PostgresText(payload), PostgresInt8(math.MaxInt64), PostgresText("max")}}
	parameters, err := postgresParameters(query)
	if err != nil {
		t.Fatal(err)
	}
	if parameters[0] != int64(math.MinInt64) || parameters[1] != payload || parameters[2] != int64(math.MaxInt64) || strings.Contains(query.Statement, payload) {
		t.Fatal("parameter loss or interpolation")
	}
	for _, value := range []PostgresValue{{}, PostgresText("nul\x00"), PostgresText(string([]byte{0xff})), PostgresText(strings.Repeat("x", 16385))} {
		_, err := postgresParameters(PostgresQuery{Statement: "SELECT id FROM items WHERE id=$1", Parameters: []PostgresValue{value}})
		if err == nil {
			t.Fatal("invalid parameter admitted")
		}
	}
}

func TestPostgresConfigOverridesHostileLibpqEnvironment(t *testing.T) {
	t.Setenv("PGSERVICE", "")
	for key, value := range map[string]string{"PGHOST": "untrusted.invalid", "PGPORT": "bad-port", "PGDATABASE": "wrong", "PGUSER": "wrong", "PGPASSWORD": "do-not-read",
		"PGPASSFILE": "/nonexistent/do-not-read", "PGSERVICEFILE": "/nonexistent/do-not-read", "PGSSLMODE": "disable", "PGSSLCERT": "/nonexistent/do-not-read",
		"PGSSLKEY": "/nonexistent/do-not-read", "PGSSLROOTCERT": "/nonexistent/do-not-read", "PGSSLNEGOTIATION": "wrong", "PGCONNECT_TIMEOUT": "invalid",
		"PGMINPROTOCOLVERSION": "malformed", "PGMAXPROTOCOLVERSION": "malformed",
		"PGTARGETSESSIONATTRS": "read-only", "PGOPTIONS": "untrusted", "PGTZ": "untrusted"} {
		t.Setenv(key, value)
	}
	config := postgresTestConfig(t)
	driver, err := buildPostgresConfig(config)
	if err != nil {
		t.Fatal(err)
	}
	if driver.Host != config.Host || driver.Port != config.Port || driver.User != config.KeyID || driver.Database != config.Database || driver.Password != config.Password {
		t.Fatal("ambient identity or routing affected explicit config")
	}
	if driver.TLSConfig.MinVersion != tls.VersionTLS13 || driver.TLSConfig.MaxVersion != 0 || driver.TLSConfig.InsecureSkipVerify || driver.TLSConfig.ServerName != config.TLSServerName || driver.ConnectTimeout != 5*time.Second {
		t.Fatal("TLS contract weakened")
	}
	if driver.MinProtocolVersion != "3.0" || driver.MaxProtocolVersion != "3.0" {
		t.Fatal("ambient protocol versions affected explicit config")
	}
	if !slices.Equal(driver.TLSConfig.NextProtos, []string{"postgresql"}) || driver.TLSConfig.VerifyConnection == nil {
		t.Fatal("PostgreSQL ALPN policy absent")
	}
	for _, selected := range []string{"", "h2", "postgresql"} {
		err := driver.TLSConfig.VerifyConnection(tls.ConnectionState{NegotiatedProtocol: selected})
		if (err == nil) != (selected == "postgresql" || selected == "") {
			t.Fatalf("selected protocol %q: %v", selected, err)
		}
	}
	if len(driver.Fallbacks) != 0 || len(driver.RuntimeParams) != 0 || driver.ValidateConnect != nil || driver.AfterConnect != nil ||
		driver.StatementCacheCapacity != 0 || driver.DescriptionCacheCapacity != 0 || driver.DefaultQueryExecMode != pgx.QueryExecModeDescribeExec {
		t.Fatal("ambient fallback, runtime setting, or unbounded statement cache retained")
	}
	if strings.Contains(fmt.Sprintf("%v %#v", config, config), config.Password) || strings.Contains(driver.ConnString(), config.Password) {
		t.Fatal("configuration diagnostics expose a credential")
	}
}

func TestPostgresPublicDefaultsAndDatabaseSelectors(t *testing.T) {
	t.Setenv("PGSERVICE", "")
	key := "01ARZ3NDEKTSV4RRFFQ69G5FAE_" + strings.Repeat("0", 52)
	for _, database := range []string{"main", "a-b_2", "01ARZ3NDEKTSV4RRFFQ69G5FAE/main"} {
		driver, err := buildPostgresConfig(PostgresConfig{Database: database, Password: key})
		if err != nil {
			t.Fatal("public configuration rejected")
		}
		if driver.Host != "pg.terrascale.tech" || driver.Port != 5432 || driver.Database != database || driver.User != key[:26] ||
			driver.TLSConfig.ServerName != driver.Host || driver.TLSConfig.RootCAs != nil || len(driver.Fallbacks) != 0 {
			t.Fatal("public defaults, selector, identity or system trust lost")
		}
	}
	for _, database := range []string{"", "Main", "a/b", "01ARZ3NDEKTSV4RRFFQ69G5FAE/main/other", "01ARZ3NDEKTSV4RRFFQ69G5FAE%2Fmain", "main\x00", "main\n", "con", "com0", "lpt9", strings.Repeat("a", 64)} {
		if _, err := buildPostgresConfig(PostgresConfig{Database: database, Password: key}); err == nil {
			t.Fatal("malformed database selector admitted")
		}
	}
	for _, password := range []string{"", "tbk1." + key[:26] + ".secret", strings.ToLower(key), key[:len(key)-1], key + "0", key[:27] + strings.Repeat("i", 52), key + "\n"} {
		_, err := buildPostgresConfig(PostgresConfig{Database: "main", Password: password})
		if err == nil || password != "" && strings.Contains(err.Error(), password) {
			t.Fatal("malformed key admitted or exposed")
		}
	}
	for _, config := range []PostgresConfig{
		{Database: "main", Password: key, KeyID: "01ARZ3NDEKTSV4RRFFQ69G5FAF"},
		{Database: "main", Password: key, Host: "https://pg.terrascale.tech"},
		{Database: "main", Password: key, TLSServerName: "bad\nname"},
		{Database: "main", Password: key, CACertificatePEM: []byte("not a certificate")},
		{Database: "main", Password: key, CACertificatePEM: make([]byte, 65537)},
	} {
		if _, err := buildPostgresConfig(config); err == nil {
			t.Fatal("invalid identity, route or trust admitted")
		}
	}
}

func TestPostgresServiceConfigurationIsRejectedBeforeParsingOrConnecting(t *testing.T) {
	t.Setenv("PGSERVICE", "do-not-read")
	t.Setenv("PGSERVICEFILE", "/nonexistent/do-not-read")
	_, err := ConnectPostgres(t.Context(), postgresTestConfig(t))
	var failure *PostgresError
	if !errors.As(err, &failure) || failure.Kind != "configuration" || strings.Contains(err.Error(), "do-not-read") {
		t.Fatal("ambient service was not rejected with a fixed diagnostic")
	}
}

func TestPostgresFailurePreservesStateAndCancellationWithoutDriverDetails(t *testing.T) {
	secret := "do-not-report-secret-or-sql"
	err := postgresFailure(&pgconn.PgError{Code: "40003", Message: secret, Detail: secret})
	var failure *PostgresError
	if !errors.As(err, &failure) || failure.SQLState != "40003" || strings.Contains(err.Error(), secret) || errors.Unwrap(err) != nil {
		t.Fatal("driver diagnostic leaked or SQLSTATE lost")
	}
	if !errors.Is(postgresFailure(context.Canceled), context.Canceled) {
		t.Fatal("cancellation lost")
	}
}

func TestPostgresConnectHonorsCanceledContext(t *testing.T) {
	t.Setenv("PGSERVICE", "")
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	connection, err := ConnectPostgres(ctx, postgresTestConfig(t))
	if connection != nil || !errors.Is(err, context.Canceled) {
		t.Fatal("connect lost caller cancellation")
	}
}

// The real pgx SSLRequest path must verify TLS before sending startup bytes.
func TestPostgresTLSBeforeStartup(t *testing.T) {
	t.Setenv("PGSERVICE", "")
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	certificate := &x509.Certificate{
		SerialNumber: big.NewInt(2), DNSNames: []string{"postgres.example"},
		IsCA: true, BasicConstraintsValid: true,
		NotBefore: time.Now().Add(-time.Minute), NotAfter: time.Now().Add(time.Hour),
		KeyUsage:    x509.KeyUsageCertSign | x509.KeyUsageDigitalSignature,
		ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
	}
	der, err := x509.CreateCertificate(rand.Reader, certificate, certificate, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	for _, tc := range []struct {
		name, alpn, serverName string
		untrusted, refuseTLS   bool
		startup                int
	}{
		{name: "optional ALPN", startup: 1},
		{name: "postgresql ALPN", alpn: "postgresql", startup: 1},
		{name: "wrong protocol", alpn: "h2"},
		{name: "wrong hostname", serverName: "wrong.example"},
		{name: "untrusted certificate", untrusted: true},
		{name: "no plaintext fallback", refuseTLS: true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			config := postgresTestConfig(t)
			config.CACertificatePEM = pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})
			if tc.untrusted {
				config.CACertificatePEM = nil
			}
			if tc.serverName != "" {
				config.TLSServerName = tc.serverName
			}
			driver, err := buildPostgresConfig(config)
			if err != nil {
				t.Fatal(err)
			}
			client, peer := net.Pipe()
			t.Cleanup(func() { client.Close(); peer.Close() })
			driver.DialFunc = func(context.Context, string, string) (net.Conn, error) { return client, nil }
			sent := make(chan int, 1)
			go func() {
				defer peer.Close()
				peer.SetDeadline(time.Now().Add(time.Second))
				var ssl [8]byte
				if _, err := io.ReadFull(peer, ssl[:]); err != nil || binary.BigEndian.Uint32(ssl[4:]) != 80877103 {
					sent <- -1
					return
				}
				answer := byte('S')
				if tc.refuseTLS {
					answer = 'N'
				}
				if _, err := peer.Write([]byte{answer}); err != nil {
					sent <- -1
					return
				}
				if tc.refuseTLS {
					var startup [1]byte
					n, _ := peer.Read(startup[:])
					sent <- n
					return
				}
				serverConfig := &tls.Config{Certificates: []tls.Certificate{{Certificate: [][]byte{der}, PrivateKey: key}}, MinVersion: tls.VersionTLS13, MaxVersion: tls.VersionTLS13}
				if tc.alpn != "" {
					serverConfig.NextProtos = []string{tc.alpn}
				}
				secure := tls.Server(peer, serverConfig)
				if err := secure.HandshakeContext(t.Context()); err != nil {
					sent <- 0
					return
				}
				var header [4]byte
				if _, err := io.ReadFull(secure, header[:]); err != nil {
					sent <- 0
					return
				}
				length := binary.BigEndian.Uint32(header[:])
				if length < 8 || length > 10000 {
					sent <- -2
					return
				}
				startup := make([]byte, length-4)
				if _, err := io.ReadFull(secure, startup); err != nil || !bytes.Contains(startup, []byte("database\x00main\x00")) {
					sent <- -2
					return
				}
				// Request the password only after the TLS boundary has been verified.
				secure.Write([]byte{'R', 0, 0, 0, 8, 0, 0, 0, 3})
				password := make([]byte, 5+len(config.Password)+1)
				if _, err := io.ReadFull(secure, password); err != nil || password[0] != 'p' || string(password[5:]) != config.Password+"\x00" {
					sent <- -2
					return
				}
				secure.Write([]byte{'R', 0, 0, 0, 8, 0, 0, 0, 0, 'Z', 0, 0, 0, 5, 'I'})
				sent <- 1
				var terminate [5]byte
				io.ReadFull(secure, terminate[:])
			}()
			ctx, cancel := context.WithTimeout(t.Context(), time.Second)
			defer cancel()
			connection, err := pgx.ConnectConfig(ctx, driver)
			if connection != nil {
				connection.Close(ctx)
			}
			if (err == nil) != (tc.startup == 1) {
				t.Fatal("PostgreSQL TLS handshake acceptance mismatch")
			}
			if count := <-sent; count != tc.startup {
				t.Fatalf("startup bytes=%d", count)
			}
		})
	}
}
