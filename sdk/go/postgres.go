package sdk

import (
	"bytes"
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"errors"
	"log/slog"
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
	"terrascale.tech/sdk/internal/causal"
	"terrascale.tech/sdk/v1"
)

const ErrCommitUncertain = v1.ErrCommitUncertain

const (
	postgresMaxBytes       = 1024*1024 - 64
	postgresResultBytes    = 16 * 1024 * 1024
	postgresConnectTimeout = 5 * time.Second
	postgresBookmark       = "terrascale.bookmark"
	postgresConsistency    = "terrascale.consistency"
	postgresSerialization  = "40001"
)

type PostgresConfig struct {
	Host             string
	Port             uint16
	TLSServerName    string
	CACertificatePEM []byte
	Database         string
	KeyID            string
	Password         string
	Consistency      Consistency
	Bookmark         string
}

func (PostgresConfig) String() string {
	return "terrascale PostgreSQL configuration (credentials redacted)"
}
func (config PostgresConfig) GoString() string { return config.String() }
func (config PostgresConfig) LogValue() slog.Value {
	return slog.StringValue(config.String())
}

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

func (err *PostgresError) Unwrap() error { return err.cause }

func (err *PostgresError) Is(target error) bool {
	return target == ErrSerializationConflict && err.IsSerializationConflict() ||
		target == ErrCommitUncertain && err.Kind == "commit_uncertain"
}

func (err *PostgresError) IsSerializationConflict() bool {
	return err.Kind != "commit_uncertain" && err.SQLState == postgresSerialization
}

func postgresFailure(err error) *PostgresError {
	if errors.Is(err, context.Canceled) {
		return &PostgresError{Kind: "canceled", cause: context.Canceled}
	}
	if errors.Is(err, context.DeadlineExceeded) {
		return &PostgresError{Kind: "canceled", cause: context.DeadlineExceeded}
	}
	var database *pgconn.PgError
	if errors.As(err, &database) {
		failure := &PostgresError{Kind: "database", SQLState: database.Code}
		switch database.Detail {
		case string(v1.ErrCommitUncertain), string(v1.ErrWriteOutcomeUnknown), string(v1.ErrInRegionQuorumUnconfirmed):
			failure.Kind = "commit_uncertain"
		}
		switch database.Code {
		case "08007", "40003":
			failure.Kind = "commit_uncertain"
		}
		return failure
	}
	return &PostgresError{Kind: "connection"}
}

func postgresCommitFailure(err error) *PostgresError {
	failure := postgresFailure(err)
	switch failure.SQLState {
	case "", "08006", "57P01", "57P02":
		failure.Kind = "commit_uncertain"
	}
	return failure
}

type PostgresValue struct {
	oid   uint32
	value any
}

func PostgresParameter(value any) PostgresValue  { return PostgresValue{value: value} }
func PostgresInt8(value int64) PostgresValue     { return PostgresValue{oid: 20, value: value} }
func PostgresText(value string) PostgresValue    { return PostgresValue{oid: 25, value: value} }
func (value PostgresValue) Int8() (int64, bool)  { v, ok := value.value.(int64); return v, ok }
func (value PostgresValue) Text() (string, bool) { v, ok := value.value.(string); return v, ok }
func (value PostgresValue) OID() uint32          { return value.oid }

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
	Command      string
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
	password := config.Password
	if strings.HasPrefix(password, "sk_") {
		password = password[3:]
	}
	keyID, secret, _ := strings.Cut(password, "_")
	project, name, qualified := strings.Cut(config.Database, "/")
	if !qualified {
		name = config.Database
	}
	if len(config.Host) > 253 || strings.ContainsAny(config.Host, " \t\r\n,/@\x00") ||
		len(config.TLSServerName) > 253 || strings.ContainsAny(config.TLSServerName, " \t\r\n,/@\x00") ||
		!postgresDatabaseName.MatchString(name) || postgresReservedName.MatchString(name) || qualified && ValidateID(project) != nil ||
		ValidateID(keyID) != nil || len(secret) != 52 || strings.Trim(secret, "0123456789abcdefghjkmnpqrstvwxyz") != "" ||
		config.KeyID != "" && config.KeyID != keyID || len(config.CACertificatePEM) > 64*1024 ||
		config.Consistency != "" && !config.Consistency.Valid() || !causal.Valid(config.Bookmark) {
		return nil, &PostgresError{Kind: "configuration"}
	}
	var roots *x509.CertPool
	if len(config.CACertificatePEM) != 0 {
		roots = x509.NewCertPool()
		if !roots.AppendCertsFromPEM(config.CACertificatePEM) {
			return nil, &PostgresError{Kind: "configuration"}
		}
	}
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

