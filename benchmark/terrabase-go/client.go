package terrabase

import (
	"context"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"fmt"
	"net"
	"sync/atomic"

	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/metadata"

	v1 "terrascale.tech/terrabase-go/terrabase/v1"
)

// The metadata keys of the engine's cross-cutting conventions: tenant
// identity rides metadata, not fields, and mutations carry their retry
// identity the same way.
const (
	principalMetadata   = "x-tb-principal"
	tenantMetadata      = "x-tb-tenant"
	idempotencyMetadata = "x-tb-idempotency-key"

	// MaxIdempotencyBytes is the idempotency-key bound (1..=128 bytes,
	// no control characters) the engine enforces fail-closed.
	MaxIdempotencyBytes = 128
)

// The declared request bounds, enforced fail-closed by the engine and
// mirrored here so malformed requests are refused before the wire.
const (
	maxIdentityBytes    = 128 // principal
	maxTenantBytes      = 64
	maxCollectionBytes  = 64
	maxDocumentBytes    = 64
	maxOperationBytes   = 128
	maxQueryTextBytes   = 4096
	maxQueryItems       = 1 << 20
	maxBatchItems       = 256
	maxCollections      = 64 // per subscription
	maxFamilyNameBytes  = 128
	maxFamilyCodecBytes = 64
	maxSyncTokenBytes   = 1057 // sync checkpoints and cursors
	maxTierPolicyBytes  = 128
	maxTierPinned       = 4096
	maxFrameBytes       = 16 << 20 // the engine's per-RPC message bound
)

// Binding is the answer of one channel bind: the engine accepted the
// application identity and names its serving instance.
type Binding struct {
	Tenant          string
	ServingInstance uint64
}

// identity is the application identity one bound channel carries.
type identity struct {
	principal string
	tenant    string
}

// Client is one connection to the TerraBase engine: every service of
// the TBEP contract behind typed Go methods. Dial, Bind, then call.
//
// A Client is safe for concurrent use; every RPC except Bind requires
// the channel identity Bind establishes, exactly as the engine does.
type Client struct {
	conn         grpc.ClientConnInterface
	channel      v1.ChannelClient
	engine       v1.EngineClient
	backbone     v1.BackboneClient
	sync         v1.SyncClient
	schema       v1.SchemaClient
	replication  v1.ReplicationClient
	maintenance  v1.MaintenanceClient
	tier         v1.TierClient
	bound        atomic.Pointer[identity]
	defaultLevel ReadLevel
}

// Option configures one Dial. The default transport is TLS against the
// system roots without a client certificate; production lanes pass
// WithRootCerts and WithClientCert for mutually authenticated TLS, dev
// lanes pass WithInsecure.
type Option func(*dialConfig) error

type dialConfig struct {
	tlsConfig *tls.Config
	transport credentials.TransportCredentials
	authority string
	level     ReadLevel
	dialer    func(context.Context, string) (net.Conn, error)
}

// WithRootCerts trusts the given PEM bundle as the engine's root
// certificate authority instead of the system roots.
func WithRootCerts(pem []byte) Option {
	return func(config *dialConfig) error {
		pool := x509.NewCertPool()
		if !pool.AppendCertsFromPEM(pem) {
			return fmt.Errorf("terrabase: the root certificate bundle carries no certificate")
		}
		config.ensureTLS().RootCAs = pool
		return nil
	}
}

// WithClientCert presents the given PEM leaf certificate and key as
// the client identity of a mutually authenticated TLS channel. Pair it
// with WithRootCerts.
func WithClientCert(certPEM, keyPEM []byte) Option {
	return func(config *dialConfig) error {
		pair, err := tls.X509KeyPair(certPEM, keyPEM)
		if err != nil {
			return fmt.Errorf("terrabase: the client certificate pair does not parse: %w", err)
		}
		config.ensureTLS().Certificates = []tls.Certificate{pair}
		return nil
	}
}

// WithInsecure dials a plaintext channel: local and development lanes
// only, never a production binding.
func WithInsecure() Option {
	return func(config *dialConfig) error {
		config.transport = insecure.NewCredentials()
		return nil
	}
}

// WithAuthority overrides the authority (TLS server name and
// :authority header) of the channel, for endpoints whose dial address
// differs from the certificate name.
func WithAuthority(authority string) Option {
	return func(config *dialConfig) error {
		config.authority = authority
		return nil
	}
}

// WithTransportCredentials sets the transport credentials directly:
// the escape hatch for lanes this package's options do not cover.
func WithTransportCredentials(transport credentials.TransportCredentials) Option {
	return func(config *dialConfig) error {
		if transport == nil {
			return fmt.Errorf("terrabase: transport credentials are nil")
		}
		config.transport = transport
		return nil
	}
}

// WithDefaultReadLevel sets the read consistency level every read and
// query uses unless its own parameter says otherwise.
func WithDefaultReadLevel(level ReadLevel) Option {
	return func(config *dialConfig) error {
		config.level = level
		return nil
	}
}

// WithContextDialer supplies the connection function of the target
// address: Unix-domain-socket lanes and in-memory test transports.
func WithContextDialer(dialer func(context.Context, string) (net.Conn, error)) Option {
	return func(config *dialConfig) error {
		if dialer == nil {
			return fmt.Errorf("terrabase: the context dialer is nil")
		}
		config.dialer = dialer
		return nil
	}
}

// ensureTLS allocates the shared TLS configuration the certificate
// options compose into.
func (config *dialConfig) ensureTLS() *tls.Config {
	if config.tlsConfig == nil {
		config.tlsConfig = &tls.Config{}
	}
	return config.tlsConfig
}

