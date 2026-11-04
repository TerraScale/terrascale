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
	"encoding/json"
	"encoding/pem"

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

	"github.com/jackc/pgx/v5/pgproto3"
	"github.com/jackc/pgx/v5/pgtype"
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

// Uses the real pgx extended-query parser/codecs; no SQL engine or live resources.
type postgresFixture struct {
	fields           []pgproto3.FieldDescription
	rows             [][][]byte
	tag              string
	params           []uint32
	binds            chan *pgproto3.Bind
	entered, release chan struct{}
	stalled          chan error
}

func postgresFixtureClient(t *testing.T, f postgresFixture) *PostgresClient {
	t.Helper()
	wire, peer := net.Pipe()
	t.Cleanup(func() { wire.Close(); peer.Close() })
	go func() {
		defer peer.Close()
		peer.SetDeadline(time.Now().Add(5 * time.Second))
		backend := pgproto3.NewBackend(peer, peer)
		if _, err := backend.ReceiveStartupMessage(); err != nil {
			return
		}
		backend.Send(&pgproto3.AuthenticationOk{})
		backend.Send(&pgproto3.ReadyForQuery{TxStatus: 'I'})
		if backend.Flush() != nil {
			return
		}
		for {
			msg, err := backend.Receive()
			if err != nil {
				return
			}
			switch m := msg.(type) {
			case *pgproto3.Parse:
				backend.Send(&pgproto3.ParseComplete{})
			case *pgproto3.Describe:
				if m.ObjectType == 'S' {
					backend.Send(&pgproto3.ParameterDescription{ParameterOIDs: f.params})
				}
				if len(f.fields) == 0 {
					backend.Send(&pgproto3.NoData{})
				} else {
					backend.Send(&pgproto3.RowDescription{Fields: f.fields})
				}
			case *pgproto3.Bind:
				if f.binds != nil {
					f.binds <- m
				}
				backend.Send(&pgproto3.BindComplete{})
			case *pgproto3.Execute:
				if f.entered != nil {
					close(f.entered)
					<-f.release
				}
				for _, row := range f.rows {
					backend.Send(&pgproto3.DataRow{Values: row})
				}
				if f.stalled != nil {
					backend.Flush() // First row only; withhold all remaining rows and completion.
					for {
						message, err := backend.Receive()
						if err != nil {
							f.stalled <- nil
							return
						}
						switch message.(type) {
						case *pgproto3.Sync, *pgproto3.Terminate:
						default:
							f.stalled <- fmt.Errorf("unexpected replay: %T", message)
							return
						}
					}
				}
				backend.Send(&pgproto3.CommandComplete{CommandTag: []byte(f.tag)})
			case *pgproto3.Sync:
				backend.Send(&pgproto3.ReadyForQuery{TxStatus: 'I'})
				if backend.Flush() != nil {
					return
				}
			case *pgproto3.Terminate:
				return
			}
		}
	}()
	config, err := pgx.ParseConfig("host=localhost user=fixture password=fixture sslmode=disable")
	if err != nil {
		t.Fatal(err)
	}
	config.DialFunc = func(context.Context, string, string) (net.Conn, error) { return wire, nil }
	config.DefaultQueryExecMode = pgx.QueryExecModeDescribeExec
	driver, err := pgx.ConnectConfig(t.Context(), config)
	if err != nil {
		t.Fatal(err)
	}
	return &PostgresClient{driver: driver, gate: make(chan struct{}, 1)}
}
func postgresFields(oids ...uint32) []pgproto3.FieldDescription {
	fields := make([]pgproto3.FieldDescription, len(oids))
	for i, oid := range oids {
		fields[i] = pgproto3.FieldDescription{Name: []byte("value"), DataTypeOID: oid, DataTypeSize: -1, TypeModifier: -1}
	}
	return fields
}
func TestPostgresNativeScalarsRowsNullAndAlias(t *testing.T) {
	fields := postgresFields(16, 20, 21, 23, 701, 1700, 25, 1043, 114, 3802, 1082, 1114, 1184, 25, 1700, 1700, 1700)
	raw := []string{"t", "9223372036854775807", "-32768", "2147483647", "1.25", "9007199254740993.125", "text", "varchar", `{"n":9007199254740993}`, `{"n":1e9999}`, "2026-10-26", "2026-10-26 12:34:56", "2026-10-26 12:34:56+00", "1", "1e+20", "1e-7", "-1.25E+3"}
	data := make([][]byte, len(raw))
	for i, v := range raw {
		data[i] = []byte(v)
	}
	c := postgresFixtureClient(t, postgresFixture{fields: fields, rows: [][][]byte{data, make([][]byte, len(raw)), data}, tag: "SELECT 3"})
	result, err := c.Query(t.Context(), PostgresQuery{Statement: "SELECT CAST(1 AS BIGINT) AS value"})
	if err != nil || len(result.Rows) != 3 || result.CommandTag != "SELECT 3" {
		t.Fatalf("rows: %v", err)
	}
	values := make([]any, len(raw))
	for i, v := range result.Rows[0] {
		values[i] = v.Value()
		if v.OID() != fields[i].DataTypeOID || result.Rows[1][i].Value() != nil {
			t.Fatal("metadata or NULL lost")
		}
	}
	if values[0] != true || values[1] != int64(math.MaxInt64) || values[2] != int16(math.MinInt16) || values[3] != int32(math.MaxInt32) || values[4] != 1.25 || values[6] != "text" || values[7] != "varchar" {
		t.Fatal("native scalar decoding")
	}
	numeric := values[5].(pgtype.Numeric)
	if numeric.Int.String() != "9007199254740993125" || numeric.Exp != -3 {
		t.Fatal("numeric precision lost")
	}
	for i, exponent := range []int32{20, -7, 1} {
		numeric := result.Rows[0][14+i].Value().(pgtype.Numeric)
		coefficient := "1"
		if i == 2 {
			coefficient = "-125"
		}
		if numeric.Int.String() != coefficient || numeric.Exp != exponent {
			t.Fatal("scientific numeric changed")
		}
	}
	for _, i := range []int{8, 9} {
		if string(values[i].(json.RawMessage)) != raw[i] {
			t.Fatal("JSON precision lost")
		}
	}
	for _, i := range []int{10, 11, 12} {
		if _, ok := values[i].(time.Time); !ok {
			t.Fatal("native temporal decoding")
		}
	}
	if text, ok := result.Rows[0][13].Text(); !ok || text != "1" {
		t.Fatal("aliased expression TEXT must remain TEXT")
	}
	if _, ok := result.Rows[0][13].Int8(); ok {
		t.Fatal("aliased TEXT silently coerced to INT8")
	}
	if err := c.Close(t.Context()); err != nil {
		t.Fatal(err)
	}
}
