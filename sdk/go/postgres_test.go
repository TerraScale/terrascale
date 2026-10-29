package sdk

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"encoding/base64"
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
	return PostgresConfig{Host: "127.0.0.1", Port: 55432, TLSServerName: "postgres.example", Database: "01J0000000E008000000000001",
		KeyID: id, Password: "tbk1." + id + "." + base64.RawURLEncoding.EncodeToString(make([]byte, 32)),
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
	if driver.TLSConfig.MinVersion != tls.VersionTLS13 || driver.TLSConfig.MaxVersion != tls.VersionTLS13 || driver.TLSConfig.InsecureSkipVerify || driver.TLSConfig.ServerName != config.TLSServerName {
		t.Fatal("TLS contract weakened")
	}
	if !slices.Equal(driver.TLSConfig.NextProtos, []string{"postgresql"}) || driver.TLSConfig.VerifyConnection == nil {
		t.Fatal("PostgreSQL ALPN policy absent")
	}
	for _, selected := range []string{"", "h2", "postgresql"} {
		err := driver.TLSConfig.VerifyConnection(tls.ConnectionState{NegotiatedProtocol: selected})
		if (err == nil) != (selected == "postgresql") {
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

// This uses the actual pgx startup path over an in-memory TLS peer. It proves
// rejection before startup bytes, not a successful native PostgreSQL journey.
func TestPostgresALPNRejectsBeforeStartup(t *testing.T) {
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
	for _, offered := range []string{"", "h2"} {
		t.Run("server="+offered, func(t *testing.T) {
			config := postgresTestConfig(t)
			config.CACertificatePEM = pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})
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
				if _, err := peer.Write([]byte{'S'}); err != nil {
					sent <- -1
					return
				}
				serverConfig := &tls.Config{Certificates: []tls.Certificate{{Certificate: [][]byte{der}, PrivateKey: key}}, MinVersion: tls.VersionTLS13, MaxVersion: tls.VersionTLS13}
				if offered != "" {
					serverConfig.NextProtos = []string{offered}
				}
				secure := tls.Server(peer, serverConfig)
				if err := secure.HandshakeContext(t.Context()); err != nil {
					sent <- 0
					return
				}
				var startup [1]byte
				n, _ := secure.Read(startup[:])
				sent <- n
			}()
			ctx, cancel := context.WithTimeout(t.Context(), time.Second)
			defer cancel()
			connection, err := pgx.ConnectConfig(ctx, driver)
			if connection != nil {
				connection.Close(ctx)
			}
			if err == nil {
				t.Fatal("non-PostgreSQL ALPN admitted")
			}
			if count := <-sent; count != 0 {
				t.Fatalf("startup bytes=%d", count)
			}
		})
	}
}