// Dial connects to the engine at target (host:port) and returns the
// client. The connection is lazy: the first RPC establishes it. Call
// Close when done.
func Dial(target string, options ...Option) (*Client, error) {
	config := dialConfig{}
	for _, option := range options {
		if err := option(&config); err != nil {
			return nil, err
		}
	}
	transport := config.transport
	if transport == nil {
		if tlsConfig := config.tlsConfig; tlsConfig != nil {
			// The authority names the engine's certificate when the
			// dial address does not (in-memory and socket lanes).
			if tlsConfig.ServerName == "" && config.authority != "" {
				tlsConfig.ServerName = config.authority
			}
			transport = credentials.NewTLS(tlsConfig)
		} else {
			transport = credentials.NewTLS(nil)
		}
	}
	dialOptions := []grpc.DialOption{grpc.WithTransportCredentials(transport)}
	if config.authority != "" {
		dialOptions = append(dialOptions, grpc.WithAuthority(config.authority))
	}
	if config.dialer != nil {
		dialOptions = append(dialOptions, grpc.WithContextDialer(config.dialer))
	}
	conn, err := grpc.NewClient(target, dialOptions...)
	if err != nil {
		return nil, fmt.Errorf("terrabase: dial %s: %w", target, err)
	}
	client := NewClient(conn)
	client.defaultLevel = config.level
	return client, nil
}

// NewClient wraps one existing gRPC connection; the transport options
// of Dial were applied when that connection was created.
func NewClient(conn grpc.ClientConnInterface) *Client {
	return &Client{
		conn:        conn,
		channel:     v1.NewChannelClient(conn),
		engine:      v1.NewEngineClient(conn),
		backbone:    v1.NewBackboneClient(conn),
		sync:        v1.NewSyncClient(conn),
		schema:      v1.NewSchemaClient(conn),
		replication: v1.NewReplicationClient(conn),
		maintenance: v1.NewMaintenanceClient(conn),
		tier:        v1.NewTierClient(conn),
	}
}

// Close tears down the underlying connection when this client owns
// one created by Dial.
func (c *Client) Close() error {
	if c.conn == nil {
		return nil
	}
	closer, ok := c.conn.(interface{ Close() error })
	if !ok {
		return nil
	}
	return closer.Close()
}

// outgoing pairs the caller's context with the bound identity (and,
// for mutations, the idempotency key) the engine's conventions
// require.
func (c *Client) outgoing(ctx context.Context, mutation bool, key string) (context.Context, error) {
	bound := c.bound.Load()
	if bound == nil {
		return nil, &Error{
			Code:   CodeUnboundChannel,
			Detail: "the client carries no bound channel identity; call Bind first",
		}
	}
	pairs := []string{
		principalMetadata, bound.principal,
		tenantMetadata, bound.tenant,
	}
	if mutation {
		if key == "" {
			return nil, localError("the mutation carries no idempotency key")
		}
		if len(key) > MaxIdempotencyBytes {
			return nil, &Error{
				Code:   CodeIdempotencyPairing,
				Detail: fmt.Sprintf("the idempotency key exceeds %d bytes", MaxIdempotencyBytes),
			}
		}
		for index := 0; index < len(key); index++ {
			if key[index] < 0x20 || key[index] == 0x7f {
				return nil, &Error{
					Code:   CodeIdempotencyPairing,
					Detail: "the idempotency key carries control characters",
				}
			}
		}
		pairs = append(pairs, idempotencyMetadata, key)
	}
	return metadata.AppendToOutgoingContext(ctx, pairs...), nil
}

// CallOption tunes one call.
type CallOption func(*callConfig)

type callConfig struct {
	idempotencyKey string
}

// WithIdempotencyKey names the retry identity of one mutation. Without
// it the operation identity is used, and a mutation with no operation
// identity gets a fresh random key: safe for first attempts, never for
// retries.
func WithIdempotencyKey(key string) CallOption {
	return func(config *callConfig) { config.idempotencyKey = key }
}

// retryKey resolves the idempotency key of one mutation.
func retryKey(operationID string, options []CallOption) (string, error) {
	config := callConfig{}
	for _, option := range options {
		option(&config)
	}
	if config.idempotencyKey != "" {
		return config.idempotencyKey, nil
	}
	if operationID != "" {
		return operationID, nil
	}
	buffer := make([]byte, 16)
	if _, err := rand.Read(buffer); err != nil {
		return "", fmt.Errorf("terrabase: the idempotency key could not be generated: %w", err)
	}
	return hex.EncodeToString(buffer), nil
}

// boundName validates one required bounded identifier field locally,
// mirroring the engine's fail-closed bounds.
func boundName(field, value string, limit int) error {
	if value == "" {
		return localError("the %s is empty", field)
	}
	if len(value) > limit {
		return localError("the %s exceeds %d bytes", field, limit)
	}
	return nil
}

// optionalName validates one bounded identifier that may be empty.
func optionalName(field, value string, limit int) error {
	if len(value) > limit {
		return localError("the %s exceeds %d bytes", field, limit)
	}
	return nil
}

// levelOf resolves the effective read level of one call.
func (c *Client) levelOf(level ReadLevel) ReadLevel {
	if level != ReadLevelDefault {
		return level
	}
	return c.defaultLevel
}

// ---------------------------------------------------------------------------
// Channel
// ---------------------------------------------------------------------------

// Bind establishes the channel identity every later RPC carries: the
// application identity within the mutually authenticated transport.
// The transport is expected to be mTLS in production; the metadata
// names the identity, it does not authenticate it.
func (c *Client) Bind(ctx context.Context, principal, tenant string) (Binding, error) {
	if err := boundName("principal", principal, maxIdentityBytes); err != nil {
		return Binding{}, err
	}
	if err := boundName("tenant", tenant, maxTenantBytes); err != nil {
		return Binding{}, err
	}
	response, err := c.channel.Bind(ctx, &v1.ChannelBindRequest{Principal: principal, Tenant: tenant})
	if err != nil {
		return Binding{}, interpret(err)
	}
	c.bound.Store(&identity{principal: principal, tenant: response.GetTenant()})
	return Binding{Tenant: response.GetTenant(), ServingInstance: response.GetServingInstance()}, nil
}

