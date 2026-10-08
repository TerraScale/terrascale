package sdk

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/base64"
	"errors"
	"net"
	"os"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
)

const postgresMaxBytes = 16 * 1024

// PostgresConfig addresses an explicitly enabled, signed PostgreSQL listener.
// Credentials and CA trust are independent from the native HTTP configuration.
type PostgresConfig struct {
	Host             string
	Port             uint16
	TLSServerName    string
	CACertificatePEM []byte
	Database         string
	KeyID            string
	Password         string
	ConnectTimeout   time.Duration
}

// String and GoString keep credentials out of ordinary configuration diagnostics.
func (PostgresConfig) String() string {
	return "terrascale PostgreSQL configuration (credentials redacted)"
}
func (config PostgresConfig) GoString() string { return config.String() }

// PostgresError retains SQLSTATE without exposing driver messages or bound data.
type PostgresError struct {
	Kind     string
	SQLState string
	cause    error
}

func (err *PostgresError) Error() string {
	if err.SQLState != "" {
		return "terrascale PostgreSQL " + err.Kind + " failure (" + err.SQLState + ")"
	}
	return "terrascale PostgreSQL " + err.Kind + " failure"
}

// Unwrap exposes only context cancellation, never a credential-bearing driver error.
func (err *PostgresError) Unwrap() error { return err.cause }

func postgresFailure(err error) error {
	if errors.Is(err, context.Canceled) {
		return &PostgresError{Kind: "canceled", cause: context.Canceled}
	}
	if errors.Is(err, context.DeadlineExceeded) {
		return &PostgresError{Kind: "canceled", cause: context.DeadlineExceeded}
	}
	var database *pgconn.PgError
	if errors.As(err, &database) {
		return &PostgresError{Kind: "database", SQLState: database.Code}
	}
	return &PostgresError{Kind: "connection"}
}

// PostgresValue is one non-null INT8 or TEXT parameter/result value.
type PostgresValue struct {
	oid     uint32
	integer int64
	text    string
}

func PostgresInt8(value int64) PostgresValue     { return PostgresValue{oid: 20, integer: value} }
func PostgresText(value string) PostgresValue    { return PostgresValue{oid: 25, text: value} }
func (value PostgresValue) Int8() (int64, bool)  { return value.integer, value.oid == 20 }
func (value PostgresValue) Text() (string, bool) { return value.text, value.oid == 25 }
func (value PostgresValue) OID() uint32          { return value.oid }

type PostgresQuery struct {
	Statement  string
	Parameters []PostgresValue
}
type PostgresColumn struct {
	Name string
	OID  uint32
}
type PostgresResult struct {
	Command      string
	AffectedRows uint64
	Columns      []PostgresColumn
	Rows         [][]PostgresValue
}

func postgresTextValid(value string) bool {
	return len(value) <= postgresMaxBytes && utf8.ValidString(value) && !strings.ContainsRune(value, '\x00')
}

func postgresParameters(query PostgresQuery) ([]any, error) {
	if strings.TrimSpace(query.Statement) == "" || !postgresTextValid(query.Statement) || len(query.Parameters) > 4 {
		return nil, &PostgresError{Kind: "query"}
	}
	values := make([]any, len(query.Parameters))
	for index, value := range query.Parameters {
		switch value.oid {
		case 20:
			values[index] = value.integer
		case 25:
			if !postgresTextValid(value.text) {
				return nil, &PostgresError{Kind: "query"}
			}
			values[index] = value.text
		default:
			return nil, &PostgresError{Kind: "query"}
		}
	}
	return values, nil
}

func postgresBase64(value string, length int) bool {
	decoded, err := base64.RawURLEncoding.DecodeString(value)
	return err == nil && len(decoded) == length && base64.RawURLEncoding.EncodeToString(decoded) == value
}

func buildPostgresConfig(config PostgresConfig) (*pgx.ConnConfig, error) {
	timeout := config.ConnectTimeout
	if timeout == 0 {
		timeout = 5 * time.Second
	}
	parts := strings.Split(config.Password, ".")
	if config.Host == "" || len(config.Host) > 253 || strings.ContainsAny(config.Host, " \t\r\n,/\x00") || config.Port == 0 ||
		config.TLSServerName == "" || strings.ContainsAny(config.TLSServerName, " \t\r\n,/\x00") || timeout <= 0 || timeout > 10*time.Second ||
		ValidateID(config.Database) != nil || ValidateID(config.KeyID) != nil || len(parts) != 3 || parts[0] != "tbk1" ||
		parts[1] != config.KeyID || !postgresBase64(parts[2], 32) || len(config.CACertificatePEM) > 64*1024 {
		return nil, &PostgresError{Kind: "configuration"}
	}
	roots := x509.NewCertPool()
	if !roots.AppendCertsFromPEM(config.CACertificatePEM) {
		return nil, &PostgresError{Kind: "configuration"}
	}
	// pgx has no environment-free config constructor. Refuse service selection
	// before parsing; explicit nonempty stub passwords also prevent pgpass reads.
	if os.Getenv("PGSERVICE") != "" {
		return nil, &PostgresError{Kind: "configuration"}
	}
	const stub = "host=localhost port=1 dbname=unused user=unused password=unused sslmode=disable sslrootcert='' sslcert='' sslkey='' sslpassword='' sslsni=1 sslnegotiation=postgres connect_timeout=5 target_session_attrs=any application_name='' options='' timezone='' passfile=''"
	driver, err := pgx.ParseConfig(stub)
	if err != nil {
		return nil, &PostgresError{Kind: "configuration"}
	}
	driver.Host, driver.Port, driver.Database, driver.User, driver.Password = config.Host, config.Port, config.Database, config.KeyID, config.Password
	driver.ConnectTimeout = timeout
	driver.DialFunc = (&net.Dialer{Timeout: timeout}).DialContext
	driver.TLSConfig = &tls.Config{
		RootCAs: roots, ServerName: config.TLSServerName,
		MinVersion: tls.VersionTLS13, MaxVersion: tls.VersionTLS13,
		NextProtos: []string{"postgresql"},
		VerifyConnection: func(state tls.ConnectionState) error {
			if state.NegotiatedProtocol != "postgresql" {
				return &PostgresError{Kind: "tls_protocol"}
			}
			return nil
		},
	}
	driver.Fallbacks, driver.RuntimeParams, driver.ValidateConnect, driver.AfterConnect = nil, map[string]string{}, nil, nil
	driver.StatementCacheCapacity, driver.DescriptionCacheCapacity = 0, 0
	driver.DefaultQueryExecMode = pgx.QueryExecModeDescribeExec
	return driver, nil
}

