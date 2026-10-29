package sdk

import (
	"bytes"
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"errors"
	"net"
	"os"
	"regexp"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgtype"
)

const (
	postgresMaxBytes       = 1024*1024 - 64 // reserve producer message framing
	postgresResultBytes    = 16 * 1024 * 1024
	postgresConnectTimeout = 5 * time.Second
)

// PostgresConfig addresses the public PostgreSQL listener with a project-scoped dashboard API key.
// Zero routing/trust fields use pg.terrascale.tech:5432 and verified system trust.
type PostgresConfig struct {
	// Host and Port override the public listener address for a custom deployment.
	Host string
	Port uint16
	// TLSServerName defaults to Host; CACertificatePEM replaces system roots when supplied.
	TLSServerName    string
	CACertificatePEM []byte
	// Database is a name, optionally qualified as the key's project ULID/name.
	Database string
	// KeyID is optional; when supplied it must equal the ID in Password.
	KeyID string
	// Password is the complete dashboard API key: <ULID>_<52 lowercase Crockford base32 characters>.
	Password string
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

// PostgresValue holds a native pgx scalar or nil for SQL NULL.
// JSON results are json.RawMessage; numeric results retain pgtype.Numeric precision.
type PostgresValue struct {
	oid   uint32
	value any
}

// PostgresParameter binds a pgx-compatible value, including nil and pgtype scalars.
func PostgresParameter(value any) PostgresValue  { return PostgresValue{value: value} }
func PostgresInt8(value int64) PostgresValue     { return PostgresValue{oid: 20, value: value} }
func PostgresText(value string) PostgresValue    { return PostgresValue{oid: 25, value: value} }
func (value PostgresValue) Int8() (int64, bool)  { v, ok := value.value.(int64); return v, ok }
func (value PostgresValue) Text() (string, bool) { v, ok := value.value.(string); return v, ok }
func (value PostgresValue) OID() uint32          { return value.oid }

// Value returns the decoded pgx value, or nil for SQL NULL.
func (value PostgresValue) Value() any { return value.value }

type PostgresQuery struct {
	Statement  string
	Parameters []PostgresValue
}
type PostgresColumn struct {
	Name string
	OID  uint32
}
type PostgresResult struct {
	Command string
	// CommandTag preserves the complete server tag, including DDL object and row counts.
	CommandTag   string
	AffectedRows uint64
	Columns      []PostgresColumn
	Rows         [][]PostgresValue
}

func postgresTextValid(value string) bool {
	return len(value) <= postgresMaxBytes && utf8.ValidString(value) && !strings.ContainsRune(value, '\x00')
}

func postgresParameters(query PostgresQuery) ([]any, error) {
	if strings.TrimSpace(query.Statement) == "" || !postgresTextValid(query.Statement) || len(query.Parameters) > 32767 {
		return nil, &PostgresError{Kind: "query"}
	}
	values := make([]any, len(query.Parameters))
	budget := postgresMaxBytes - len(query.Statement)
	for index, value := range query.Parameters {
		switch v := value.value.(type) {
		case pgx.QueryExecMode, pgx.QueryRewriter, pgx.QueryResultFormats, pgx.QueryResultFormatsByOID:
			return nil, &PostgresError{Kind: "query"}
		case string:
			if !postgresTextValid(v) {
				return nil, &PostgresError{Kind: "query"}
			}
			budget -= len(v)
		case []byte:
			budget -= len(v)
		case json.RawMessage:
			budget -= len(v)
		}
		budget -= 8
		if budget < 0 {
			return nil, &PostgresError{Kind: "limit"}
		}
		values[index] = value.value
	}
	return values, nil
}

var (
	postgresDatabaseName = regexp.MustCompile(`^[a-z][a-z0-9_-]{0,62}$`)
	postgresReservedName = regexp.MustCompile(`^(con|prn|aux|nul|com[0-9]|lpt[0-9])$`)
)

func buildPostgresConfig(config PostgresConfig) (*pgx.ConnConfig, error) {
	if config.Host == "" {
		config.Host = "pg.terrascale.tech"
	}
	if config.Port == 0 {
		config.Port = 5432
	}
	if config.TLSServerName == "" {
		config.TLSServerName = config.Host
	}
	keyID, secret, _ := strings.Cut(config.Password, "_")
	project, name, qualified := strings.Cut(config.Database, "/")
	if !qualified {
		name = config.Database
	}
	if len(config.Host) > 253 || strings.ContainsAny(config.Host, " \t\r\n,/@\x00") ||
		len(config.TLSServerName) > 253 || strings.ContainsAny(config.TLSServerName, " \t\r\n,/@\x00") ||
		!postgresDatabaseName.MatchString(name) || postgresReservedName.MatchString(name) || qualified && ValidateID(project) != nil ||
		ValidateID(keyID) != nil || len(secret) != 52 || strings.Trim(secret, "0123456789abcdefghjkmnpqrstvwxyz") != "" ||
		config.KeyID != "" && config.KeyID != keyID || len(config.CACertificatePEM) > 64*1024 {
		return nil, &PostgresError{Kind: "configuration"}
	}
	var roots *x509.CertPool // nil uses the platform's system certificate roots.
	if len(config.CACertificatePEM) != 0 {
		roots = x509.NewCertPool()
		if !roots.AppendCertsFromPEM(config.CACertificatePEM) {
			return nil, &PostgresError{Kind: "configuration"}
		}
	}
	// pgx has no environment-free config constructor. Refuse service selection
	// before parsing; explicit nonempty stub passwords also prevent pgpass reads.
	if os.Getenv("PGSERVICE") != "" {
		return nil, &PostgresError{Kind: "configuration"}
	}
	const stub = "host=localhost port=1 dbname=unused user=unused password=unused sslmode=disable sslrootcert='' sslcert='' sslkey='' sslpassword='' sslsni=1 sslnegotiation=postgres min_protocol_version=3.0 max_protocol_version=3.0 connect_timeout=5 target_session_attrs=any application_name='' options='' timezone='' passfile=''"
	driver, err := pgx.ParseConfig(stub)
	if err != nil {
		return nil, &PostgresError{Kind: "configuration"}
	}
	driver.Host, driver.Port, driver.Database, driver.User, driver.Password = config.Host, config.Port, config.Database, keyID, config.Password
	driver.ConnectTimeout = postgresConnectTimeout
	driver.DialFunc = (&net.Dialer{Timeout: postgresConnectTimeout}).DialContext
	driver.TLSConfig = &tls.Config{
		RootCAs: roots, ServerName: config.TLSServerName,
		MinVersion: tls.VersionTLS13,
		NextProtos: []string{"postgresql"},
		VerifyConnection: func(state tls.ConnectionState) error {
			// SSLRequest establishes the protocol; the public edge permits absent ALPN.
			if state.NegotiatedProtocol != "" && state.NegotiatedProtocol != "postgresql" {
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
	gate   chan struct{}
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
	return &PostgresClient{driver: connection, gate: make(chan struct{}, 1)}, nil
}

// acquire owns this logical session until released; waiting is cancellation-aware.
func (client *PostgresClient) acquire(ctx context.Context) error {
	if err := ctx.Err(); err != nil {
		return postgresFailure(err)
	}
	if client.gate == nil {
		return &PostgresError{Kind: "connection"}
	}
	select {
	case client.gate <- struct{}{}:
		if err := ctx.Err(); err != nil {
			<-client.gate
			return postgresFailure(err)
		}
		return nil
	case <-ctx.Done():
		return postgresFailure(ctx.Err())
	}
}

// Preserve pgx codecs while preferring the producer's supported parameter format.
type postgresTextCodec struct{ pgtype.Codec }

func (postgresTextCodec) PreferredFormat() int16 { return pgx.TextFormatCode }

// Decode without Rows.Scan: its error path drains Rows before returning.
func postgresDecode(types *pgtype.Map, field pgconn.FieldDescription, raw []byte) (any, error) {
	if raw == nil {
		return nil, nil
	}
	if field.DataTypeOID == 1700 && field.Format == pgx.TextFormatCode {
		if index := bytes.IndexAny(raw, "eE"); index >= 0 {
			// Producer finite reals use scientific text. Shift the native decimal scale
			// exactly; never expand a string or coerce through floating point.
			exponent, err := strconv.ParseInt(string(raw[index+1:]), 10, 32)
			if err != nil || exponent < -postgresMaxBytes || exponent > postgresMaxBytes {
				return nil, &PostgresError{Kind: "decode"}
			}
			var numeric pgtype.Numeric
			if err := types.Scan(1700, field.Format, raw[:index], &numeric); err != nil {
				return nil, err
			}
			if numeric.Int == nil {
				return nil, &PostgresError{Kind: "decode"}
			}
			numeric.Exp += int32(exponent)
			return numeric, nil
		}
	}
	if field.DataTypeOID == 114 || field.DataTypeOID == 3802 {
		var value json.RawMessage
		err := types.Scan(field.DataTypeOID, field.Format, raw, &value)
		return value, err
	}
	var value any
	err := types.Scan(field.DataTypeOID, field.Format, raw, &value)
	return value, err
}

func (client *PostgresClient) abort(ctx context.Context) {
	physical := client.driver.PgConn().Conn()
	if secure, ok := physical.(*tls.Conn); ok {
		physical = secure.NetConn()
	}
	physical.Close() // Unblock I/O before pgx/Rows cleanup, including TLS close_notify.
	canceled, cancel := context.WithCancel(ctx)
	cancel()
	client.driver.PgConn().Close(canceled)
}

// Query sends the SQL and typed values separately through PostgreSQL binding.
// BEGIN, COMMIT and ROLLBACK remain distinct statements; callbacks are never retried.
func (client *PostgresClient) Query(ctx context.Context, query PostgresQuery) (PostgresResult, error) {
	values, err := postgresParameters(query)
	if err != nil {
		return PostgresResult{}, err
	}
	if err := client.acquire(ctx); err != nil {
		return PostgresResult{}, err
	}
	defer func() { <-client.gate }()
	if client.driver == nil || client.driver.IsClosed() {
		return PostgresResult{}, &PostgresError{Kind: "connection"}
	}
	for _, oid := range []uint32{1700, 1082, 1114, 1184} {
		typ, _ := client.driver.TypeMap().TypeForOID(oid)
		if _, ok := typ.Codec.(postgresTextCodec); !ok {
			copy := *typ
			copy.Codec = postgresTextCodec{typ.Codec}
			client.driver.TypeMap().RegisterType(&copy)
		}
	}
	// The producer sends numeric and temporal scalars as text even when binary is requested.
	// Request text explicitly: pgx ExecStatement trusts the requested formats without Describe.
	arguments := append([]any{pgx.QueryResultFormats{pgx.TextFormatCode}}, values...)
	rows, err := client.driver.Query(ctx, query.Statement, arguments...)
	if err != nil {
		return PostgresResult{}, postgresFailure(err)
	}
	defer rows.Close()
	result := PostgresResult{Columns: make([]PostgresColumn, len(rows.FieldDescriptions())), Rows: [][]PostgresValue{}}
	budget := postgresResultBytes
	for index, field := range rows.FieldDescriptions() {
		budget -= len(field.Name) + 64
		result.Columns[index] = PostgresColumn{Name: field.Name, OID: field.DataTypeOID}
	}
	for rows.Next() {
		rawValues := rows.RawValues()
		if len(rawValues) != len(result.Columns) {
			client.abort(ctx)
			return PostgresResult{}, &PostgresError{Kind: "decode"}
		}
		budget -= 24 + 64*len(result.Columns)
		for _, raw := range rawValues {
			budget -= len(raw)
		}
		if budget < 0 {
			// Do not drain an unbounded result or leave an ambiguous session reusable.
			client.abort(ctx)
			return PostgresResult{}, &PostgresError{Kind: "limit"}
		}
		row := make([]PostgresValue, len(result.Columns))
		for index, field := range rows.FieldDescriptions() {
			value, err := postgresDecode(client.driver.TypeMap(), field, rawValues[index])
			if err != nil {
				client.abort(ctx)
				if ctx.Err() != nil {
					return PostgresResult{}, postgresFailure(ctx.Err())
				}
				return PostgresResult{}, &PostgresError{Kind: "decode"}
			}
			row[index] = PostgresValue{oid: field.DataTypeOID, value: value}
		}
		result.Rows = append(result.Rows, row)
	}
	if err := rows.Err(); err != nil {
		client.abort(ctx)
		return PostgresResult{}, postgresFailure(err)
	}
	tag := rows.CommandTag()
	result.CommandTag = tag.String()
	result.Command, _, _ = strings.Cut(result.CommandTag, " ")
	result.AffectedRows = uint64(tag.RowsAffected())
	return result, nil
}

func (client *PostgresClient) Close(ctx context.Context) error {
	if err := client.acquire(ctx); err != nil {
		return err
	}
	defer func() { <-client.gate }()
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