// ---------------------------------------------------------------------------
// Engine: documents, queries, families
// ---------------------------------------------------------------------------

// DocumentRead reads one document at the given consistency level. The
// second return is false when the collection holds no such document.
func (c *Client) DocumentRead(ctx context.Context, collection, document string, level ReadLevel) (Document, bool, error) {
	if err := boundName("collection", collection, maxCollectionBytes); err != nil {
		return Document{}, false, err
	}
	if err := boundName("document", document, maxDocumentBytes); err != nil {
		return Document{}, false, err
	}
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return Document{}, false, err
	}
	response, err := c.engine.DocumentRead(ctx, &v1.DocumentReadRequest{
		Collection: collection,
		Document:   document,
		Level:      c.levelOf(level).proto(),
	})
	if err != nil {
		return Document{}, false, interpret(err)
	}
	if !response.GetFound() {
		return Document{}, false, nil
	}
	read, err := documentFrom(response.GetDocument())
	if err != nil {
		return Document{}, false, err
	}
	read.Position = response.GetPosition()
	return read, true, nil
}

// DocumentWrite puts one document (create or overwrite) under the
// expected-version discipline. Conflict and refusal are outcomes, not
// errors: read them off the returned WriteOutcome.
func (c *Client) DocumentWrite(ctx context.Context, put DocumentPut, expected Expected, ack Acknowledgement, operationID string, options ...CallOption) (WriteOutcome, error) {
	if err := boundName("collection", put.Collection, maxCollectionBytes); err != nil {
		return WriteOutcome{}, err
	}
	if err := boundName("document id", put.ID, maxDocumentBytes); err != nil {
		return WriteOutcome{}, err
	}
	if err := optionalName("operation id", operationID, maxOperationBytes); err != nil {
		return WriteOutcome{}, err
	}
	body, err := valueToProto(put.Body)
	if err != nil {
		return WriteOutcome{}, err
	}
	key, err := retryKey(operationID, options)
	if err != nil {
		return WriteOutcome{}, err
	}
	ctx, err = c.outgoing(ctx, true, key)
	if err != nil {
		return WriteOutcome{}, err
	}
	response, err := c.engine.DocumentWrite(ctx, &v1.DocumentWriteRequest{
		Document: &v1.DocumentPut{
			Id:         put.ID,
			Collection: put.Collection,
			Body:       body,
		},
		Expected:    expected.proto(),
		OperationId: operationID,
		Ack:         ack.proto(),
	})
	if err != nil {
		return WriteOutcome{}, interpret(err)
	}
	return writeOutcomeFrom(response.GetOutcome()), nil
}

// DocumentDelete deletes one document under the expected-version
// discipline.
func (c *Client) DocumentDelete(ctx context.Context, collection, document string, expected Expected, ack Acknowledgement, operationID string, options ...CallOption) (WriteOutcome, error) {
	if err := boundName("collection", collection, maxCollectionBytes); err != nil {
		return WriteOutcome{}, err
	}
	if err := boundName("document", document, maxDocumentBytes); err != nil {
		return WriteOutcome{}, err
	}
	if err := optionalName("operation id", operationID, maxOperationBytes); err != nil {
		return WriteOutcome{}, err
	}
	key, err := retryKey(operationID, options)
	if err != nil {
		return WriteOutcome{}, err
	}
	ctx, err = c.outgoing(ctx, true, key)
	if err != nil {
		return WriteOutcome{}, err
	}
	response, err := c.engine.DocumentDelete(ctx, &v1.DocumentDeleteRequest{
		Collection:  collection,
		Document:    document,
		Expected:    expected.proto(),
		OperationId: operationID,
		Ack:         ack.proto(),
	})
	if err != nil {
		return WriteOutcome{}, interpret(err)
	}
	return writeOutcomeFrom(response.GetOutcome()), nil
}

// DocumentBatch applies one atomic batch of puts. Each item carries
// its own expected-version discipline and operation identity.
func (c *Client) DocumentBatch(ctx context.Context, items []BatchItem, ack Acknowledgement, options ...CallOption) (WriteOutcome, error) {
	if len(items) == 0 || len(items) > maxBatchItems {
		return WriteOutcome{}, localError("a batch carries 1..=%d items, got %d", maxBatchItems, len(items))
	}
	batch := make([]*v1.BatchWrite, 0, len(items))
	for index, item := range items {
		if err := boundName("collection", item.Document.Collection, maxCollectionBytes); err != nil {
			return WriteOutcome{}, fmt.Errorf("terrabase: batch item %d: %w", index, err)
		}
		if err := boundName("document id", item.Document.ID, maxDocumentBytes); err != nil {
			return WriteOutcome{}, fmt.Errorf("terrabase: batch item %d: %w", index, err)
		}
		if err := optionalName("operation id", item.OperationID, maxOperationBytes); err != nil {
			return WriteOutcome{}, fmt.Errorf("terrabase: batch item %d: %w", index, err)
		}
		body, err := valueToProto(item.Document.Body)
		if err != nil {
			return WriteOutcome{}, fmt.Errorf("terrabase: batch item %d: %w", index, err)
		}
		batch = append(batch, &v1.BatchWrite{
			Document: &v1.DocumentPut{
				Id:         item.Document.ID,
				Collection: item.Document.Collection,
				Body:       body,
			},
			Expected:    item.Expected.proto(),
			OperationId: item.OperationID,
		})
	}
	key, err := retryKey("", options)
	if err != nil {
		return WriteOutcome{}, err
	}
	ctx, err = c.outgoing(ctx, true, key)
	if err != nil {
		return WriteOutcome{}, err
	}
	response, err := c.engine.DocumentBatch(ctx, &v1.DocumentBatchRequest{Items: batch, Ack: ack.proto()})
	if err != nil {
		return WriteOutcome{}, interpret(err)
	}
	return writeOutcomeFrom(response.GetOutcome()), nil
}