// PostgresClient serializes one mature-driver connection without reconnect or write replay.
type PostgresClient struct {
	mutex  sync.Mutex
	driver *pgx.Conn
}

func ConnectPostgres(ctx context.Context, config PostgresConfig) (*PostgresClient, error) {
	driver, err := buildPostgresConfig(config)
	if err != nil {
		return nil, err
	}
	ctx, cancel := context.WithTimeout(ctx, driver.ConnectTimeout)
	defer cancel()
	connection, err := pgx.ConnectConfig(ctx, driver)
	if err != nil {
		return nil, postgresFailure(err)
	}
	return &PostgresClient{driver: connection}, nil
}

// Query sends the SQL and typed values separately through PostgreSQL binding.
// BEGIN, COMMIT and ROLLBACK remain distinct statements; callbacks are never retried.
func (client *PostgresClient) Query(ctx context.Context, query PostgresQuery) (PostgresResult, error) {
	values, err := postgresParameters(query)
	if err != nil {
		return PostgresResult{}, err
	}
	client.mutex.Lock()
	defer client.mutex.Unlock()
	if client.driver == nil || client.driver.IsClosed() {
		return PostgresResult{}, &PostgresError{Kind: "connection"}
	}
	rows, err := client.driver.Query(ctx, query.Statement, values...)
	if err != nil {
		return PostgresResult{}, postgresFailure(err)
	}
	defer rows.Close()
	result := PostgresResult{Columns: make([]PostgresColumn, len(rows.FieldDescriptions())), Rows: [][]PostgresValue{}}
	for index, field := range rows.FieldDescriptions() {
		if field.DataTypeOID != 20 && field.DataTypeOID != 25 {
			return PostgresResult{}, &PostgresError{Kind: "protocol"}
		}
		result.Columns[index] = PostgresColumn{Name: field.Name, OID: field.DataTypeOID}
	}
	for rows.Next() {
		if len(result.Rows) == 1 {
			return PostgresResult{}, &PostgresError{Kind: "protocol"}
		}
		values, err := rows.Values()
		if err != nil {
			return PostgresResult{}, postgresFailure(err)
		}
		if len(values) != len(result.Columns) {
			return PostgresResult{}, &PostgresError{Kind: "protocol"}
		}
		row := make([]PostgresValue, len(values))
		for index, value := range values {
			switch result.Columns[index].OID {
			case 20:
				integer, ok := value.(int64)
				if !ok {
					return PostgresResult{}, &PostgresError{Kind: "protocol"}
				}
				row[index] = PostgresInt8(integer)
			case 25:
				text, ok := value.(string)
				if !ok || !postgresTextValid(text) {
					return PostgresResult{}, &PostgresError{Kind: "protocol"}
				}
				row[index] = PostgresText(text)
			}
		}
		result.Rows = append(result.Rows, row)
	}
	if err := rows.Err(); err != nil {
		return PostgresResult{}, postgresFailure(err)
	}
	tag := rows.CommandTag()
	result.Command, _, _ = strings.Cut(tag.String(), " ")
	count := tag.RowsAffected()
	if count < 0 || count > 2 {
		return PostgresResult{}, &PostgresError{Kind: "protocol"}
	}
	result.AffectedRows = uint64(count)
	switch result.Command {
	case "SELECT", "INSERT", "UPDATE", "DELETE", "BEGIN", "COMMIT", "ROLLBACK", "SET":
		return result, nil
	default:
		return PostgresResult{}, &PostgresError{Kind: "protocol"}
	}
}

func (client *PostgresClient) Close(ctx context.Context) error {
	client.mutex.Lock()
	defer client.mutex.Unlock()
	if client.driver == nil {
		return nil
	}
	err := client.driver.Close(ctx)
	client.driver = nil
	if err != nil {
		return postgresFailure(err)
	}
	return nil
}