type PostgresClient struct {
	gate   chan struct{}
	driver *pgx.Conn
	causal causal.Tracker
	synced string
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
	client := &PostgresClient{driver: connection, gate: make(chan struct{}, 1)}
	if err := client.causal.Observe(config.Bookmark); err != nil {
		connection.Close(ctx)
		return nil, &PostgresError{Kind: "configuration"}
	}
	if config.Consistency != "" {
		if _, err := client.run(ctx, PostgresQuery{Statement: "SET " + postgresConsistency + " = '" + string(config.Consistency) + "'"}, false); err != nil {
			connection.Close(ctx)
			return nil, err
		}
	}
	return client, nil
}

func (client *PostgresClient) Bookmark() string { return client.causal.Load() }

func (client *PostgresClient) sync(ctx context.Context) error {
	outgoing, err := client.causal.Outgoing(ctx)
	if err != nil {
		return &PostgresError{Kind: "query", cause: err}
	}
	if outgoing == "" || outgoing == client.synced {
		return nil
	}
	if _, err := client.run(ctx, PostgresQuery{Statement: "SET " + postgresBookmark + " = '" + outgoing + "'"}, false); err != nil {
		return err
	}
	client.synced = outgoing
	return client.causal.Observe(outgoing)
}

func (client *PostgresClient) observe() error {
	if client.driver == nil {
		return nil
	}
	reported := client.driver.PgConn().ParameterStatus(postgresBookmark)
	if reported == "" || reported == client.synced {
		return nil
	}
	if err := client.causal.Observe(reported); err != nil {
		return &PostgresError{Kind: "protocol"}
	}
	client.synced = reported
	return nil
}

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

type postgresTextCodec struct{ pgtype.Codec }

func (postgresTextCodec) PreferredFormat() int16 { return pgx.TextFormatCode }

func postgresDecode(types *pgtype.Map, field pgconn.FieldDescription, raw []byte) (any, error) {
	if raw == nil {
		return nil, nil
	}
	if field.DataTypeOID == 1700 && field.Format == pgx.TextFormatCode {
		if index := bytes.IndexAny(raw, "eE"); index >= 0 {
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
	physical.Close()
	canceled, cancel := context.WithCancel(ctx)
	cancel()
	client.driver.PgConn().Close(canceled)
}

func (client *PostgresClient) Query(ctx context.Context, query PostgresQuery) (PostgresResult, error) {
	if err := client.acquire(ctx); err != nil {
		return PostgresResult{}, err
	}
	defer func() { <-client.gate }()
	if err := client.sync(ctx); err != nil {
		return PostgresResult{}, err
	}
	return client.run(ctx, query, true)
}

func (client *PostgresClient) openRows(ctx context.Context, query PostgresQuery, autocommit bool) (*PostgresRows, error) {
	values, err := postgresParameters(query)
	if err != nil {
		return nil, err
	}
	if client.driver == nil || client.driver.IsClosed() {
		return nil, &PostgresError{Kind: "connection"}
	}
	for _, oid := range []uint32{1700, 1082, 1114, 1184} {
		typ, _ := client.driver.TypeMap().TypeForOID(oid)
		if _, ok := typ.Codec.(postgresTextCodec); !ok {
			copy := *typ
			copy.Codec = postgresTextCodec{typ.Codec}
			client.driver.TypeMap().RegisterType(&copy)
		}
	}
	arguments := append([]any{pgx.QueryResultFormats{pgx.TextFormatCode}}, values...)
	rows, err := client.driver.Query(ctx, query.Statement, arguments...)
	if err != nil {
		return nil, postgresFailure(err)
	}
	columns := make([]PostgresColumn, len(rows.FieldDescriptions()))
	for index, field := range rows.FieldDescriptions() {
		columns[index] = PostgresColumn{Name: field.Name, OID: field.DataTypeOID}
	}
	return &PostgresRows{client: client, ctx: ctx, rows: rows, columns: columns, autocommit: autocommit}, nil
}

func (client *PostgresClient) run(ctx context.Context, query PostgresQuery, autocommit bool) (PostgresResult, error) {
	stream, err := client.openRows(ctx, query, autocommit)
	if err != nil {
		return PostgresResult{}, err
	}
	result := PostgresResult{Columns: stream.columns, Rows: [][]PostgresValue{}}
	budget := postgresResultBytes
	for _, column := range stream.columns {
		budget -= len(column.Name) + 64
	}
	for stream.Next() {
		row := stream.Values()
		budget -= stream.rawSize + 24 + 64*len(row)
		if budget < 0 {
			stream.abort(&PostgresError{Kind: "limit"})
			return PostgresResult{}, stream.err
		}
		result.Rows = append(result.Rows, row)
	}
	if stream.err != nil {
		return PostgresResult{}, stream.err
	}
	if err := client.observe(); err != nil {
		return PostgresResult{}, err
	}
	result.CommandTag = stream.tag.String()
	result.Command, _, _ = strings.Cut(result.CommandTag, " ")
	result.AffectedRows = uint64(stream.tag.RowsAffected())
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