// QueryExecute executes one query bound to queryID; the identifier is
// the handle QueryCancel cancels. maxItems of zero lets the engine
// choose its bound.
func (c *Client) QueryExecute(ctx context.Context, queryID uint64, text string, maxItems uint32, level ReadLevel) (QueryResult, error) {
	if text == "" || len(text) > maxQueryTextBytes {
		return QueryResult{}, localError("query text is 1..=%d bytes", maxQueryTextBytes)
	}
	if maxItems > maxQueryItems {
		return QueryResult{}, localError("the query item bound exceeds %d", maxQueryItems)
	}
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return QueryResult{}, err
	}
	response, err := c.engine.QueryExecute(ctx, &v1.QueryExecuteRequest{
		QueryId:  queryID,
		Text:     text,
		MaxItems: maxItems,
		Level:    c.levelOf(level).proto(),
	})
	if err != nil {
		return QueryResult{}, interpret(err)
	}
	result := QueryResult{
		Count:     int(response.GetCount()),
		Truncated: response.GetTerminal() == v1.QueryTerminal_QUERY_TERMINAL_TRUNCATED,
		Position:  response.GetPosition(),
	}
	for _, body := range response.GetDocuments() {
		document, err := documentFrom(body)
		if err != nil {
			return QueryResult{}, err
		}
		result.Documents = append(result.Documents, document)
	}
	return result, nil
}

// QueryCancel cancels one running query.
func (c *Client) QueryCancel(ctx context.Context, queryID uint64) (CancelOutcome, error) {
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return 0, err
	}
	response, err := c.engine.QueryCancel(ctx, &v1.QueryCancelRequest{QueryId: queryID})
	if err != nil {
		return 0, interpret(err)
	}
	return cancelOutcomeFrom(response.GetOutcome()), nil
}

// FamilyRead reads one typed family value (attachment, counter, time
// series, ...) by name, carrying the family protocol's request payload
// and naming its codec.
func (c *Client) FamilyRead(ctx context.Context, family Family, name string, codec string, request []byte) (FamilyResponse, error) {
	if err := family.validate(); err != nil {
		return FamilyResponse{}, err
	}
	if err := familyName(name); err != nil {
		return FamilyResponse{}, err
	}
	if err := optionalName("family request codec", codec, maxFamilyCodecBytes); err != nil {
		return FamilyResponse{}, err
	}
	if len(request) > maxFrameBytes {
		return FamilyResponse{}, localError("the family request exceeds %d bytes", maxFrameBytes)
	}
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return FamilyResponse{}, err
	}
	response, err := c.engine.FamilyRead(ctx, &v1.FamilyReadRequest{
		Family:       string(family),
		Name:         name,
		RequestCodec: codec,
		Request:      request,
	})
	if err != nil {
		return FamilyResponse{}, interpret(err)
	}
	return FamilyResponse{Codec: response.GetResponseCodec(), Payload: response.GetResponse()}, nil
}

// FamilyWrite writes one typed family value under an operation
// identity.
func (c *Client) FamilyWrite(ctx context.Context, family Family, name string, codec string, request []byte, operationID string, options ...CallOption) (FamilyResponse, error) {
	if err := family.validate(); err != nil {
		return FamilyResponse{}, err
	}
	if err := familyName(name); err != nil {
		return FamilyResponse{}, err
	}
	if err := optionalName("family request codec", codec, maxFamilyCodecBytes); err != nil {
		return FamilyResponse{}, err
	}
	if err := optionalName("operation id", operationID, maxOperationBytes); err != nil {
		return FamilyResponse{}, err
	}
	if len(request) > maxFrameBytes {
		return FamilyResponse{}, localError("the family request exceeds %d bytes", maxFrameBytes)
	}
	key, err := retryKey(operationID, options)
	if err != nil {
		return FamilyResponse{}, err
	}
	ctx, err = c.outgoing(ctx, true, key)
	if err != nil {
		return FamilyResponse{}, err
	}
	response, err := c.engine.FamilyWrite(ctx, &v1.FamilyWriteRequest{
		Family:       string(family),
		Name:         name,
		RequestCodec: codec,
		Request:      request,
		OperationId:  operationID,
	})
	if err != nil {
		return FamilyResponse{}, interpret(err)
	}
	return FamilyResponse{Codec: response.GetResponseCodec(), Payload: response.GetResponse()}, nil
}

// validate checks the closed family vocabulary locally.
func (family Family) validate() error {
	switch family {
	case FamilyAttachment, FamilyCounter, FamilyTimeSeries, FamilyRevisions,
		FamilyCompareExchange, FamilyIdentities, FamilyExpirationRefresh, FamilyArchival:
		return nil
	default:
		return localError("the family %q is outside the closed family vocabulary", string(family))
	}
}

// familyName bounds one family name (1..=128 bytes).
func familyName(value string) error {
	if value == "" {
		return localError("the family name is empty")
	}
	if len(value) > maxFamilyNameBytes {
		return localError("the family name exceeds %d bytes", maxFamilyNameBytes)
	}
	return nil
}

// ---------------------------------------------------------------------------
// Sync sessions
// ---------------------------------------------------------------------------

// SyncSessionSetup opens one sync delivery session over a collection.
// A non-empty resume token resumes a prior session; an empty one
// starts fresh.
func (c *Client) SyncSessionSetup(ctx context.Context, collection string, resume []byte) (SyncSession, error) {
	if err := boundName("collection", collection, maxCollectionBytes); err != nil {
		return SyncSession{}, err
	}
	if len(resume) > maxSyncTokenBytes {
		return SyncSession{}, localError("the resume token exceeds %d bytes", maxSyncTokenBytes)
	}
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return SyncSession{}, err
	}
	response, err := c.sync.SessionSetup(ctx, &v1.SyncSessionSetupRequest{Collection: collection, Resume: resume})
	if err != nil {
		return SyncSession{}, interpret(err)
	}
	return SyncSession{
		ID:            response.GetSessionId(),
		Checkpoint:    response.GetCheckpoint(),
		RetainedFloor: response.GetRetainedFloor(),
	}, nil
}

// SyncDeltaPull pulls one page of committed deltas after from.
func (c *Client) SyncDeltaPull(ctx context.Context, sessionID uint64, from []byte) (DeltaPage, error) {
	if len(from) > maxSyncTokenBytes {
		return DeltaPage{}, localError("the delta cursor exceeds %d bytes", maxSyncTokenBytes)
	}
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return DeltaPage{}, err
	}
	response, err := c.sync.DeltaPull(ctx, &v1.SyncDeltaPullRequest{SessionId: sessionID, From: from})
	if err != nil {
		return DeltaPage{}, interpret(err)
	}
	return DeltaPage{
		SessionID:            response.GetSessionId(),
		Codec:                response.GetPayloadCodec(),
		Payload:              response.GetPayload(),
		Next:                 response.GetNext(),
		Commits:              response.GetCommits(),
		SingleCommitOversize: response.GetSingleCommitOversize(),
	}, nil
}

// SyncSnapshotPull pulls one full snapshot of the session's collection.
func (c *Client) SyncSnapshotPull(ctx context.Context, sessionID uint64) (Snapshot, error) {
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return Snapshot{}, err
	}
	response, err := c.sync.SnapshotPull(ctx, &v1.SyncSnapshotPullRequest{SessionId: sessionID})
	if err != nil {
		return Snapshot{}, interpret(err)
	}
	return Snapshot{
		SessionID:  response.GetSessionId(),
		Codec:      response.GetPayloadCodec(),
		Payload:    response.GetPayload(),
		Checkpoint: response.GetCheckpoint(),
	}, nil
}

// SyncCheckpointAck acknowledges one checkpoint of a sync session.
func (c *Client) SyncCheckpointAck(ctx context.Context, sessionID uint64, checkpoint []byte) error {
	if len(checkpoint) > maxSyncTokenBytes {
		return localError("the checkpoint exceeds %d bytes", maxSyncTokenBytes)
	}
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return err
	}
	_, err = c.sync.CheckpointAck(ctx, &v1.SyncCheckpointAckRequest{SessionId: sessionID, Checkpoint: checkpoint})
	if err != nil {
		return interpret(err)
	}
	return nil
}

// SyncMutationSubmit submits one mutation within a sync session.
func (c *Client) SyncMutationSubmit(ctx context.Context, sessionID uint64, document string, change SyncChange, operationID string, options ...CallOption) (MutationOutcome, error) {
	if err := boundName("document", document, maxDocumentBytes); err != nil {
		return MutationOutcome{}, err
	}
	if err := optionalName("operation id", operationID, maxOperationBytes); err != nil {
		return MutationOutcome{}, err
	}
	converted, err := change.proto()
	if err != nil {
		return MutationOutcome{}, err
	}
	key, err := retryKey(operationID, options)
	if err != nil {
		return MutationOutcome{}, err
	}
	ctx, err = c.outgoing(ctx, true, key)
	if err != nil {
		return MutationOutcome{}, err
	}
	response, err := c.sync.MutationSubmit(ctx, &v1.SyncMutationSubmitRequest{
		SessionId: sessionID,
		Document:  document,
		Change:    converted,
	})
	if err != nil {
		return MutationOutcome{}, interpret(err)
	}
	return mutationOutcomeFrom(response.GetOutcome()), nil
}

// SyncSessionClose closes one sync session.
func (c *Client) SyncSessionClose(ctx context.Context, sessionID uint64) error {
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return err
	}
	_, err = c.sync.SessionClose(ctx, &v1.SyncSessionCloseRequest{SessionId: sessionID})
	if err != nil {
		return interpret(err)
	}
	return nil
}

// ---------------------------------------------------------------------------
// Schema and migrations
// ---------------------------------------------------------------------------

// SchemaDeclare declares one collection's schema; the engine answers
// the enforced version and rule count.
func (c *Client) SchemaDeclare(ctx context.Context, collection string, schema Value, operationID string, options ...CallOption) (SchemaInfo, error) {
	if err := boundName("collection", collection, maxCollectionBytes); err != nil {
		return SchemaInfo{}, err
	}
	if err := optionalName("operation id", operationID, maxOperationBytes); err != nil {
		return SchemaInfo{}, err
	}
	declared, err := valueToProto(schema)
	if err != nil {
		return SchemaInfo{}, err
	}
	key, err := retryKey(operationID, options)
	if err != nil {
		return SchemaInfo{}, err
	}
	ctx, err = c.outgoing(ctx, true, key)
	if err != nil {
		return SchemaInfo{}, err
	}
	response, err := c.schema.Declare(ctx, &v1.SchemaDeclareRequest{
		Collection:  collection,
		Schema:      declared,
		OperationId: operationID,
	})
	if err != nil {
		return SchemaInfo{}, interpret(err)
	}
	return SchemaInfo{EnforcedVersion: response.GetEnforcedVersion(), RuleCount: response.GetRuleCount()}, nil
}

// SchemaStatus reads one collection's enforced schema state.
func (c *Client) SchemaStatus(ctx context.Context, collection string) (SchemaInfo, error) {
	if err := boundName("collection", collection, maxCollectionBytes); err != nil {
		return SchemaInfo{}, err
	}
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return SchemaInfo{}, err
	}
	response, err := c.schema.Status(ctx, &v1.SchemaStatusRequest{Collection: collection})
	if err != nil {
		return SchemaInfo{}, interpret(err)
	}
	return SchemaInfo{EnforcedVersion: response.GetEnforcedVersion(), RuleCount: response.GetRuleCount()}, nil
}

// SchemaValidate validates one document against the collection's
// enforced schema without storing it.
func (c *Client) SchemaValidate(ctx context.Context, collection string, document Value) (Validation, error) {
	if err := boundName("collection", collection, maxCollectionBytes); err != nil {
		return Validation{}, err
	}
	body, err := valueToProto(document)
	if err != nil {
		return Validation{}, err
	}
	ctx, err = c.outgoing(ctx, false, "")
	if err != nil {
		return Validation{}, err
	}
	response, err := c.schema.Validate(ctx, &v1.SchemaValidateRequest{Collection: collection, Document: body})
	if err != nil {
		return Validation{}, interpret(err)
	}
	validation := Validation{
		EnforcedVersion: response.GetEnforcedVersion(),
		Valid:           response.GetValid(),
	}
	for _, violation := range response.GetViolations() {
		validation.Violations = append(validation.Violations, Violation{
			Field: violation.GetField(),
			Kind:  violationKindFrom(violation.GetKind()),
		})
	}
	return validation, nil
}

// MigrationStatus reads one collection's migration state.
func (c *Client) MigrationStatus(ctx context.Context, collection string) (MigrationState, error) {
	if err := boundName("collection", collection, maxCollectionBytes); err != nil {
		return MigrationState{}, err
	}
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return MigrationState{}, err
	}
	response, err := c.schema.MigrationStatus(ctx, &v1.MigrationStatusRequest{Collection: collection})
	if err != nil {
		return MigrationState{}, interpret(err)
	}
	state := response.GetState()
	return MigrationState{
		Kind:        migrationStateKindFrom(state.GetKind()),
		Manifest:    state.GetManifest(),
		FromVersion: state.GetFromVersion(),
		ToVersion:   state.GetToVersion(),
		Documents:   state.GetDocuments(),
		Transformed: state.GetTransformed(),
		Pages:       state.GetPages(),
	}, nil
}

// MigrationDryRun plans one migration without applying it.
func (c *Client) MigrationDryRun(ctx context.Context, contract MigrationContract) (MigrationPlan, error) {
	converted, err := contract.proto()
	if err != nil {
		return MigrationPlan{}, err
	}
	ctx, err = c.outgoing(ctx, false, "")
	if err != nil {
		return MigrationPlan{}, err
	}
	response, err := c.schema.MigrationDryRun(ctx, &v1.MigrationDryRunRequest{Contract: converted})
	if err != nil {
		return MigrationPlan{}, interpret(err)
	}
	return migrationPlanFrom(response.GetPlan()), nil
}

// MigrationApply applies one migration contract. An irreversible
// migration requires acknowledgeNotRollbackable.
func (c *Client) MigrationApply(ctx context.Context, contract MigrationContract, acknowledgeNotRollbackable bool, operationID string, options ...CallOption) (MigrationReceipt, error) {
	converted, err := contract.proto()
	if err != nil {
		return MigrationReceipt{}, err
	}
	if err := optionalName("operation id", operationID, maxOperationBytes); err != nil {
		return MigrationReceipt{}, err
	}
	key, err := retryKey(operationID, options)
	if err != nil {
		return MigrationReceipt{}, err
	}
	ctx, err = c.outgoing(ctx, true, key)
	if err != nil {
		return MigrationReceipt{}, err
	}
	response, err := c.schema.MigrationApply(ctx, &v1.MigrationApplyRequest{
		Contract:                   converted,
		AcknowledgeNotRollbackable: acknowledgeNotRollbackable,
		OperationId:                operationID,
	})
	if err != nil {
		return MigrationReceipt{}, interpret(err)
	}
	receipt := response.GetReceipt()
	return MigrationReceipt{
		Collection:                  receipt.GetCollection(),
		Manifest:                    receipt.GetManifest(),
		FromVersion:                 receipt.GetFromVersion(),
		ToVersion:                   receipt.GetToVersion(),
		SourceDigest:                receipt.GetSourceDigest(),
		ResultDigest:                receipt.GetResultDigest(),
		Documents:                   receipt.GetDocuments(),
		Transformed:                 receipt.GetTransformed(),
		TransformCounts:             transformCountsFrom(receipt.GetTransformCounts()),
		Pages:                       receipt.GetPages(),
		RollbackLimit:               rollbackLimitFrom(receipt.GetRollbackLimit()),
		AcknowledgedNotRollbackable: receipt.GetAcknowledgedNotRollbackable(),
		AlreadyApplied:              receipt.GetAlreadyApplied(),
	}, nil
}

// ---------------------------------------------------------------------------
// Replication observation (read-only)
// ---------------------------------------------------------------------------

// ReplicationConflicts observes the resolved conflicts of the local
// replication member.
func (c *Client) ReplicationConflicts(ctx context.Context) (ConflictsReport, error) {
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return ConflictsReport{}, err
	}
	response, err := c.replication.Conflicts(ctx, &v1.ReplicationConflictsRequest{})
	if err != nil {
		return ConflictsReport{}, interpret(err)
	}
	report := ConflictsReport{Member: response.GetMember()}
	for _, record := range response.GetRecords() {
		converted := ConflictRecord{
			Tenant:     record.GetTenant(),
			Operations: record.GetOperations(),
			Members:    record.GetMembers(),
			Outcome: ConflictOutcome{
				Kind:   conflictOutcomeKindFrom(record.GetOutcome().GetKind()),
				Member: record.GetOutcome().GetMember(),
			},
		}
		for _, clock := range record.GetClocks() {
			converted.Clocks = append(converted.Clocks, ClockStamp{Ticks: clock.GetTicks(), Counter: clock.GetCounter()})
		}
		report.Records = append(report.Records, converted)
	}
	return report, nil
}

// ReplicationFenced observes the fenced ranges of the local
// replication member.
func (c *Client) ReplicationFenced(ctx context.Context) (FencedReport, error) {
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return FencedReport{}, err
	}
	response, err := c.replication.Fenced(ctx, &v1.ReplicationFencedRequest{})
	if err != nil {
		return FencedReport{}, interpret(err)
	}
	report := FencedReport{Member: response.GetMember()}
	for _, record := range response.GetRecords() {
		report.Records = append(report.Records, FencedRecord{
			Range:        record.GetRange(),
			BatchEpoch:   record.GetBatchEpoch(),
			HighestEpoch: record.GetHighestEpoch(),
			Source:       record.GetSource(),
		})
	}
	return report, nil
}

// ReplicationDepositions observes the leadership depositions of the
// local replication member.
func (c *Client) ReplicationDepositions(ctx context.Context) (DepositionsReport, error) {
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return DepositionsReport{}, err
	}
	response, err := c.replication.Depositions(ctx, &v1.ReplicationDepositionsRequest{})
	if err != nil {
		return DepositionsReport{}, interpret(err)
	}
	report := DepositionsReport{Member: response.GetMember()}
	for _, record := range response.GetRecords() {
		report.Records = append(report.Records, DepositionRecord{
			Range:      record.GetRange(),
			HeldEpoch:  record.GetHeldEpoch(),
			OwnerEpoch: record.GetOwnerEpoch(),
			NewOwner:   record.GetNewOwner(),
		})
	}
	return report, nil
}

// ReplicationCursor reads the replication position of the local
// member.
func (c *Client) ReplicationCursor(ctx context.Context) (Cursor, error) {
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return Cursor{}, err
	}
	response, err := c.replication.Cursor(ctx, &v1.ReplicationCursorRequest{})
	if err != nil {
		return Cursor{}, interpret(err)
	}
	return Cursor{Member: response.GetMember(), Position: response.GetPosition()}, nil
}

// ReplicationHealth reads the serving health of the local replication
// member.
func (c *Client) ReplicationHealth(ctx context.Context) (ReplicationHealth, error) {
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return ReplicationHealth{}, err
	}
	response, err := c.replication.Health(ctx, &v1.ReplicationHealthRequest{})
	if err != nil {
		return ReplicationHealth{}, interpret(err)
	}
	return ReplicationHealth{
		Member: response.GetMember(),
		Status: replicationStatusFrom(response.GetStatus()),
	}, nil
}

// ReplicationDenials observes the cross-tenant denials of the local
// replication member.
func (c *Client) ReplicationDenials(ctx context.Context) (DenialsReport, error) {
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return DenialsReport{}, err
	}
	response, err := c.replication.Denials(ctx, &v1.ReplicationDenialsRequest{})
	if err != nil {
		return DenialsReport{}, interpret(err)
	}
	report := DenialsReport{Member: response.GetMember()}
	for _, record := range response.GetRecords() {
		report.Records = append(report.Records, DenialRecord{
			Seam:            denialSeamFrom(record.GetSeam()),
			ScopeTenant:     record.GetScopeTenant(),
			AttemptedTenant: record.GetAttemptedTenant(),
			Counterparty:    record.GetCounterparty(),
		})
	}
	return report, nil
}

// ---------------------------------------------------------------------------
// Maintenance: topology, administration, supervision
// ---------------------------------------------------------------------------

// TopologyRead reads the serving topology of the connected instance.
func (c *Client) TopologyRead(ctx context.Context) (Topology, error) {
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return Topology{}, err
	}
	response, err := c.maintenance.TopologyRead(ctx, &v1.TopologyReadRequest{})
	if err != nil {
		return Topology{}, interpret(err)
	}
	return Topology{
		Binding:                 bindingKindFrom(response.GetBinding()),
		Subsystems:              response.GetSubsystems(),
		EngineTopologyAvailable: response.GetEngineTopologyAvailable(),
		ClusterMode:             clusterModeFrom(response.GetClusterMode()),
	}, nil
}

// AdminStatus reads the administrative status of the serving instance.
func (c *Client) AdminStatus(ctx context.Context) (AdminSnapshot, error) {
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return AdminSnapshot{}, err
	}
	response, err := c.maintenance.AdminStatus(ctx, &v1.AdminStatusRequest{})
	if err != nil {
		return AdminSnapshot{}, interpret(err)
	}
	return AdminSnapshot{
		RegistryVersion: response.GetRegistryVersion(),
		FramesServed:    response.GetFramesServed(),
		Sessions:        response.GetSessions(),
		Subscriptions:   response.GetSubscriptions(),
		Scopes:          response.GetScopes(),
	}, nil
}

// SupervisionHealth reads the serving health of the engine.
func (c *Client) SupervisionHealth(ctx context.Context) (SupervisionReport, error) {
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return SupervisionReport{}, err
	}
	response, err := c.maintenance.SupervisionHealth(ctx, &v1.SupervisionHealthRequest{})
	if err != nil {
		return SupervisionReport{}, interpret(err)
	}
	return SupervisionReport{
		State:         healthStateFrom(response.GetState()),
		Sessions:      response.GetSessions(),
		Subscriptions: response.GetSubscriptions(),
		FramesServed:  response.GetFramesServed(),
	}, nil
}

// SupervisionShutdown asks the engine to begin a supervised shutdown.
func (c *Client) SupervisionShutdown(ctx context.Context, reason ShutdownReason) (ShutdownReceipt, error) {
	protoReason, ok := reason.proto()
	if !ok {
		return ShutdownReceipt{}, localError("a shutdown carries no reason")
	}
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return ShutdownReceipt{}, err
	}
	response, err := c.maintenance.SupervisionShutdown(ctx, &v1.SupervisionShutdownRequest{Reason: protoReason})
	if err != nil {
		return ShutdownReceipt{}, interpret(err)
	}
	return ShutdownReceipt{
		State:    healthStateFrom(response.GetState()),
		InFlight: response.GetInFlight(),
	}, nil
}

// ---------------------------------------------------------------------------
// Tier administration
// ---------------------------------------------------------------------------

// TierConfigure configures the storage tier policy.
func (c *Client) TierConfigure(ctx context.Context, policy TierPolicy, options ...CallOption) (TierPolicyStatus, error) {
	if err := boundName("policy id", policy.PolicyID, maxTierPolicyBytes); err != nil {
		return TierPolicyStatus{}, err
	}
	if len(policy.PinnedSegmentIDs) > maxTierPinned {
		return TierPolicyStatus{}, localError("the pinned segment list exceeds %d items", maxTierPinned)
	}
	key, err := retryKey(policy.PolicyID, options)
	if err != nil {
		return TierPolicyStatus{}, err
	}
	ctx, err = c.outgoing(ctx, true, key)
	if err != nil {
		return TierPolicyStatus{}, err
	}
	response, err := c.tier.Configure(ctx, &v1.TierConfigureRequest{
		PolicyId:           policy.PolicyID,
		Version:            policy.Version,
		NvmeBudgetBytes:    policy.NVMeBudgetBytes,
		MinResidenceMoves:  policy.MinResidenceMoves,
		PromotionThreshold: policy.PromotionThreshold,
		PinnedSegmentIds:   policy.PinnedSegmentIDs,
	})
	if err != nil {
		return TierPolicyStatus{}, interpret(err)
	}
	return TierPolicyStatus{
		PolicyID: response.GetPolicyId(),
		Version:  response.GetVersion(),
		Paused:   response.GetPaused(),
	}, nil
}

// TierInspect reads the storage tier's serving state.
func (c *Client) TierInspect(ctx context.Context) (TierStatus, error) {
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return TierStatus{}, err
	}
	response, err := c.tier.Inspect(ctx, &v1.TierInspectRequest{})
	if err != nil {
		return TierStatus{}, interpret(err)
	}
	return TierStatus{
		Configured:    response.GetConfigured(),
		Paused:        response.GetPaused(),
		ManifestEpoch: response.GetManifestEpoch(),
		ObjectCount:   response.GetObjectCount(),
		NVMeBytes:     response.GetNvmeBytes(),
	}, nil
}

// TierDryRun evaluates the tier policy without moving anything.
func (c *Client) TierDryRun(ctx context.Context) (TierMove, error) {
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return TierMove{}, err
	}
	response, err := c.tier.DryRun(ctx, &v1.TierDryRunRequest{})
	if err != nil {
		return TierMove{}, interpret(err)
	}
	return tierMoveFrom(response.GetReceipt()), nil
}

// TierExplain explains the tier policy's next decision.
func (c *Client) TierExplain(ctx context.Context) (TierMove, error) {
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return TierMove{}, err
	}
	response, err := c.tier.Explain(ctx, &v1.TierExplainRequest{})
	if err != nil {
		return TierMove{}, interpret(err)
	}
	return tierMoveFrom(response.GetReceipt()), nil
}

// TierRun runs one tier move pass under an operation identity.
func (c *Client) TierRun(ctx context.Context, operationID string, options ...CallOption) (TierMove, error) {
	if err := optionalName("operation id", operationID, maxOperationBytes); err != nil {
		return TierMove{}, err
	}
	key, err := retryKey(operationID, options)
	if err != nil {
		return TierMove{}, err
	}
	ctx, err = c.outgoing(ctx, true, key)
	if err != nil {
		return TierMove{}, err
	}
	response, err := c.tier.Run(ctx, &v1.TierRunRequest{})
	if err != nil {
		return TierMove{}, interpret(err)
	}
	return tierMoveFrom(response.GetReceipt()), nil
}

// TierPause pauses the tier mover; the return is the resulting pause
// state.
func (c *Client) TierPause(ctx context.Context, operationID string, options ...CallOption) (bool, error) {
	if err := optionalName("operation id", operationID, maxOperationBytes); err != nil {
		return false, err
	}
	key, err := retryKey(operationID, options)
	if err != nil {
		return false, err
	}
	ctx, err = c.outgoing(ctx, true, key)
	if err != nil {
		return false, err
	}
	response, err := c.tier.Pause(ctx, &v1.TierPauseRequest{})
	if err != nil {
		return false, interpret(err)
	}
	return response.GetPaused(), nil
}

// TierResume resumes the tier mover.
func (c *Client) TierResume(ctx context.Context, operationID string, options ...CallOption) (bool, error) {
	if err := optionalName("operation id", operationID, maxOperationBytes); err != nil {
		return false, err
	}
	key, err := retryKey(operationID, options)
	if err != nil {
		return false, err
	}
	ctx, err = c.outgoing(ctx, true, key)
	if err != nil {
		return false, err
	}
	response, err := c.tier.Resume(ctx, &v1.TierResumeRequest{})
	if err != nil {
		return false, interpret(err)
	}
	return response.GetPaused(), nil
}
